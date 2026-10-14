using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

// A separate process with a plain GetMessage loop. This removes the WPF
// dispatcher's workload from the receiver comparison, not from the hotkey hook.
internal static class NativeReceiver
{
    internal static void Run(string name)
    {
        using var memory = MemoryMappedFile.OpenExisting(name);
        using var view = memory.CreateViewAccessor();
        IntPtr label = IntPtr.Zero;
        int doneTicks = 0;
        string previous = "";
        WindowProc callback = (hwnd, message, w, l) =>
        {
            if (message == 0x100 && w.ToInt64() == 0x87)
            {
                long tick = Stopwatch.GetTimestamp();
                int row = view.ReadInt32(0);
                if (row >= 0 && row < view.ReadInt32(44))
                {
                    long at = 128 + (long)row * 128;
                    int count = view.ReadInt32(at + 72);
                    view.Write(at + 72, count + 1);
                    if (count == 0) { view.Write(at + 48, tick); view.Write(4, view.ReadInt32(4) + 1); }
                }
            }
            if (message == 0x113) // WM_TIMER; no per-input UI work
            {
                int phase = view.ReadInt32(48), received = view.ReadInt32(4);
                string text = phase switch
                {
                    0 => "请点击此窗口，将它置于前台。正在预热，请先不要按 F8。",
                    1 => $"请按下并松开 F8 共 {view.ReadInt32(52)} 次，每次间隔约半秒。\r\n已接收 {received} 次。请保持此窗口为前台。",
                    2 => $"正在自动测试，请不要切换窗口。\r\n已接收 {received} 次。",
                    3 => "测试完成，结果已保存。窗口即将关闭。",
                    _ => "测试未完成，请查看错误记录。窗口即将关闭。"
                };
                if (text != previous) { SetWindowText(label, text); previous = text; }
                if (view.ReadInt32(8) != 0 && ++doneTicks >= 30) DestroyWindow(hwnd);
                return IntPtr.Zero;
            }
            if (message == 0x2) { view.Write(24, 1); PostQuitMessage(0); return IntPtr.Zero; }
            return DefWindowProc(hwnd, message, w, l);
        };
        string className = "MacroHID.Receiver." + Guid.NewGuid().ToString("N");
        var cls = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(),
            Proc = Marshal.GetFunctionPointerForDelegate(callback), Instance = GetModuleHandle(null),
            Cursor = LoadCursor(IntPtr.Zero, new IntPtr(32512)), Background = new IntPtr(6), Name = className };
        if (RegisterClassEx(ref cls) == 0) throw new InvalidOperationException("Cannot register receiver window");
        IntPtr window = IntPtr.Zero;
        try
        {
            window = CreateWindowEx(8, className, "MacroHID 真实热键接收测试（Win32）", 0x10CF0000,
                240, 240, 800, 300, IntPtr.Zero, IntPtr.Zero, cls.Instance, IntPtr.Zero);
            if (window == IntPtr.Zero) throw new InvalidOperationException("Cannot create receiver window");
            label = CreateWindowEx(0, "STATIC", "正在准备测试……", 0x50000000,
                24, 40, 720, 180, window, IntPtr.Zero, cls.Instance, IntPtr.Zero);
            if (label == IntPtr.Zero || SetTimer(window, new UIntPtr(1), 100, IntPtr.Zero) == UIntPtr.Zero)
                throw new InvalidOperationException("Cannot initialize receiver UI");
            view.Write(16, window.ToInt64()); view.Write(12, 1);
            SetForegroundWindow(window);
            int result;
            while ((result = GetMessage(out var message, IntPtr.Zero, 0, 0)) > 0)
            { TranslateMessage(ref message); DispatchMessage(ref message); }
            if (result < 0) throw new InvalidOperationException("Receiver message loop failed");
        }
        finally
        {
            if (window != IntPtr.Zero) DestroyWindow(window);
            UnregisterClass(className, cls.Instance);
            GC.KeepAlive(callback);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
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
    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr Hwnd;
        public uint Id;
        public UIntPtr W;
        public IntPtr L;
        public uint Time;
        public int X, Y;
        public uint Private;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WindowClass cls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string name, IntPtr instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowEx(uint ex, string cls, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetWindowText(IntPtr hwnd, string text);
    [DllImport("user32.dll")] private static extern UIntPtr SetTimer(IntPtr hwnd, UIntPtr id, uint milliseconds, IntPtr callback);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetMessage(out Message message, IntPtr hwnd, uint minimum, uint maximum);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DispatchMessage(ref Message message);
}
