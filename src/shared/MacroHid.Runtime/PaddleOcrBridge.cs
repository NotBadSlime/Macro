using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MacroHid.Core;

namespace MacroHid.Runtime;

public sealed record OcrTextBox(string Text, int Left, int Top, int Width, int Height)
{
    public int CenterX => Left + Width / 2;
    public int CenterY => Top + Height / 2;
}

public sealed record OcrTextLocation(string Text, int X, int Y, OcrTextBox Bounds);

public sealed record OcrRecognitionResult(
    bool IsAvailable,
    bool Success,
    string BackendName,
    string Text,
    string? Error,
    IReadOnlyList<OcrTextBox>? Boxes = null);

public sealed class PaddleOcrBridge : IDisposable
{
    private Process? ocrProcess;
    private readonly string executablePath;
    private readonly string modelDir;
    private readonly string backendName;
    private readonly ConcurrentQueue<OcrRequest> requestQueue = new();
    private readonly SemaphoreSlim processLock = new(1, 1);
    private volatile bool disposed;

    public static OcrRecognitionResult LastRecognition { get; private set; } =
        new(false, false, "Unavailable", string.Empty, "backend missing");

    public static event EventHandler<OcrRecognitionResult>? RecognitionCompleted;

    public PaddleOcrBridge(string? executablePath = null, string? modelDir = null)
    {
        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        this.executablePath = ResolveExecutablePath(appDir, executablePath);
        this.modelDir = modelDir ?? Path.Combine(appDir, "paddleocr", "models");
        backendName = Path.GetFileNameWithoutExtension(this.executablePath);
    }

    public bool IsAvailable => File.Exists(executablePath);

    public string BackendName => IsAvailable ? backendName : "Unavailable";

    public string StatusText => IsAvailable
        ? $"OCR 后端可用: {backendName}"
        : "OCR 后端不可用: 未找到 paddleocr\\ppocr_server.exe 或 WindowsOcrServer.exe";

    public static string DefaultStatusText
    {
        get
        {
            using var bridge = new PaddleOcrBridge();
            return bridge.StatusText;
        }
    }

    private static OcrRecognitionResult PublishRecognition(OcrRecognitionResult result)
    {
        LastRecognition = result;
        RecognitionCompleted?.Invoke(null, result);
        return result;
    }

    public async Task<string> RecognizeTextAsync(ScreenRegion region, string language = "ch", CancellationToken cancellationToken = default)
    {
        var result = await RecognizeWithDiagnosticsAsync(region, language, cancellationToken);
        return result.Text;
    }

    public async Task<OcrRecognitionResult> RecognizeWithDiagnosticsAsync(
        ScreenRegion region,
        string language = "ch",
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            return PublishRecognition(new OcrRecognitionResult(
                false,
                false,
                "Unavailable",
                string.Empty,
                "backend missing: WindowsOcrServer.exe or paddleocr\\ppocr_server.exe was not found"));
        }

        var pixels = ScreenCaptureService.CaptureRegion(region);
        if (pixels == null || pixels.Length == 0)
        {
            return PublishRecognition(new OcrRecognitionResult(
                true,
                false,
                BackendName,
                string.Empty,
                "capture empty"));
        }

        if (ScreenCaptureService.LooksBlankOrUniform(pixels, region.Width, region.Height))
        {
            return PublishRecognition(new OcrRecognitionResult(
                true,
                false,
                BackendName,
                string.Empty,
                "capture blank (use borderless/windowed mode; exclusive fullscreen often blocks OCR)"));
        }

        try
        {
            await EnsureProcessRunning(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return PublishRecognition(new OcrRecognitionResult(
                true,
                false,
                BackendName,
                string.Empty,
                $"startup failed: {ex.Message}"));
        }

        if (ocrProcess == null || ocrProcess.HasExited)
        {
            return PublishRecognition(new OcrRecognitionResult(
                true,
                false,
                BackendName,
                string.Empty,
                "startup failed: OCR backend exited before it became ready"));
        }

        var request = new OcrRequest
        {
            Width = region.Width,
            Height = region.Height,
            Pixels = Convert.ToBase64String(pixels),
            Language = language
        };

        var json = JsonSerializer.Serialize(request);
        OcrServerResponse response;
        try
        {
            response = await SendRequestDetailedAsync(json, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return PublishRecognition(new OcrRecognitionResult(
                true,
                false,
                BackendName,
                string.Empty,
                $"request failed: {ex.Message}"));
        }

        if (!string.IsNullOrWhiteSpace(response.Error))
        {
            return PublishRecognition(new OcrRecognitionResult(
                true,
                false,
                BackendName,
                response.Text,
                response.Error,
                response.Boxes));
        }

        if (string.IsNullOrWhiteSpace(response.Text))
        {
            var hint = Math.Max(region.Width, region.Height) < 120
                ? "recognition empty (tiny/single-glyph regions often fail on Windows OCR; use pixel color or a larger text region)"
                : "recognition empty";
            return PublishRecognition(new OcrRecognitionResult(
                true,
                false,
                BackendName,
                string.Empty,
                hint));
        }

        return PublishRecognition(new OcrRecognitionResult(
            true,
            true,
            BackendName,
            response.Text,
            null,
            response.Boxes));
    }

    public bool ContainsText(
        ScreenRegion region,
        string expectedText,
        bool contains = true,
        string language = "ch",
        bool useRegex = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = RecognizeWithDiagnosticsAsync(region, language, cancellationToken).GetAwaiter().GetResult();
            if (string.IsNullOrEmpty(result.Text)) return false;
            return TextMatches(result.Text, expectedText, contains, useRegex);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    public async Task<OcrTextLocation?> FindTextAsync(
        ScreenRegion region,
        string expectedText,
        bool contains = true,
        string language = "ch",
        bool useRegex = false,
        int matchIndex = 1,
        int offsetX = 0,
        int offsetY = 0,
        CancellationToken cancellationToken = default)
    {
        var result = await RecognizeWithDiagnosticsAsync(region, language, cancellationToken);
        if (!result.Success || result.Boxes is not { Count: > 0 }) return null;

        return FindTextInBoxes(
            region,
            result.Boxes,
            expectedText,
            contains,
            useRegex,
            matchIndex,
            offsetX,
            offsetY);
    }

    public static OcrTextLocation? FindTextInBoxes(
        ScreenRegion region,
        IReadOnlyList<OcrTextBox> boxes,
        string expectedText,
        bool contains = true,
        bool useRegex = false,
        int matchIndex = 1,
        int offsetX = 0,
        int offsetY = 0)
    {
        var candidates = boxes
            .Where(box => TextMatches(box.Text, expectedText, contains, useRegex))
            .OrderBy(box => box.Top)
            .ThenBy(box => box.Left)
            .ThenBy(box => box.Width * box.Height)
            .ToList();
        if (candidates.Count == 0 || matchIndex < 1 || matchIndex > candidates.Count) return null;

        var index = matchIndex - 1;
        var match = candidates[index];
        return new OcrTextLocation(
            match.Text,
            region.TopLeft.X + match.CenterX + offsetX,
            region.TopLeft.Y + match.CenterY + offsetY,
            match);
    }

    public static bool TextMatches(
        string recognizedText,
        string expectedText,
        bool contains = true,
        bool useRegex = false)
    {
        if (string.IsNullOrWhiteSpace(recognizedText) || string.IsNullOrWhiteSpace(expectedText))
            return false;

        if (useRegex)
        {
            return RegexMatches(recognizedText, expectedText)
                || RegexMatches(NormalizeOcrText(recognizedText), expectedText);
        }

        if (contains
            ? recognizedText.Contains(expectedText, StringComparison.OrdinalIgnoreCase)
            : recognizedText.Equals(expectedText, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var recognized = NormalizeOcrText(recognizedText);
        var expected = NormalizeOcrText(expectedText);
        if (recognized.Length == 0 || expected.Length == 0)
            return false;

        if (contains && recognized.Contains(expected, StringComparison.OrdinalIgnoreCase))
            return true;

        if (expected.Length <= 4)
        {
            return CountSharedCharacters(recognized, expected) >= Math.Max(1, expected.Length - 1);
        }

        return Similarity(recognized, expected) >= 0.78;
    }

    public static bool IsValidRegex(string pattern, out string? error)
    {
        try
        {
            _ = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            error = null;
            return true;
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool TryExtractText(
        string recognizedText,
        string pattern,
        bool useRegex,
        int matchIndex,
        int captureGroup,
        bool normalizeWhitespace,
        out string value,
        out string? error)
    {
        return TryExtractText(
            recognizedText,
            pattern,
            useRegex,
            matchIndex,
            captureGroup,
            filterTerms: string.Empty,
            keepDigitsOnly: false,
            normalizeWhitespace,
            out value,
            out error);
    }

    public static bool TryExtractText(
        string recognizedText,
        string pattern,
        bool useRegex,
        int matchIndex,
        int captureGroup,
        string filterTerms,
        bool keepDigitsOnly,
        bool normalizeWhitespace,
        out string value,
        out string? error)
    {
        value = string.Empty;
        error = null;
        if (string.IsNullOrWhiteSpace(recognizedText))
        {
            error = "OCR did not recognize any text.";
            return false;
        }

        var source = ApplyLiteralFilters(recognizedText.Trim(), filterTerms);
        if (string.IsNullOrWhiteSpace(source))
        {
            error = "No text remained after applying the filters.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(pattern))
        {
            value = source;
            return FinalizeExtractedText(ref value, keepDigitsOnly, out error);
        }

        if (matchIndex < 1)
        {
            error = "Match index must be at least 1.";
            return false;
        }

        if (captureGroup < 0)
        {
            error = "Capture group cannot be negative.";
            return false;
        }

        if (!useRegex)
        {
            var searchStart = 0;
            for (var index = 1; index <= matchIndex; index++)
            {
                var found = source.IndexOf(pattern, searchStart, StringComparison.OrdinalIgnoreCase);
                if (found < 0)
                {
                    error = $"Literal match {matchIndex} was not found.";
                    return false;
                }

                if (index == matchIndex)
                {
                    value = source.Substring(found, pattern.Length);
                    return FinalizeExtractedText(ref value, keepDigitsOnly, out error);
                }

                searchStart = found + Math.Max(1, pattern.Length);
            }

            error = $"Literal match {matchIndex} was not found.";
            return false;
        }

        if (!IsValidRegex(pattern, out error))
        {
            return false;
        }

        if (TryExtractRegex(source, pattern, matchIndex, captureGroup, out value, out error))
        {
            return FinalizeExtractedText(ref value, keepDigitsOnly, out error);
        }

        if (!normalizeWhitespace)
        {
            return false;
        }

        var compactSource = RemoveWhitespace(source);
        if (string.Equals(compactSource, source, StringComparison.Ordinal)
            || !TryExtractRegex(compactSource, pattern, matchIndex, captureGroup, out value, out error))
        {
            return false;
        }

        return FinalizeExtractedText(ref value, keepDigitsOnly, out error);
    }

    private static string ApplyLiteralFilters(string value, string filterTerms)
    {
        if (string.IsNullOrWhiteSpace(filterTerms))
        {
            return value;
        }

        var result = value;
        var terms = filterTerms
            .Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static term => term.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(static term => term.Length);
        foreach (var term in terms)
        {
            result = result.Replace(term, string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        return result.Trim();
    }

    private static bool FinalizeExtractedText(
        ref string value,
        bool keepDigitsOnly,
        out string? error)
    {
        value = value.Trim();
        if (keepDigitsOnly)
        {
            value = new string(value.Where(static ch => ch is >= '0' and <= '9').ToArray());
        }

        if (value.Length == 0)
        {
            error = keepDigitsOnly
                ? "No digits remained after applying the filters."
                : "The extracted text was empty.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryExtractRegex(
        string source,
        string pattern,
        int matchIndex,
        int captureGroup,
        out string value,
        out string? error)
    {
        value = string.Empty;
        error = null;
        try
        {
            var matches = Regex.Matches(
                source,
                pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100));
            if (matchIndex > matches.Count)
            {
                error = $"Regex match {matchIndex} was not found.";
                return false;
            }

            var match = matches[matchIndex - 1];
            if (captureGroup >= match.Groups.Count)
            {
                error = $"Capture group {captureGroup} does not exist; the pattern has {match.Groups.Count - 1} capture group(s).";
                return false;
            }

            var group = match.Groups[captureGroup];
            if (!group.Success || string.IsNullOrWhiteSpace(group.Value))
            {
                error = $"Capture group {captureGroup} did not contain text.";
                return false;
            }

            value = group.Value.Trim();
            return true;
        }
        catch (RegexMatchTimeoutException)
        {
            error = "Regex matching timed out.";
            return false;
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string RemoveWhitespace(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (!char.IsWhiteSpace(ch))
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }

    private static bool RegexMatches(string input, string pattern)
    {
        try
        {
            return Regex.IsMatch(
                input,
                pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100));
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private async Task EnsureProcessRunning(CancellationToken cancellationToken)
    {
        if (ocrProcess != null && !ocrProcess.HasExited)
            return;

        await processLock.WaitAsync(cancellationToken);
        try
        {
            if (ocrProcess != null && !ocrProcess.HasExited)
                return;

            ocrProcess?.Dispose();
            ocrProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executablePath,
                    Arguments = BuildArguments(),
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardInputEncoding = Encoding.UTF8
                },
                EnableRaisingEvents = true
            };

            ocrProcess.Start();

            var readyLine = await ocrProcess.StandardOutput.ReadLineAsync(cancellationToken);
            if (readyLine == null || !readyLine.Contains("ready"))
            {
                ocrProcess.Kill();
                ocrProcess = null;
            }
        }
        finally
        {
            processLock.Release();
        }
    }

    private static string ResolveExecutablePath(string appDir, string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return explicitPath;
        }

        var candidates = new[]
        {
            Path.Combine(appDir, "paddleocr", "ppocr_server.exe"),
            Path.Combine(appDir, "WindowsOcrServer.exe"),
            Path.Combine(appDir, "ocr", "WindowsOcrServer.exe")
        };

        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    private string BuildArguments()
    {
        return string.Equals(Path.GetFileName(executablePath), "ppocr_server.exe", StringComparison.OrdinalIgnoreCase)
            ? $"--model_dir \"{modelDir}\""
            : string.Empty;
    }

    private static string NormalizeOcrText(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch) || char.IsPunctuation(ch) || char.IsSymbol(ch))
                continue;

            builder.Append(char.ToUpperInvariant(ch));
        }

        return builder.ToString();
    }

    private static int CountSharedCharacters(string recognized, string expected)
    {
        var remaining = recognized.GroupBy(c => c).ToDictionary(g => g.Key, g => g.Count());
        var shared = 0;
        foreach (var ch in expected)
        {
            if (!remaining.TryGetValue(ch, out var count) || count <= 0)
                continue;

            remaining[ch] = count - 1;
            shared++;
        }

        return shared;
    }

    private static double Similarity(string recognized, string expected)
    {
        var distance = LevenshteinDistance(recognized, expected);
        return 1.0 - distance / (double)Math.Max(recognized.Length, expected.Length);
    }

    private static int LevenshteinDistance(string left, string right)
    {
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var j = 0; j <= right.Length; j++)
            previous[j] = j;

        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
            {
                var cost = left[i - 1] == right[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    private async Task<OcrServerResponse> SendRequestDetailedAsync(string json, CancellationToken cancellationToken)
    {
        if (ocrProcess == null || ocrProcess.HasExited)
            return new OcrServerResponse(string.Empty, "backend missing: OCR process is not running");

        await processLock.WaitAsync(cancellationToken);
        try
        {
            await ocrProcess.StandardInput.WriteLineAsync(json.AsMemory(), cancellationToken);
            await ocrProcess.StandardInput.FlushAsync();

            var response = await ocrProcess.StandardOutput.ReadLineAsync(cancellationToken);
            if (string.IsNullOrEmpty(response))
                return new OcrServerResponse(string.Empty, "recognition empty: OCR backend returned no response");

            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;
            var responseError = ReadStringProperty(root, "error");
            var responseText = ReadStringProperty(root, "text");
            var responseBoxes = ReadTextBoxes(root);
            if (!string.IsNullOrWhiteSpace(responseText))
                return new OcrServerResponse(responseText, responseError, responseBoxes);

            if (root.TryGetProperty("text", out var textProp))
                return new OcrServerResponse(textProp.GetString() ?? string.Empty, responseError, responseBoxes);

            if (root.TryGetProperty("results", out var resultsProp) && resultsProp.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                foreach (var item in resultsProp.EnumerateArray())
                {
                    var itemTextValue = ReadStringProperty(item, "text");
                    if (!string.IsNullOrWhiteSpace(itemTextValue))
                    {
                        if (sb.Length > 0) sb.Append(' ');
                        sb.Append(itemTextValue);
                    }
                }
                return new OcrServerResponse(sb.ToString(), responseError, responseBoxes);
            }

            return new OcrServerResponse(string.Empty, responseError, responseBoxes);
        }
        finally
        {
            processLock.Release();
        }
    }

    private static string? ReadStringProperty(JsonElement element, string propertyName)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()
                    : property.Value.ToString();
            }
        }

        return null;
    }

    private static IReadOnlyList<OcrTextBox> ReadTextBoxes(JsonElement root)
    {
        if (!TryGetPropertyIgnoreCase(root, "boxes", out var boxesElement)
            || boxesElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var boxes = new List<OcrTextBox>();
        foreach (var item in boxesElement.EnumerateArray())
        {
            var text = ReadStringProperty(item, "text") ?? string.Empty;
            var left = ReadIntProperty(item, "left");
            var top = ReadIntProperty(item, "top");
            var width = ReadIntProperty(item, "width");
            var height = ReadIntProperty(item, "height");
            if (!string.IsNullOrWhiteSpace(text) && width > 0 && height > 0)
            {
                boxes.Add(new OcrTextBox(text, left, top, width, height));
            }
        }

        return boxes;
    }

    private static int ReadIntProperty(JsonElement element, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(element, propertyName, out var value)) return 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : int.TryParse(value.ToString(), out var parsed) ? parsed : 0;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase)) continue;
            value = property.Value;
            return true;
        }

        value = default;
        return false;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        if (ocrProcess != null && !ocrProcess.HasExited)
        {
            try
            {
                ocrProcess.StandardInput.WriteLine("{\"command\":\"exit\"}");
                ocrProcess.StandardInput.Flush();
                if (!ocrProcess.WaitForExit(3000))
                    ocrProcess.Kill();
            }
            catch
            {
                try { ocrProcess.Kill(); } catch { }
            }
            ocrProcess.Dispose();
        }

        processLock.Dispose();
    }

    private sealed class OcrRequest
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public string Pixels { get; set; } = string.Empty;
        public string Language { get; set; } = "ch";
    }

    private sealed record OcrServerResponse(
        string Text,
        string? Error,
        IReadOnlyList<OcrTextBox>? Boxes = null);
}
