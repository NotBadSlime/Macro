using System.Runtime.InteropServices;

namespace InputApiProbe;

/// <summary>
/// A window with its own message-loop thread. The handler runs on that thread for every message and
/// returns true to swallow it (result 0); returning false falls through to DefWindowProc.
/// </summary>
internal sealed class MessageWindow : IDisposable
{
    private const uint WmDestroy = 0x2, WmClose = 0x10;
    private static readonly IntPtr HwndMessage = new(-3);

    private delegate IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr w, IntPtr l);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size, Style;
        public IntPtr Proc;
        public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background;
        public string? Menu, Name;
        public IntPtr SmallIcon;
    }

    private readonly Func<uint, IntPtr, IntPtr, bool> handler;
    private readonly Action<IntPtr>? onCreated;
    private readonly bool visible, messageOnly;
    private readonly string title, className = "MacroHID.ApiProbe." + Guid.NewGuid().ToString("N");
    private readonly WindowProc proc;
    private readonly Thread thread;
    private readonly ManualResetEventSlim ready = new();
    private Exception? failure;

    public IntPtr Handle { get; private set; }

    /// <param name="onCreated">Runs on the window thread right after creation (RegisterHotKey and raw-input registration must happen there).</param>
    /// <param name="messageOnly">For hidden windows: parent them to HWND_MESSAGE. Set false to get a hidden top-level window.</param>
    public MessageWindow(Func<uint, IntPtr, IntPtr, bool> handler, bool visible, string title,
        Action<IntPtr>? onCreated = null, bool messageOnly = true)
    {
        this.handler = handler; this.visible = visible; this.title = title;
        this.onCreated = onCreated; this.messageOnly = messageOnly;
        proc = Proc;
        thread = new Thread(Run) { IsBackground = true, Name = "ApiProbe-" + title, Priority = ThreadPriority.AboveNormal };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        if (failure is not null) throw new InvalidOperationException($"{title}: {failure.Message}", failure);
    }

    private IntPtr Proc(IntPtr hwnd, uint message, IntPtr w, IntPtr l)
    {
        if (handler(message, w, l)) return IntPtr.Zero;
        if (message == WmDestroy) PostQuitMessage(0);
        return DefWindowProc(hwnd, message, w, l);
    }

    private void Run()
    {
        IntPtr instance = GetModuleHandle(null);
        var cls = new WindowClass
        {
            Size = (uint)Marshal.SizeOf<WindowClass>(), Proc = Marshal.GetFunctionPointerForDelegate(proc), Instance = instance,
            Cursor = LoadCursor(IntPtr.Zero, new IntPtr(32512)), Background = new IntPtr(6), Name = className
        };
        try
        {
            if (RegisterClassEx(ref cls) == 0) throw new InvalidOperationException($"RegisterClassEx failed, error={Marshal.GetLastWin32Error()}");
            // 0x10CF0000 = WS_VISIBLE | WS_OVERLAPPEDWINDOW, ex style 8 = WS_EX_TOPMOST (same as the existing receiver).
            Handle = visible
                ? CreateWindowEx(8, className, title, 0x10CF0000, 240, 240, 600, 200, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero)
                : CreateWindowEx(0, className, title, 0, 0, 0, 0, 0, messageOnly ? HwndMessage : IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (Handle == IntPtr.Zero) throw new InvalidOperationException($"CreateWindowEx failed, error={Marshal.GetLastWin32Error()}");
            onCreated?.Invoke(Handle);
        }
        catch (Exception ex)
        {
            failure = ex;
            if (Handle != IntPtr.Zero) DestroyWindow(Handle);
            UnregisterClass(className, instance);
            ready.Set();
            return;
        }
        ready.Set();
        while (Native.GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        { Native.TranslateMessage(ref message); Native.DispatchMessage(ref message); }
        UnregisterClass(className, instance);
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero) Native.PostMessage(Handle, WmClose, UIntPtr.Zero, IntPtr.Zero);
        thread.Join(2000);
        GC.KeepAlive(proc);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WindowClass cls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string name, IntPtr instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(uint ex, string cls, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
}
