using MacroHid.Core;

namespace MacroHid.Runtime;

public static class TemplateMatchEngine
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<byte[], DecodedTemplate> Templates = new();
    private sealed record DecodedTemplate(RgbColor[]? Pixels, int Width, int Height, double[] Centered, double Variance);
    public static (int x, int y, double score) Match(ScreenRegion searchRegion, byte[] templatePngData, double threshold = 0.85,
        Func<ScreenRegion, RgbColor[]>? capture = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var searchPixels = capture is null ? ScreenCaptureService.CaptureFrame(searchRegion) : PixelFrame.FromRgb(capture(searchRegion));
        return Match(searchRegion, templatePngData, threshold, searchPixels, cancellationToken);
    }

    internal static (int x, int y, double score) Match(ScreenRegion searchRegion, byte[] templatePngData, double threshold,
        PixelFrame searchPixels, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (searchPixels.Count == 0)
            return (-1, -1, 0);

        var decoded = Templates.GetValue(templatePngData, data =>
        {
            var pixels = DecodePngToRgb(data, out var width, out var height);
            var luminance = pixels?.Select(Luma).ToArray() ?? [];
            var mean = luminance.Length == 0 ? 0 : luminance.Average();
            for (var i = 0; i < luminance.Length; i++) luminance[i] -= mean;
            return new(pixels, width, height, luminance, luminance.Sum(value => value * value));
        });
        var templatePixels = decoded.Pixels;
        var templateWidth = decoded.Width;
        var templateHeight = decoded.Height;
        if (templatePixels == null || templateWidth == 0 || templateHeight == 0)
            return (-1, -1, 0);

        var searchWidth = searchRegion.Width;
        var searchHeight = searchRegion.Height;

        if (templateWidth > searchWidth || templateHeight > searchHeight)
            return (-1, -1, 0);

        using var plane = new LuminancePlane(searchPixels, searchWidth, searchHeight, cancellationToken);
        bool legacy = Environment.GetEnvironmentVariable("MACROHID_LEGACY_IMAGES") == "1";
        var bestScore = 0.0;
        var bestX = -1;
        var bestY = -1;

        for (int sy = 0; sy <= searchHeight - templateHeight; sy++)
        {
            for (int sx = 0; sx <= searchWidth - templateWidth; sx++)
            {
                if ((sx & 31) == 0) cancellationToken.ThrowIfCancellationRequested();
                var score = ComputeNCC(plane, sx, sy, decoded, cancellationToken, legacy);
                // Integral sums have a different rounding order. Re-evaluate decision boundaries
                // using the original row-major sum order, including ties and the early-exit cutoff.
                if (!legacy && (Math.Abs(score - threshold) < 1e-7 || Math.Abs(score - bestScore) < 1e-7 || Math.Abs(score - .999) < 1e-7))
                    score = ComputeNCC(plane, sx, sy, decoded, cancellationToken, true);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestX = sx;
                    bestY = sy;
                    if (score >= 0.999) goto done;
                }
            }
        }

        done:
        return bestScore >= threshold ? (bestX, bestY, bestScore) : (-1, -1, bestScore);
    }

    public static bool Matches(ScreenRegion searchRegion, byte[] templatePngData, double threshold = 0.85, Func<ScreenRegion, RgbColor[]>? capture = null,
        CancellationToken cancellationToken = default)
    {
        var (x, _, score) = Match(searchRegion, templatePngData, threshold, capture, cancellationToken);
        return x >= 0 && score >= threshold;
    }

    private static double ComputeNCC(LuminancePlane search, int offsetX, int offsetY, DecodedTemplate template, CancellationToken token, bool original)
    {
        double sumST = 0, sumS = 0, sumSquare = 0;
        var count = template.Centered.Length;
        for (int ty = 0; ty < template.Height; ty++)
        {
            if ((ty & 31) == 0) token.ThrowIfCancellationRequested();
            for (int tx = 0; tx < template.Width; tx++)
            {
                var value = search.Values[(offsetY + ty) * search.Width + offsetX + tx];
                sumST += value * template.Centered[ty * template.Width + tx];
                if (original) { sumS += value; sumSquare += value * value; }
            }
        }
        if (!original)
        {
            sumS = search.Window(search.Sums, offsetX, offsetY, template.Width, template.Height);
            sumSquare = search.Window(search.Squares, offsetX, offsetY, template.Width, template.Height);
        }
        var variance = Math.Max(0, sumSquare - sumS * sumS / count);
        if (!original && variance <= Math.Max(1e-6, sumSquare * 1e-6))
            return ComputeNCC(search, offsetX, offsetY, template, token, true);
        var denom = Math.Sqrt(variance * template.Variance);
        return denom < 1e-10 ? 0 : sumST / denom;
    }

    private sealed class LuminancePlane : IDisposable
    {
        internal readonly double[] Values, Sums, Squares;
        internal readonly int Width;
        internal LuminancePlane(PixelFrame frame, int width, int height, CancellationToken token)
        {
            Width = width;
            var pool = System.Buffers.ArrayPool<double>.Shared;
            Values = pool.Rent(checked(width * height));
            Sums = []; Squares = [];
            try
            {
                Sums = pool.Rent(checked((width + 1) * (height + 1)));
                Squares = pool.Rent(checked((width + 1) * (height + 1)));
                Array.Clear(Sums, 0, width + 1); Array.Clear(Squares, 0, width + 1);
                for (int y = 0; y < height; y++)
                {
                    token.ThrowIfCancellationRequested();
                    double row = 0, rowSquare = 0;
                    int start = (y + 1) * (width + 1);
                    Sums[start] = Squares[start] = 0;
                    for (int x = 0; x < width; x++)
                    {
                        double value = frame.Luma(y * width + x);
                        Values[y * width + x] = value;
                        row += value; rowSquare += value * value;
                        Sums[start + x + 1] = Sums[start - width + x] + row;
                        Squares[start + x + 1] = Squares[start - width + x] + rowSquare;
                    }
                }
            }
            catch { Dispose(); throw; }
        }
        internal double Window(double[] table, int x, int y, int w, int h)
        {
            int top = y * (Width + 1), bottom = (y + h) * (Width + 1);
            return table[bottom + x + w] - table[bottom + x] - table[top + x + w] + table[top + x];
        }
        public void Dispose()
        {
            var pool = System.Buffers.ArrayPool<double>.Shared;
            pool.Return(Values); if (Sums.Length != 0) pool.Return(Sums); if (Squares.Length != 0) pool.Return(Squares);
        }
    }

    private static double Luma(RgbColor c) => c.R * 0.299 + c.G * 0.587 + c.B * 0.114;

    private static RgbColor[]? DecodePngToRgb(byte[] pngData, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (pngData.Length < 24) return null;

        // Minimal PNG header parse for IHDR dimensions
        // PNG signature: 137 80 78 71 13 10 26 10
        if (pngData[0] != 137 || pngData[1] != 80 || pngData[2] != 78 || pngData[3] != 71)
            return null;

        // Use System.Drawing or WIC through WPF if available, fallback to raw decode
        // For simplicity, use a managed BMP decoder via interop
        try
        {
            using var ms = new MemoryStream(pngData);
            // Use WIC via COM interop for decoding
            var decoder = new PngBitmapDecoder(ms);
            width = decoder.Width;
            height = decoder.Height;
            return decoder.Pixels;
        }
        catch
        {
            return null;
        }
    }
}

internal sealed class PngBitmapDecoder : IDisposable
{
    public int Width { get; }
    public int Height { get; }
    public RgbColor[] Pixels { get; }

    public PngBitmapDecoder(MemoryStream stream)
    {
        var decoded = WindowsPngDecoder.Decode(stream.ToArray());
        Width = decoded.Width;
        Height = decoded.Height;
        Pixels = decoded.Pixels;
    }

    public void Dispose() { }
}
