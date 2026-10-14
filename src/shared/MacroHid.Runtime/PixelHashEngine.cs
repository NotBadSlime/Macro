using MacroHid.Core;

namespace MacroHid.Runtime;

public static class PixelHashEngine
{
    public static byte[] ComputeHash(ScreenRegion region, Func<ScreenRegion, RgbColor[]>? capture = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var pixels = capture is null ? ScreenCaptureService.CaptureFrame(region) : PixelFrame.FromRgb(capture(region));
        return ComputeHash(region, pixels, cancellationToken);
    }

    internal static byte[] ComputeHash(ScreenRegion region, PixelFrame pixels, CancellationToken cancellationToken)
    {
        if (pixels.Count == 0) return [];

        var width = region.Width;
        var height = region.Height;
        var blockW = Math.Max(1, width / 8);
        var blockH = Math.Max(1, height / 8);
        var hash = new byte[64];

        for (int by = 0; by < 8; by++)
        {
            for (int bx = 0; bx < 8; bx++)
            {
                long r = 0, g = 0, b = 0;
                int count = 0;

                for (int y = by * blockH; y < Math.Min((by + 1) * blockH, height); y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    for (int x = bx * blockW; x < Math.Min((bx + 1) * blockW, width); x++)
                    {
                        var pixel = pixels[y * width + x];
                        r += pixel.R;
                        g += pixel.G;
                        b += pixel.B;
                        count++;
                    }
                }

                if (count > 0)
                {
                    var luma = (byte)((r / count * 299 + g / count * 587 + b / count * 114) / 1000);
                    hash[by * 8 + bx] = luma;
                }
            }
        }

        return hash;
    }

    public static double CompareSimilarity(byte[] hashA, byte[] hashB)
    {
        if (hashA.Length != hashB.Length || hashA.Length == 0) return 0;

        double sumSqDiff = 0;
        for (int i = 0; i < hashA.Length; i++)
        {
            var diff = hashA[i] - hashB[i];
            sumSqDiff += diff * diff;
        }

        var maxPossible = 255.0 * 255.0 * hashA.Length;
        return 1.0 - (sumSqDiff / maxPossible);
    }

    public static bool Matches(ScreenRegion region, byte[] referenceHash, double threshold, Func<ScreenRegion, RgbColor[]>? capture = null,
        CancellationToken cancellationToken = default)
    {
        var currentHash = ComputeHash(region, capture, cancellationToken);
        if (currentHash.Length == 0) return false;
        return CompareSimilarity(currentHash, referenceHash) >= threshold;
    }
}
