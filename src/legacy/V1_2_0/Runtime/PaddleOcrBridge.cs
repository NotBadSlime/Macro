using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MacroHid.Core;

namespace MacroHid.Runtime;

public sealed record OcrRecognitionResult(
    bool IsAvailable,
    bool Success,
    string BackendName,
    string Text,
    string? Error);

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

        try
        {
            await EnsureProcessRunning(cancellationToken);
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
                response.Error));
        }

        if (string.IsNullOrWhiteSpace(response.Text))
        {
            return PublishRecognition(new OcrRecognitionResult(
                true,
                false,
                BackendName,
                string.Empty,
                "recognition empty"));
        }

        return PublishRecognition(new OcrRecognitionResult(
            true,
            true,
            BackendName,
            response.Text,
            null));
    }

    public bool ContainsText(ScreenRegion region, string expectedText, bool contains = true, string language = "ch")
    {
        try
        {
            var result = RecognizeWithDiagnosticsAsync(region, language, CancellationToken.None).GetAwaiter().GetResult();
            if (string.IsNullOrEmpty(result.Text)) return false;
            return TextMatches(result.Text, expectedText, contains);
        }
        catch
        {
            return false;
        }
    }

    public static bool TextMatches(string recognizedText, string expectedText, bool contains = true)
    {
        if (string.IsNullOrWhiteSpace(recognizedText) || string.IsNullOrWhiteSpace(expectedText))
            return false;

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
            if (!string.IsNullOrWhiteSpace(responseText))
                return new OcrServerResponse(responseText, responseError);

            if (root.TryGetProperty("text", out var textProp))
                return new OcrServerResponse(textProp.GetString() ?? string.Empty, responseError);

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
                return new OcrServerResponse(sb.ToString(), responseError);
            }

            return new OcrServerResponse(string.Empty, responseError);
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

    private sealed record OcrServerResponse(string Text, string? Error);
}
