using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SkirkFps;
public sealed class HotkeyRegistry(nint window) : IDisposable
{
    private readonly List<int> ids = [];
    public void Register(int id, FpsRule rule)
    {
        if (rule.VirtualKey is <= 0 or > 254) throw new InvalidOperationException("请先录入快捷键。");
        if ((rule.Modifiers & ~15u) != 0) throw new InvalidOperationException("快捷键修饰键无效。");
        if (!RegisterHotKey(window, id, rule.Modifiers | 0x4000, (uint)rule.VirtualKey))
            throw new InvalidOperationException($"快捷键 {rule.HotkeyText} 无法注册，可能已被其他程序占用。\n{new Win32Exception(Marshal.GetLastWin32Error()).Message}");
        ids.Add(id);
    }
    public void Dispose() { foreach (var id in ids) UnregisterHotKey(window, id); ids.Clear(); }
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);
}
