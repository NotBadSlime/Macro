using System.Runtime.InteropServices;
using MacroHid.Core;

namespace MacroHid.Runtime;

/// <summary>
/// Captures the virtual desktop into a BGRA bitmap so color picking can sample
/// what was on screen before an overlay window covers it.
/// </summary>
public sealed class DesktopScreenSnapshot : IDisposable
{
    private byte[]? pixels;

    private DesktopScreenSnapshot(int left, int top, int width, int height, byte[] pixels)
    {
        Left = left;
        Top = top;
        Width = width;
        Height = height;
        this.pixels = pixels;
    }

    public int Left { get; }
    public int Top { get; }
    public int Width { get; }
    public int Height { get; }

    public bool TryGetPixel(int screenX, int screenY, out RgbColor color)
    {
        color = new RgbColor(0, 0, 0);
        if (pixels is null)
        {
            return false;
        }

        var x = screenX - Left;
        var y = screenY - Top;
        if (x < 0 || y < 0 || x >= Width || y >= Height)
        {
            return false;
        }

        var offset = ((y * Width) + x) * 4;
        color = new RgbColor(pixels[offset + 2], pixels[offset + 1], pixels[offset]);
        return true;
    }

    public void Dispose()
    {
        pixels = null;
    }

    public static bool TryCaptureVirtualScreen(out DesktopScreenSnapshot? snapshot)
    {
        snapshot = null;
        var left = GetSystemMetrics(SmXVirtualScreen);
        var top = GetSystemMetrics(SmYVirtualScreen);
        var width = GetSystemMetrics(SmCxVirtualScreen);
        var height = GetSystemMetrics(SmCyVirtualScreen);
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            return false;
        }

        IntPtr memDc = IntPtr.Zero;
        IntPtr bitmap = IntPtr.Zero;
        IntPtr oldBitmap = IntPtr.Zero;
        try
        {
            memDc = CreateCompatibleDC(screenDc);
            bitmap = CreateCompatibleBitmap(screenDc, width, height);
            if (memDc == IntPtr.Zero || bitmap == IntPtr.Zero)
            {
                return false;
            }

            oldBitmap = SelectObject(memDc, bitmap);
            if (!BitBlt(memDc, 0, 0, width, height, screenDc, left, top, SrcCopy))
            {
                return false;
            }

            var bmi = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    BiSize = Marshal.SizeOf<BitmapInfoHeader>(),
                    BiWidth = width,
                    BiHeight = -height,
                    BiPlanes = 1,
                    BiBitCount = 32,
                    BiCompression = 0
                }
            };

            var buffer = new byte[width * height * 4];
            if (GetDIBits(memDc, bitmap, 0, (uint)height, buffer, ref bmi, DibRgbColors) == 0)
            {
                return false;
            }

            snapshot = new DesktopScreenSnapshot(left, top, width, height, buffer);
            return true;
        }
        finally
        {
            if (oldBitmap != IntPtr.Zero && memDc != IntPtr.Zero)
            {
                SelectObject(memDc, oldBitmap);
            }

            if (bitmap != IntPtr.Zero)
            {
                DeleteObject(bitmap);
            }

            if (memDc != IntPtr.Zero)
            {
                DeleteDC(memDc);
            }

            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private const uint SrcCopy = 0x00CC0020;
    private const uint DibRgbColors = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int BiSize;
        public int BiWidth;
        public int BiHeight;
        public short BiPlanes;
        public short BiBitCount;
        public int BiCompression;
        public int BiSizeImage;
        public int BiXPelsPerMeter;
        public int BiYPelsPerMeter;
        public int BiClrUsed;
        public int BiClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

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
        byte[] lpvBits, ref BitmapInfo lpbi, uint uUsage);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
