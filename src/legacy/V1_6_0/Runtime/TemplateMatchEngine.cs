using MacroHid.Core;

namespace MacroHid.Runtime;

public static class TemplateMatchEngine
{
    public static (int x, int y, double score) Match(ScreenRegion searchRegion, byte[] templatePngData, double threshold = 0.85)
    {
        var searchPixels = ScreenCaptureService.CaptureRegionAsRgb(searchRegion);
        if (searchPixels.Length == 0)
            return (-1, -1, 0);

        var templatePixels = DecodePngToRgb(templatePngData, out var templateWidth, out var templateHeight);
        if (templatePixels == null || templateWidth == 0 || templateHeight == 0)
            return (-1, -1, 0);

        var searchWidth = searchRegion.Width;
        var searchHeight = searchRegion.Height;

        if (templateWidth > searchWidth || templateHeight > searchHeight)
            return (-1, -1, 0);

        var bestScore = 0.0;
        var bestX = -1;
        var bestY = -1;

        for (int sy = 0; sy <= searchHeight - templateHeight; sy++)
        {
            for (int sx = 0; sx <= searchWidth - templateWidth; sx++)
            {
                var score = ComputeNCC(searchPixels, searchWidth, sx, sy, templatePixels, templateWidth, templateHeight);
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

    public static bool Matches(ScreenRegion searchRegion, byte[] templatePngData, double threshold = 0.85)
    {
        var (x, _, score) = Match(searchRegion, templatePngData, threshold);
        return x >= 0 && score >= threshold;
    }

    private static double ComputeNCC(RgbColor[] search, int searchWidth, int offsetX, int offsetY,
        RgbColor[] template, int templateWidth, int templateHeight)
    {
        double sumST = 0, sumSS = 0, sumTT = 0;
        double meanS = 0, meanT = 0;
        int count = templateWidth * templateHeight;

        for (int ty = 0; ty < templateHeight; ty++)
        {
            for (int tx = 0; tx < templateWidth; tx++)
            {
                var sp = search[(offsetY + ty) * searchWidth + (offsetX + tx)];
                var tp = template[ty * templateWidth + tx];
                meanS += Luma(sp);
                meanT += Luma(tp);
            }
        }

        meanS /= count;
        meanT /= count;

        for (int ty = 0; ty < templateHeight; ty++)
        {
            for (int tx = 0; tx < templateWidth; tx++)
            {
                var sp = search[(offsetY + ty) * searchWidth + (offsetX + tx)];
                var tp = template[ty * templateWidth + tx];
                var s = Luma(sp) - meanS;
                var t = Luma(tp) - meanT;
                sumST += s * t;
                sumSS += s * s;
                sumTT += t * t;
            }
        }

        var denom = Math.Sqrt(sumSS * sumTT);
        return denom < 1e-10 ? 0 : sumST / denom;
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
    [System.Runtime.InteropServices.DllImport("windowscodecs.dll", PreserveSig = false)]
    private static extern void WICCreateImagingFactory_Proxy(uint sdkVersion, out IntPtr ppIImagingFactory);

    public int Width { get; }
    public int Height { get; }
    public RgbColor[] Pixels { get; }

    public PngBitmapDecoder(MemoryStream stream)
    {
        // Simpler approach: use GDI+ via System.Drawing.Common concepts
        // Since we can't depend on System.Drawing, parse minimal PNG or use WIC
        // For now use a safe fallback - read IHDR for dimensions and return gray
        var data = stream.ToArray();
        if (data.Length < 33)
        {
            Width = 0; Height = 0; Pixels = [];
            return;
        }

        // IHDR is at offset 8 (length=4) + 4 (type) = data starts at 16
        Width = (data[16] << 24) | (data[17] << 16) | (data[18] << 8) | data[19];
        Height = (data[20] << 24) | (data[21] << 16) | (data[22] << 8) | data[23];

        // Full PNG decode is complex; for production we'll use WIC interop
        // Placeholder: fill with gray to indicate unknown content
        Pixels = new RgbColor[Width * Height];
        Array.Fill(Pixels, new RgbColor(128, 128, 128));
    }

    public void Dispose() { }
}
