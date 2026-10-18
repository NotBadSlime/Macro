using System.Diagnostics;
using System.Runtime.InteropServices;
using MacroHid.Core;

namespace MacroHid.Runtime;

public readonly record struct BackgroundWindowTarget(
    nint Hwnd,
    int ClientLeft,
    int ClientTop,
    int ClientWidth,
    int ClientHeight);

public readonly record struct BackgroundClientImage(int Width, int Height, byte[] Bgra);

internal readonly record struct BackgroundWindowLookup(
    bool Found,
    bool Minimized,
    nint Hwnd,
    int ClientLeft,
    int ClientTop,
    int ClientWidth,
    int ClientHeight);

public enum BackgroundMessageKind
{
    Activate,
    KeyDown,
    KeyUp,
    Char,
    MouseMove,
    MouseDown,
    MouseUp,
    Wheel,
    HorizontalWheel
}

public readonly record struct BackgroundPostedMessage(
    nint Hwnd,
    BackgroundMessageKind Kind,
    int WParam,
    int LParam,
    int ClientX,
    int ClientY,
    MouseButton Button);

public static class BackgroundWindowSession
{
    private static readonly object Gate = new();
    private static BackgroundWindowTarget? target;
    private static bool useGraphicsCapture;
    private static int cursorX;
    private static int cursorY;
    private static MouseButton heldButtons;
    private static WindowGraphicsCapture? graphicsCapture;

    internal static Func<string, BackgroundWindowLookup>? TestResolve { get; set; }
    internal static Action<BackgroundPostedMessage>? TestPost { get; set; }
    internal static Action<int, int>? TestInjectRelative { get; set; }
    internal static Func<BackgroundWindowTarget, BackgroundClientImage?>? TestCaptureClient { get; set; }
    internal static Func<BackgroundWindowTarget, BackgroundClientImage?>? TestCaptureGraphics { get; set; }

    public static bool IsActive
    {
        get
        {
            lock (Gate)
            {
                return target is not null;
            }
        }
    }

    public static bool TryBegin(string? processName, out string? error)
    {
        var name = NormalizeFileName(processName);
        if (name.Length == 0)
        {
            error = "请先设置后台执行的目标进程。";
            return false;
        }

        var lookup = Resolve(name);
        if (!lookup.Found)
        {
            error = "还没有找到目标窗口。请先打开目标程序。";
            return false;
        }

        if (lookup.Minimized || lookup.ClientWidth <= 0 || lookup.ClientHeight <= 0)
        {
            error = "目标窗口已最小化，请先恢复窗口。";
            return false;
        }

        lock (Gate)
        {
            EndPlaybackCore();
            target = new BackgroundWindowTarget(
                lookup.Hwnd,
                lookup.ClientLeft,
                lookup.ClientTop,
                lookup.ClientWidth,
                lookup.ClientHeight);
            cursorX = lookup.ClientWidth / 2;
            cursorY = lookup.ClientHeight / 2;
            heldButtons = MouseButton.None;
            useGraphicsCapture = false;
            error = null;
            return true;
        }
    }

    public static BackgroundPlaybackScope BeginScope(PlaybackExecutionOptions options)
    {
        if (!options.BackgroundWindow)
        {
            return default;
        }

        if (!TryBegin(options.BackgroundProcess, out var error))
        {
            throw new InvalidOperationException(error ?? "还没有找到目标窗口。请先打开目标程序。");
        }

        return new BackgroundPlaybackScope(true);
    }

    public static void EndPlayback()
    {
        lock (Gate)
        {
            EndPlaybackCore();
        }
    }

    internal static void ResetForTests()
    {
        lock (Gate)
        {
            EndPlaybackCore();
            TestResolve = null;
            TestPost = null;
            TestInjectRelative = null;
            TestCaptureClient = null;
            TestCaptureGraphics = null;
        }
    }

    internal static bool TryPost(BackgroundPostedMessage message)
    {
        lock (Gate)
        {
            if (target is null)
            {
                return false;
            }

            if (TestPost is not null)
            {
                TestPost(message);
                return true;
            }

            return WindowMessagePoster.Post(message);
        }
    }

    internal static void MoveCursor(int x, int y, bool clampToClient = true)
    {
        lock (Gate)
        {
            if (target is not { } window)
            {
                return;
            }

            if (clampToClient)
            {
                cursorX = Math.Clamp(x, 0, Math.Max(0, window.ClientWidth - 1));
                cursorY = Math.Clamp(y, 0, Math.Max(0, window.ClientHeight - 1));
            }
            else
            {
                cursorX = x;
                cursorY = y;
            }
        }
    }

    internal static (int X, int Y) Cursor
    {
        get
        {
            lock (Gate)
            {
                return (cursorX, cursorY);
            }
        }
    }

    internal static BackgroundWindowTarget? CurrentTarget
    {
        get
        {
            lock (Gate)
            {
                return target;
            }
        }
    }

    internal static void SetHeldButtons(MouseButton buttons)
    {
        lock (Gate)
        {
            heldButtons = buttons;
        }
    }

    internal static MouseButton HeldButtons
    {
        get
        {
            lock (Gate)
            {
                return heldButtons;
            }
        }
    }

    public static byte[]? CopyRegion(ScreenRegion region)
    {
        var image = CaptureClient();
        if (image is not { } captured || captured.Width <= 0 || captured.Height <= 0 || region.Width <= 0 || region.Height <= 0)
        {
            return null;
        }

        BackgroundWindowTarget window;
        lock (Gate)
        {
            if (target is not { } current)
            {
                return null;
            }

            window = current;
        }

        var pixels = new byte[region.Width * region.Height * 4];
        var sourceX = region.TopLeft.X - window.ClientLeft;
        var sourceY = region.TopLeft.Y - window.ClientTop;
        for (var y = 0; y < region.Height; y++)
        {
            var imageY = sourceY + y;
            if (imageY < 0 || imageY >= captured.Height)
            {
                continue;
            }

            for (var x = 0; x < region.Width; x++)
            {
                var imageX = sourceX + x;
                if (imageX < 0 || imageX >= captured.Width)
                {
                    continue;
                }

                var from = ((imageY * captured.Width) + imageX) * 4;
                var to = ((y * region.Width) + x) * 4;
                if (from + 3 >= captured.Bgra.Length || to + 3 >= pixels.Length)
                {
                    continue;
                }

                pixels[to] = captured.Bgra[from];
                pixels[to + 1] = captured.Bgra[from + 1];
                pixels[to + 2] = captured.Bgra[from + 2];
                pixels[to + 3] = captured.Bgra[from + 3];
            }
        }

        return pixels;
    }

    private static BackgroundClientImage? CaptureClient()
    {
        BackgroundWindowTarget window;
        var graphics = false;
        lock (Gate)
        {
            if (target is not { } current)
            {
                return null;
            }

            window = current;
            graphics = useGraphicsCapture;
        }

        if (TestingCapture)
        {
            if (graphics)
            {
                return TestCaptureGraphics?.Invoke(window) ?? TestCaptureClient?.Invoke(window);
            }

            var injected = TestCaptureClient?.Invoke(window);
            if (injected is { } clientImage
                && !ScreenCaptureService.LooksBlankOrUniform(clientImage.Bgra, clientImage.Width, clientImage.Height))
            {
                return clientImage;
            }

            var fallback = TestCaptureGraphics?.Invoke(window);
            if (fallback is not null)
            {
                lock (Gate)
                {
                    useGraphicsCapture = true;
                }
            }

            return fallback ?? injected;
        }

        if (!graphics)
        {
            var client = WindowClientCapture.Capture(window.Hwnd, window.ClientWidth, window.ClientHeight);
            if (client is { } clientImage
                && !ScreenCaptureService.LooksBlankOrUniform(clientImage.Bgra, clientImage.Width, clientImage.Height))
            {
                return clientImage;
            }

            lock (Gate)
            {
                useGraphicsCapture = true;
                graphics = true;
            }
        }

        return CaptureWithGraphics(window) ?? WindowClientCapture.Capture(window.Hwnd, window.ClientWidth, window.ClientHeight);
    }

    private static bool TestingCapture => TestCaptureClient is not null || TestCaptureGraphics is not null;

    private static BackgroundClientImage? CaptureWithGraphics(BackgroundWindowTarget window)
    {
        try
        {
            WindowGraphicsCapture capture;
            lock (Gate)
            {
                graphicsCapture ??= new WindowGraphicsCapture();
                capture = graphicsCapture;
            }

            return capture.CopyClient(window);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static void EndPlaybackCore()
    {
        target = null;
        cursorX = 0;
        cursorY = 0;
        heldButtons = MouseButton.None;
        useGraphicsCapture = false;
        graphicsCapture?.Dispose();
        graphicsCapture = null;
    }

    private static BackgroundWindowLookup Resolve(string processName)
    {
        if (TestResolve is not null || TestPost is not null || TestCaptureClient is not null || TestCaptureGraphics is not null)
        {
            return (TestResolve ?? (_ => default)).Invoke(processName);
        }

        return WindowFinder.Find(processName);
    }

    internal static string NormalizeFileName(string? processName)
    {
        var name = Path.GetFileName(processName?.Trim() ?? string.Empty);
        if (name.Length == 0)
        {
            return string.Empty;
        }

        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
    }
}

public readonly struct BackgroundPlaybackScope : IDisposable
{
    private readonly bool active;

    internal BackgroundPlaybackScope(bool active)
    {
        this.active = active;
    }

    public void Dispose()
    {
        if (active)
        {
            BackgroundWindowSession.EndPlayback();
        }
    }
}

internal static class WindowFinder
{
    public static BackgroundWindowLookup Find(string processName)
    {
        var matches = new List<BackgroundWindowLookup>();
        EnumWindows((hwnd, lParam) =>
        {
            if (hwnd == 0 || !IsWindowVisible(hwnd))
            {
                return true;
            }

            GetWindowThreadProcessId(hwnd, out var processId);
            if (processId == 0 || !ProcessMatches(processId, processName))
            {
                return true;
            }

            if (IsIconic(hwnd) || !TryGetClient(hwnd, out var left, out var top, out var width, out var height))
            {
                matches.Add(new BackgroundWindowLookup(true, true, hwnd, 0, 0, 0, 0));
                return true;
            }

            matches.Add(new BackgroundWindowLookup(true, false, hwnd, left, top, width, height));
            return true;
        }, 0);

        var visible = matches
            .Where(match => !match.Minimized && match.ClientWidth > 0 && match.ClientHeight > 0)
            .OrderByDescending(match => match.ClientWidth * match.ClientHeight)
            .FirstOrDefault();
        if (visible.Found)
        {
            return visible;
        }

        return matches.Count > 0
            ? matches[0] with { Minimized = true }
            : default;
    }

    private static bool ProcessMatches(uint processId, string processName)
    {
        try
        {
            using var process = Process.GetProcessById(unchecked((int)processId));
            var name = process.ProcessName;
            if (name.Length == 0)
            {
                return false;
            }

            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                name += ".exe";
            }

            return string.Equals(name, processName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetClient(nint hwnd, out int left, out int top, out int width, out int height)
    {
        left = top = width = height = 0;
        if (!GetClientRect(hwnd, out var rect))
        {
            return false;
        }

        width = rect.Right - rect.Left;
        height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        var origin = new Point();
        if (!ClientToScreen(hwnd, ref origin))
        {
            return false;
        }

        left = origin.X;
        top = origin.Y;
        return true;
    }

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(nint hwnd, ref Point point);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }
}

internal static class WindowClientCapture
{
    public static BackgroundClientImage? Capture(nint hwnd, int width, int height)
    {
        if (hwnd == 0 || width <= 0 || height <= 0)
        {
            return null;
        }

        var windowDc = GetDC(hwnd);
        if (windowDc == 0)
        {
            return null;
        }

        try
        {
            var memoryDc = CreateCompatibleDC(windowDc);
            var bitmap = CreateCompatibleBitmap(windowDc, width, height);
            var previous = SelectObject(memoryDc, bitmap);
            var copied = BitBlt(memoryDc, 0, 0, width, height, windowDc, 0, 0, SrcCopy);
            var info = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = width,
                    Height = -height,
                    Planes = 1,
                    BitCount = 32
                }
            };
            var pixels = new byte[width * height * 4];
            _ = GetDIBits(memoryDc, bitmap, 0, (uint)height, pixels, ref info, DibRgbColors);
            SelectObject(memoryDc, previous);
            DeleteObject(bitmap);
            DeleteDC(memoryDc);
            return copied ? new BackgroundClientImage(width, height, pixels) : null;
        }
        finally
        {
            ReleaseDC(hwnd, windowDc);
        }
    }

    private const uint SrcCopy = 0x00CC0020;
    private const uint DibRgbColors = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
    }

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hwnd, nint hdc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint hdc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleBitmap(nint hdc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint hdc, nint obj);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(nint hdcDest, int xDest, int yDest, int width, int height, nint hdcSrc, int xSrc, int ySrc, uint rop);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(nint hdc, nint bitmap, uint start, uint lines, byte[] bits, ref BitmapInfo info, uint usage);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(nint hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint obj);
}
