using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = Encoding.UTF8;

var engineCache = new Dictionary<string, OcrEngine?>(StringComparer.OrdinalIgnoreCase);
Console.WriteLine("ready");
Console.Out.Flush();

while (await Console.In.ReadLineAsync() is { } line)
{
    line = NormalizeInputLine(line);
    if (string.IsNullOrWhiteSpace(line))
    {
        continue;
    }

    try
    {
        using var document = JsonDocument.Parse(line);
        if (document.RootElement.TryGetProperty("command", out var command)
            && string.Equals(command.GetString(), "exit", StringComparison.OrdinalIgnoreCase))
        {
            break;
        }

        var request = JsonSerializer.Deserialize<OcrRequest>(line, JsonContext.Default.OcrRequest);
        if (request is null || request.Width <= 0 || request.Height <= 0 || string.IsNullOrWhiteSpace(request.Pixels))
        {
            WriteResponse(string.Empty, "invalid request");
            continue;
        }

        var engines = GetEngines(request.Language, engineCache);
        if (engines.Count == 0)
        {
            WriteResponse(string.Empty, "windows OCR engine unavailable");
            continue;
        }

        var pixels = Convert.FromBase64String(request.Pixels);
        var candidates = PrepareOcrCandidates(pixels, request.Width, request.Height);
        var bestText = string.Empty;
        var bestScore = int.MinValue;
        IReadOnlyList<OcrTextBoxResponse> bestBoxes = [];

        foreach (var engine in engines)
        {
            foreach (var candidate in candidates)
            {
                using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
                    candidate.Pixels.AsBuffer(),
                    BitmapPixelFormat.Bgra8,
                    candidate.Width,
                    candidate.Height,
                    BitmapAlphaMode.Ignore);

                var result = await engine.RecognizeAsync(bitmap);
                var text = BuildRecognizedText(result);
                var score = ScoreOcrText(text);
                if (score > bestScore || (score == bestScore && text.Length > bestText.Length))
                {
                    bestScore = score;
                    bestText = text;
                    bestBoxes = BuildOcrBoxes(result, candidate, request.Width, request.Height);
                }
            }
        }

        WriteResponse(bestText, null, bestBoxes);
    }
    catch (Exception ex)
    {
        WriteResponse(string.Empty, ex.Message);
    }
}

static IReadOnlyList<OcrEngine> GetEngines(string? language, Dictionary<string, OcrEngine?> cache)
{
    var requested = NormalizeLanguage(language);
    var languageTags = new List<string> { requested };

    var profileEngine = OcrEngine.TryCreateFromUserProfileLanguages();
    if (profileEngine is not null)
    {
        languageTags.Add(profileEngine.RecognizerLanguage.LanguageTag);
    }

    if (requested.StartsWith("zh-", StringComparison.OrdinalIgnoreCase))
    {
        languageTags.Add("zh-Hans-CN");
        languageTags.Add("zh-Hant-TW");
    }

    languageTags.Add("en-US");

    var engines = new List<OcrEngine>();
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var tag in languageTags)
    {
        if (seen.Add(tag) && GetEngine(tag, cache) is { } engine)
        {
            engines.Add(engine);
        }
    }

    return engines;
}

static OcrEngine? GetEngine(string? language, Dictionary<string, OcrEngine?> cache)
{
    var normalized = NormalizeLanguage(language);
    if (cache.TryGetValue(normalized, out var cached))
    {
        return cached;
    }

    OcrEngine? engine = null;
    try
    {
        engine = OcrEngine.TryCreateFromLanguage(new Language(normalized));
    }
    catch
    {
        // Fall back below.
    }

    engine ??= OcrEngine.TryCreateFromUserProfileLanguages();
    cache[normalized] = engine;
    return engine;
}

static string NormalizeLanguage(string? language)
{
    var value = (language ?? string.Empty).Trim();
    return value.ToLowerInvariant() switch
    {
        "" or "ch" or "cn" or "zh" or "zh-cn" or "zh-hans" => "zh-Hans-CN",
        "zh-tw" or "zh-hant" or "tc" => "zh-Hant-TW",
        "en" or "en-us" => "en-US",
        _ => value
    };
}

static string NormalizeInputLine(string line)
{
    var value = line.TrimStart('\uFEFF');
    return value.StartsWith("ï»¿", StringComparison.Ordinal) ? value[3..] : value;
}

static string BuildRecognizedText(OcrResult result)
{
    if (result.Lines.Count == 0)
    {
        return result.Text?.Trim() ?? string.Empty;
    }

    return string.Join(
        ' ',
        result.Lines
            .Select(line => line.Text?.Trim())
            .Where(text => !string.IsNullOrWhiteSpace(text)));
}

static int ScoreOcrText(string text)
{
    if (string.IsNullOrWhiteSpace(text))
    {
        return int.MinValue;
    }

    var trimmed = text.Trim();
    var lettersOrDigits = 0;
    var other = 0;
    foreach (var ch in trimmed)
    {
        if (char.IsLetterOrDigit(ch))
        {
            lettersOrDigits++;
        }
        else if (!char.IsWhiteSpace(ch))
        {
            other++;
        }
    }

    // Prefer recognizable glyphs over longer punctuation/noise from the wrong language engine.
    return (lettersOrDigits * 20) + trimmed.Length - (other * 8);
}

static IReadOnlyList<OcrImage> PrepareOcrCandidates(byte[] source, int width, int height)
{
    var candidates = new List<OcrImage>
    {
        PrepareOcrPixels(source, width, height, OcrPixelMode.Preserve, out var preserveWidth, out var preserveHeight)
            .ToImage(preserveWidth, preserveHeight, width, height),
        PrepareOcrPixels(source, width, height, OcrPixelMode.Grayscale, out var grayscaleWidth, out var grayscaleHeight)
            .ToImage(grayscaleWidth, grayscaleHeight, width, height),
        PrepareOcrPixels(source, width, height, OcrPixelMode.ContrastStretch, out var stretchWidth, out var stretchHeight)
            .ToImage(stretchWidth, stretchHeight, width, height),
        PrepareOcrPixels(source, width, height, OcrPixelMode.Threshold, out var thresholdWidth, out var thresholdHeight)
            .ToImage(thresholdWidth, thresholdHeight, width, height),
        PrepareOcrPixels(source, width, height, OcrPixelMode.AutoInvertThreshold, out var invertWidth, out var invertHeight)
            .ToImage(invertWidth, invertHeight, width, height)
    };

    // Isolated HUD glyphs (often <80px) rarely OCR alone; repeat them as a short "word".
    if (Math.Max(width, height) < 120)
    {
        candidates.Add(PrepareGlyphBanner(source, width, height, invert: false));
        candidates.Add(PrepareGlyphBanner(source, width, height, invert: true));
    }

    return candidates;
}

static OcrImage PrepareGlyphBanner(byte[] source, int width, int height, bool invert)
{
    const int BytesPerPixel = 4;
    const int Copies = 3;
    const int Gap = 28;
    const int Margin = 48;

    var glyph = PrepareOcrPixels(
        source,
        width,
        height,
        invert ? OcrPixelMode.AutoInvertThreshold : OcrPixelMode.Threshold,
        out var glyphWidth,
        out var glyphHeight);

    // Strip the white padding already added by PrepareOcrPixels so we can re-layout tightly.
    var maxDimension = Math.Max(width, height);
    var innerPadding = maxDimension < 80 ? 40 : 24;
    var contentWidth = Math.Max(1, glyphWidth - innerPadding * 2);
    var contentHeight = Math.Max(1, glyphHeight - innerPadding * 2);

    var bannerWidth = Margin * 2 + Copies * contentWidth + (Copies - 1) * Gap;
    var bannerHeight = Margin * 2 + contentHeight;
    var output = new byte[bannerWidth * bannerHeight * BytesPerPixel];
    for (var i = 0; i < output.Length; i += BytesPerPixel)
    {
        output[i] = 255;
        output[i + 1] = 255;
        output[i + 2] = 255;
        output[i + 3] = 255;
    }

    for (var copy = 0; copy < Copies; copy++)
    {
        var destX = Margin + copy * (contentWidth + Gap);
        var destY = Margin;
        for (var y = 0; y < contentHeight; y++)
        {
            for (var x = 0; x < contentWidth; x++)
            {
                var srcIndex = (((y + innerPadding) * glyphWidth) + (x + innerPadding)) * BytesPerPixel;
                var dstIndex = (((destY + y) * bannerWidth) + (destX + x)) * BytesPerPixel;
                output[dstIndex] = glyph[srcIndex];
                output[dstIndex + 1] = glyph[srcIndex + 1];
                output[dstIndex + 2] = glyph[srcIndex + 2];
                output[dstIndex + 3] = 255;
            }
        }
    }

    // Scale metadata maps banner hits back roughly to the original crop center.
    return new OcrImage(output, bannerWidth, bannerHeight, Math.Max(1, contentWidth / Math.Max(1, width)), Margin);
}

static IReadOnlyList<OcrTextBoxResponse> BuildOcrBoxes(
    OcrResult result,
    OcrImage image,
    int sourceWidth,
    int sourceHeight)
{
    var boxes = new List<OcrTextBoxResponse>();
    foreach (var line in result.Lines)
    {
        var words = line.Words.ToList();
        if (words.Count == 0) continue;

        AddBox(line.Text, words);
        foreach (var word in words)
        {
            AddBox(word.Text, [word]);
        }
    }

    return boxes
        .DistinctBy(box => $"{box.Text}\n{box.Left},{box.Top},{box.Width},{box.Height}", StringComparer.OrdinalIgnoreCase)
        .ToList();

    void AddBox(string? text, IReadOnlyList<OcrWord> words)
    {
        if (string.IsNullOrWhiteSpace(text) || words.Count == 0) return;
        var left = words.Min(word => word.BoundingRect.X);
        var top = words.Min(word => word.BoundingRect.Y);
        var right = words.Max(word => word.BoundingRect.X + word.BoundingRect.Width);
        var bottom = words.Max(word => word.BoundingRect.Y + word.BoundingRect.Height);

        var sourceLeft = Math.Clamp((int)Math.Floor((left - image.Padding) / image.Scale), 0, sourceWidth - 1);
        var sourceTop = Math.Clamp((int)Math.Floor((top - image.Padding) / image.Scale), 0, sourceHeight - 1);
        var sourceRight = Math.Clamp((int)Math.Ceiling((right - image.Padding) / image.Scale), sourceLeft + 1, sourceWidth);
        var sourceBottom = Math.Clamp((int)Math.Ceiling((bottom - image.Padding) / image.Scale), sourceTop + 1, sourceHeight);
        boxes.Add(new OcrTextBoxResponse(
            text.Trim(),
            sourceLeft,
            sourceTop,
            sourceRight - sourceLeft,
            sourceBottom - sourceTop));
    }
}

static byte[] PrepareOcrPixels(byte[] source, int width, int height, OcrPixelMode mode, out int preparedWidth, out int preparedHeight)
{
    const int BytesPerPixel = 4;

    if (width <= 0 || height <= 0)
    {
        preparedWidth = 1;
        preparedHeight = 1;
        return [255, 255, 255, 255];
    }

    var requiredLength = checked(width * height * BytesPerPixel);
    if (source.Length < requiredLength)
    {
        throw new InvalidOperationException("pixel buffer is smaller than width*height*4");
    }

    var maxDimension = Math.Max(width, height);
    // Tiny game HUD glyphs need heavy upscaling before Windows OCR will emit a character.
    var scale = maxDimension switch
    {
        < 48 => 10,
        < 80 => 8,
        < 160 => 6,
        < 320 => 4,
        < 900 => 2,
        _ => 1
    };
    var padding = maxDimension < 80 ? 40 : 24;
    preparedWidth = checked(width * scale + padding * 2);
    preparedHeight = checked(height * scale + padding * 2);

    var (minLuma, maxLuma, averageLuma) = EstimateLumaRange(source, width, height);
    var otsuThreshold = EstimateOtsuThreshold(source, width, height);
    var shouldInvert = mode == OcrPixelMode.AutoInvertThreshold && averageLuma < 128;
    var stretchSpan = Math.Max(1, maxLuma - minLuma);

    var output = new byte[preparedWidth * preparedHeight * BytesPerPixel];
    for (var i = 0; i < output.Length; i += BytesPerPixel)
    {
        output[i] = 255;
        output[i + 1] = 255;
        output[i + 2] = 255;
        output[i + 3] = 255;
    }

    for (var y = 0; y < height; y++)
    {
        for (var x = 0; x < width; x++)
        {
            var sourceIndex = ((y * width) + x) * BytesPerPixel;
            var b = source[sourceIndex];
            var g = source[sourceIndex + 1];
            var r = source[sourceIndex + 2];
            var a = source[sourceIndex + 3];

            if (a < 255)
            {
                b = (byte)((b * a + 255 * (255 - a)) / 255);
                g = (byte)((g * a + 255 * (255 - a)) / 255);
                r = (byte)((r * a + 255 * (255 - a)) / 255);
            }

            var luma = (r * 299 + g * 587 + b * 114) / 1000;
            byte targetB;
            byte targetG;
            byte targetR;
            if (mode == OcrPixelMode.Preserve)
            {
                targetB = b;
                targetG = g;
                targetR = r;
            }
            else if (mode == OcrPixelMode.Grayscale)
            {
                var value = (byte)luma;
                targetB = value;
                targetG = value;
                targetR = value;
            }
            else if (mode == OcrPixelMode.ContrastStretch)
            {
                var stretched = (byte)Math.Clamp(((luma - minLuma) * 255) / stretchSpan, 0, 255);
                targetB = stretched;
                targetG = stretched;
                targetR = stretched;
            }
            else
            {
                // Prefer Otsu for soft/antialiased game fonts; fall back near old fixed cutoff.
                var cutoff = stretchSpan < 40 ? 210 : otsuThreshold;
                var value = (byte)(luma < cutoff ? 0 : 255);
                if (shouldInvert)
                {
                    value = (byte)(255 - value);
                }

                targetB = value;
                targetG = value;
                targetR = value;
            }

            var targetX = padding + x * scale;
            var targetY = padding + y * scale;
            for (var sy = 0; sy < scale; sy++)
            {
                for (var sx = 0; sx < scale; sx++)
                {
                    var targetIndex = (((targetY + sy) * preparedWidth) + targetX + sx) * BytesPerPixel;
                    output[targetIndex] = targetB;
                    output[targetIndex + 1] = targetG;
                    output[targetIndex + 2] = targetR;
                    output[targetIndex + 3] = 255;
                }
            }
        }
    }

    return output;
}

static (int Min, int Max, int Average) EstimateLumaRange(byte[] source, int width, int height)
{
    const int BytesPerPixel = 4;
    var min = 255;
    var max = 0;
    long total = 0;
    var samples = 0;
    var stepX = Math.Max(1, width / 32);
    var stepY = Math.Max(1, height / 32);
    for (var y = 0; y < height; y += stepY)
    {
        for (var x = 0; x < width; x += stepX)
        {
            var index = ((y * width) + x) * BytesPerPixel;
            var b = source[index];
            var g = source[index + 1];
            var r = source[index + 2];
            var luma = (r * 299 + g * 587 + b * 114) / 1000;
            if (luma < min) min = luma;
            if (luma > max) max = luma;
            total += luma;
            samples++;
        }
    }

    return samples == 0 ? (0, 255, 255) : (min, max, (int)(total / samples));
}

static int EstimateOtsuThreshold(byte[] source, int width, int height)
{
    const int BytesPerPixel = 4;
    Span<int> histogram = stackalloc int[256];
    histogram.Clear();
    var stepX = Math.Max(1, width / 64);
    var stepY = Math.Max(1, height / 64);
    var total = 0;
    for (var y = 0; y < height; y += stepY)
    {
        for (var x = 0; x < width; x += stepX)
        {
            var index = ((y * width) + x) * BytesPerPixel;
            var b = source[index];
            var g = source[index + 1];
            var r = source[index + 2];
            var luma = (r * 299 + g * 587 + b * 114) / 1000;
            histogram[luma]++;
            total++;
        }
    }

    if (total == 0)
    {
        return 210;
    }

    var sumAll = 0L;
    for (var i = 0; i < 256; i++)
    {
        sumAll += i * histogram[i];
    }

    var sumBackground = 0L;
    var weightBackground = 0;
    var bestThreshold = 210;
    var bestVariance = -1.0;
    for (var threshold = 0; threshold < 256; threshold++)
    {
        weightBackground += histogram[threshold];
        if (weightBackground == 0)
        {
            continue;
        }

        var weightForeground = total - weightBackground;
        if (weightForeground == 0)
        {
            break;
        }

        sumBackground += threshold * histogram[threshold];
        var meanBackground = sumBackground / (double)weightBackground;
        var meanForeground = (sumAll - sumBackground) / (double)weightForeground;
        var between = weightBackground * (double)weightForeground * (meanBackground - meanForeground) * (meanBackground - meanForeground);
        if (between > bestVariance)
        {
            bestVariance = between;
            bestThreshold = threshold;
        }
    }

    return bestThreshold;
}

static void WriteResponse(string text, string? error, IReadOnlyList<OcrTextBoxResponse>? boxes = null)
{
    var response = JsonSerializer.Serialize(
        new OcrResponse(text, error, boxes ?? []),
        JsonContext.Default.OcrResponse);
    Console.WriteLine(response);
    Console.Out.Flush();
}

public sealed class OcrRequest
{
    public int Width { get; set; }
    public int Height { get; set; }
    public string Pixels { get; set; } = string.Empty;
    public string Language { get; set; } = "ch";
}

public sealed record OcrTextBoxResponse(string Text, int Left, int Top, int Width, int Height);

public sealed record OcrResponse(string Text, string? Error, IReadOnlyList<OcrTextBoxResponse> Boxes);

internal enum OcrPixelMode
{
    Preserve,
    Grayscale,
    ContrastStretch,
    Threshold,
    AutoInvertThreshold
}

internal sealed record OcrImage(byte[] Pixels, int Width, int Height, int Scale, int Padding);

internal static class OcrImageExtensions
{
    public static OcrImage ToImage(this byte[] pixels, int width, int height, int sourceWidth, int sourceHeight)
    {
        var maxDimension = Math.Max(sourceWidth, sourceHeight);
        var padding = maxDimension < 80 ? 40 : 24;
        var scale = Math.Max(1, Math.Min(
            (width - padding * 2) / Math.Max(1, sourceWidth),
            (height - padding * 2) / Math.Max(1, sourceHeight)));
        return new OcrImage(pixels, width, height, scale, padding);
    }
}

[JsonSerializable(typeof(OcrRequest))]
[JsonSerializable(typeof(OcrResponse))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal sealed partial class JsonContext : JsonSerializerContext;
