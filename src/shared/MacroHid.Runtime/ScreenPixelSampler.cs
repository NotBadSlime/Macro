using MacroHid.Core;

namespace MacroHid.Runtime;

public static class ScreenPixelSampler
{
    private static readonly object CacheGate = new();
    private static readonly Dictionary<string, IntPtr> WindowCache = new(StringComparer.Ordinal);
    private static IntPtr desktopDc;

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

            var window = GetCachedWindow(coordinate.WindowTitle);
            if (window == IntPtr.Zero || !RuntimeNativeMethods.GetWindowRect(window, out var rect))
            {
                DropCachedWindow(coordinate.WindowTitle);
                return false;
            }

            x += rect.Left;
            y += rect.Top;
        }

        var dc = GetDesktopDc();
        if (dc == IntPtr.Zero)
        {
            return false;
        }

        var colorRef = RuntimeNativeMethods.GetPixel(dc, x, y);
        if (colorRef == RuntimeNativeMethods.ClrInvalid)
        {
            ResetDesktopDc();
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

    private static IntPtr GetDesktopDc()
    {
        lock (CacheGate)
        {
            if (desktopDc == IntPtr.Zero)
            {
                desktopDc = RuntimeNativeMethods.GetDC(IntPtr.Zero);
            }

            return desktopDc;
        }
    }

    private static void ResetDesktopDc()
    {
        lock (CacheGate)
        {
            if (desktopDc != IntPtr.Zero)
            {
                RuntimeNativeMethods.ReleaseDC(IntPtr.Zero, desktopDc);
                desktopDc = IntPtr.Zero;
            }
        }
    }

    private static IntPtr GetCachedWindow(string title)
    {
        lock (CacheGate)
        {
            if (!WindowCache.TryGetValue(title, out var window) || window == IntPtr.Zero)
            {
                window = RuntimeNativeMethods.FindWindowW(null, title);
                if (window != IntPtr.Zero)
                {
                    WindowCache[title] = window;
                }
            }

            return window;
        }
    }

    private static void DropCachedWindow(string title)
    {
        lock (CacheGate)
        {
            WindowCache.Remove(title);
        }
    }
}
