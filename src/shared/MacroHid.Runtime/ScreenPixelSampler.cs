using MacroHid.Core;

namespace MacroHid.Runtime;

public static class ScreenPixelSampler
{
    public static bool Matches(PixelCondition condition)
    {
        return TrySample(condition.Coordinate, out var sample) && condition.Matches(sample);
    }

    public static bool TrySample(PixelCoordinate coordinate, out PixelSample sample)
    {
        sample = new PixelSample(coordinate.X, coordinate.Y, new RgbColor(0, 0, 0));
        var x = coordinate.X;
        var y = coordinate.Y;

        if (coordinate.Scope == CoordinateScope.Window)
        {
            if (string.IsNullOrWhiteSpace(coordinate.WindowTitle))
            {
                return false;
            }

            var window = RuntimeNativeMethods.FindWindowW(null, coordinate.WindowTitle);
            if (window == IntPtr.Zero || !RuntimeNativeMethods.GetWindowRect(window, out var rect))
            {
                return false;
            }

            x += rect.Left;
            y += rect.Top;
        }

        var desktopDc = RuntimeNativeMethods.GetDC(IntPtr.Zero);
        if (desktopDc == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var colorRef = RuntimeNativeMethods.GetPixel(desktopDc, x, y);
            if (colorRef == RuntimeNativeMethods.ClrInvalid)
            {
                return false;
            }

            sample = new PixelSample(
                x,
                y,
                new RgbColor(
                    (byte)(colorRef & 0xFF),
                    (byte)((colorRef >> 8) & 0xFF),
                    (byte)((colorRef >> 16) & 0xFF)));
            return true;
        }
        finally
        {
            RuntimeNativeMethods.ReleaseDC(IntPtr.Zero, desktopDc);
        }
    }
}
