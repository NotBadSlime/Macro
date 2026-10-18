using System.Runtime.InteropServices;
using MacroHid.Core;

namespace MacroHid.Runtime;

public static class ScreenCaptureService
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int width, int height,
        IntPtr hdcSrc, int xSrc, int ySrc, uint rop);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint uStartScan, uint cScanLines,
        byte[] lpvBits, ref BITMAPINFO lpbi, uint uUsage);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    private const uint SRCCOPY = 0x00CC0020;
    private const uint DIB_RGB_COLORS = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
    }

    public static byte[]? CaptureRegion(ScreenRegion region)
    {
        var width = region.Width;
        var height = region.Height;
        if (width <= 0 || height <= 0) return null;

        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return null;

        try
        {
            var memDc = CreateCompatibleDC(screenDc);
            var bitmap = CreateCompatibleBitmap(screenDc, width, height);
            var oldBitmap = SelectObject(memDc, bitmap);

            BitBlt(memDc, 0, 0, width, height, screenDc, region.TopLeft.X, region.TopLeft.Y, SRCCOPY);

            var bmi = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = width,
                    biHeight = -height,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0
                }
            };

            var stride = width * 4;
            var pixels = new byte[stride * height];
            GetDIBits(memDc, bitmap, 0, (uint)height, pixels, ref bmi, DIB_RGB_COLORS);

            SelectObject(memDc, oldBitmap);
            DeleteObject(bitmap);
            DeleteDC(memDc);

            return pixels;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    public static RgbColor[] CaptureRegionAsRgb(ScreenRegion region)
    {
        var raw = CaptureRegion(region);
        if (raw == null) return [];

        var width = region.Width;
        var height = region.Height;
        var result = new RgbColor[width * height];

        for (int i = 0; i < result.Length; i++)
        {
            var offset = i * 4;
            result[i] = new RgbColor(raw[offset + 2], raw[offset + 1], raw[offset]);
        }

        return result;
    }

    /// <summary>
    /// Detects exclusive-fullscreen / protected captures that come back near-black or flat.
    /// </summary>
    public static bool LooksBlankOrUniform(byte[] pixels, int width, int height)
    {
        if (pixels.Length < 4 || width <= 0 || height <= 0)
        {
            return true;
        }

        var stepX = Math.Max(1, width / 32);
        var stepY = Math.Max(1, height / 32);
        var min = 255;
        var max = 0;
        long total = 0;
        var samples = 0;
        for (var y = 0; y < height; y += stepY)
        {
            for (var x = 0; x < width; x += stepX)
            {
                var index = ((y * width) + x) * 4;
                if (index + 2 >= pixels.Length)
                {
                    continue;
                }

                var b = pixels[index];
                var g = pixels[index + 1];
                var r = pixels[index + 2];
                var luma = (r * 299 + g * 587 + b * 114) / 1000;
                if (luma < min) min = luma;
                if (luma > max) max = luma;
                total += luma;
                samples++;
            }
        }

        if (samples == 0)
        {
            return true;
        }

        var average = total / samples;
        var span = max - min;
        return span <= 6 && average <= 18;
    }
}
