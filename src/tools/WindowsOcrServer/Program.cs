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
                var text = result.Text?.Trim() ?? string.Empty;
                if (text.Length > bestText.Length)
                {
                    bestText = text;
                }
            }
        }

        WriteResponse(bestText, null);
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

static IReadOnlyList<OcrImage> PrepareOcrCandidates(byte[] source, int width, int height)
{
    return
    [
        PrepareOcrPixels(source, width, height, OcrPixelMode.Preserve, out var preserveWidth, out var preserveHeight)
            .ToImage(preserveWidth, preserveHeight),
        PrepareOcrPixels(source, width, height, OcrPixelMode.Grayscale, out var grayscaleWidth, out var grayscaleHeight)
            .ToImage(grayscaleWidth, grayscaleHeight),
        PrepareOcrPixels(source, width, height, OcrPixelMode.Threshold, out var thresholdWidth, out var thresholdHeight)
            .ToImage(thresholdWidth, thresholdHeight),
        PrepareOcrPixels(source, width, height, OcrPixelMode.AutoInvertThreshold, out var invertWidth, out var invertHeight)
            .ToImage(invertWidth, invertHeight)
    ];
}

static byte[] PrepareOcrPixels(byte[] source, int width, int height, OcrPixelMode mode, out int preparedWidth, out int preparedHeight)
{
    const int BytesPerPixel = 4;
    const int Padding = 24;

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
    var scale = maxDimension < 320 ? 3 : maxDimension < 900 ? 2 : 1;
    preparedWidth = checked(width * scale + Padding * 2);
    preparedHeight = checked(height * scale + Padding * 2);
    var shouldInvert = mode == OcrPixelMode.AutoInvertThreshold && EstimateAverageLuma(source, width, height) < 128;

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
            else
            {
                var value = (byte)(luma < 210 ? 0 : 255);
                if (shouldInvert)
                {
                    value = (byte)(255 - value);
                }

                targetB = value;
                targetG = value;
                targetR = value;
            }

            var targetX = Padding + x * scale;
            var targetY = Padding + y * scale;
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

static int EstimateAverageLuma(byte[] source, int width, int height)
{
    const int BytesPerPixel = 4;
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
            total += (r * 299 + g * 587 + b * 114) / 1000;
            samples++;
        }
    }

    return samples == 0 ? 255 : (int)(total / samples);
}

static void WriteResponse(string text, string? error)
{
    var response = JsonSerializer.Serialize(
        new OcrResponse(text, error),
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

public sealed record OcrResponse(string Text, string? Error);

internal enum OcrPixelMode
{
    Preserve,
    Grayscale,
    Threshold,
    AutoInvertThreshold
}

internal sealed record OcrImage(byte[] Pixels, int Width, int Height);

internal static class OcrImageExtensions
{
    public static OcrImage ToImage(this byte[] pixels, int width, int height) => new(pixels, width, height);
}

[JsonSerializable(typeof(OcrRequest))]
[JsonSerializable(typeof(OcrResponse))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal sealed partial class JsonContext : JsonSerializerContext;
