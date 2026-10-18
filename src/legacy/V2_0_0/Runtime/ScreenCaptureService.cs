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

    public static byte[]? CaptureRegion(ScreenRegion region) => CaptureRegionCore(region, false, out _);

    internal static PixelFrame CaptureFrame(ScreenRegion region)
    {
        if (Environment.GetEnvironmentVariable("MACROHID_LEGACY_IMAGES") == "1")
            return PixelFrame.FromRgb(CaptureRegionAsRgb(region));
        var raw = CaptureRegionCore(region, true, out var pooled);
        return raw is null ? new PixelFrame([], 0) : new PixelFrame(raw, checked(region.Width * region.Height), pooled);
    }

    private static byte[]? CaptureRegionCore(ScreenRegion region, bool rent, out bool pooled)
    {
        pooled = false;
        if (region.Width <= 0 || region.Height <= 0) return null;
        if (BackgroundWindowSession.IsActive) return BackgroundWindowSession.CopyRegion(region);
        int size = checked(region.Width * region.Height * 4);
        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return null;
        IntPtr memDc = IntPtr.Zero, bitmap = IntPtr.Zero, oldBitmap = IntPtr.Zero;
        byte[]? pixels = null;
        bool success = false;
        try
        {
            memDc = CreateCompatibleDC(screenDc);
            if (memDc == IntPtr.Zero) return null;
            bitmap = CreateCompatibleBitmap(screenDc, region.Width, region.Height);
            if (bitmap == IntPtr.Zero) return null;
            oldBitmap = SelectObject(memDc, bitmap);
            if (!BitBlt(memDc, 0, 0, region.Width, region.Height, screenDc, region.TopLeft.X, region.TopLeft.Y, SRCCOPY)) return null;
            SelectObject(memDc, oldBitmap); oldBitmap = IntPtr.Zero;
            var bmi = new BITMAPINFO { bmiHeader = new BITMAPINFOHEADER {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = region.Width,
                biHeight = -region.Height, biPlanes = 1, biBitCount = 32 } };
            pixels = rent ? System.Buffers.ArrayPool<byte>.Shared.Rent(size) : new byte[size];
            if (GetDIBits(memDc, bitmap, 0, (uint)region.Height, pixels, ref bmi, DIB_RGB_COLORS) != region.Height) return null;
            pooled = rent; success = true;
            return pixels;
        }
        finally
        {
            if (!success && rent && pixels is not null) System.Buffers.ArrayPool<byte>.Shared.Return(pixels);
            if (oldBitmap != IntPtr.Zero) SelectObject(memDc, oldBitmap);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memDc != IntPtr.Zero) DeleteDC(memDc);
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
