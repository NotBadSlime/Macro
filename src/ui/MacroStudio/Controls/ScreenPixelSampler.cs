using System.Runtime.InteropServices;
using MacroHid.Core;

namespace MacroStudio.Controls;

public static class ScreenPixelSampler
{
    public static bool TryReadPixel(int x, int y, out RgbColor color)
    {
        color = new RgbColor(0, 0, 0);
        var dc = GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) return false;

        try
        {
            var pixel = GetPixel(dc, x, y);
            if (pixel == 0xFFFF_FFFF) return false;
            color = new RgbColor(
                (byte)(pixel & 0xFF),
                (byte)((pixel >> 8) & 0xFF),
                (byte)((pixel >> 16) & 0xFF));
            return true;
        }
        finally
        {
            _ = ReleaseDC(IntPtr.Zero, dc);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern uint GetPixel(IntPtr hdc, int nXPos, int nYPos);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
}
