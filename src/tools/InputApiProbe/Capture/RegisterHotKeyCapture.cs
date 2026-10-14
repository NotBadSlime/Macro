using System.Diagnostics;
using System.Runtime.InteropServices;

namespace InputApiProbe;

/// <summary>A2: RegisterHotKey on a hidden window. Down events only; the key is consumed by the system.</summary>
internal sealed class RegisterHotKeyCapture(ushort vk) : ICaptureStrategy
{
    private const uint WmHotKey = 0x0312, ModNoRepeat = 0x4000;
    private MessageWindow? window;

    public string Id => "register-hotkey";
    public event Action<long>? Captured;

    public void Start()
    {
        window = new MessageWindow(OnMessage, visible: false, "register-hotkey", onCreated: hwnd =>
        {
            if (!RegisterHotKey(hwnd, 1, ModNoRepeat, vk))
                throw new InvalidOperationException($"RegisterHotKey failed, error={Marshal.GetLastWin32Error()}");
        });
    }

    private bool OnMessage(uint message, IntPtr w, IntPtr l)
    {
        if (message != WmHotKey) return false;
        Captured?.Invoke(Stopwatch.GetTimestamp());
        return true;
    }

    public void Stop() { window?.Dispose(); window = null; }
    public void Dispose() => Stop();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);
}
