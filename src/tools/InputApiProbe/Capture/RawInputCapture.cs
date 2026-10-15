using System.Diagnostics;
using System.Runtime.InteropServices;

namespace InputApiProbe;

/// <summary>A1: Raw Input keyboard with RIDEV_INPUTSINK on a hidden window (cannot swallow keys).</summary>
internal sealed class RawInputCapture(ushort vk) : ICaptureStrategy
{
    private const uint WmInput = 0x00FF, RidInput = 0x10000003, RidevRemove = 0x1, RidevInputSink = 0x100;
    private const int HeaderSize = 24; // RAWINPUTHEADER on x64; RAWKEYBOARD follows

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice { public ushort UsagePage, Usage; public uint Flags; public IntPtr Target; }

    private readonly byte[] buffer = new byte[64];
    private MessageWindow? window;
    private bool held;

    public string Id => "raw-input";
    public event Action<long>? Captured;

    public void Start()
    {
        window = new MessageWindow(OnMessage, visible: false, "raw-input", onCreated: hwnd =>
        {
            var device = new RawInputDevice { UsagePage = 1, Usage = 6, Flags = RidevInputSink, Target = hwnd };
            if (!RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<RawInputDevice>()))
                throw new InvalidOperationException($"RegisterRawInputDevices failed, error={Marshal.GetLastWin32Error()}");
        });
    }

    private bool OnMessage(uint message, IntPtr w, IntPtr l)
    {
        if (message != WmInput) return false;
        long tick = Stopwatch.GetTimestamp();
        uint size = (uint)buffer.Length;
        uint r = GetRawInputData(l, RidInput, buffer, ref size, (uint)HeaderSize);
        if (r != uint.MaxValue && r >= HeaderSize + 8)
        {
            ushort flags = BitConverter.ToUInt16(buffer, HeaderSize + 2);  // RAWKEYBOARD.Flags, bit 0 = RI_KEY_BREAK
            ushort key = BitConverter.ToUInt16(buffer, HeaderSize + 6);    // RAWKEYBOARD.VKey
            if (key != vk) return false;
            if ((flags & 1) != 0) { held = false; return false; }
            if (held) return false; // auto-repeat of a key that is still down
            held = true;
            Captured?.Invoke(tick);
        }
        return false; // let DefWindowProc do the required WM_INPUT cleanup
    }

    public void Stop()
    {
        var device = new RawInputDevice { UsagePage = 1, Usage = 6, Flags = RidevRemove, Target = IntPtr.Zero };
        if (window is not null) RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<RawInputDevice>());
        window?.Dispose();
        window = null;
    }

    public void Dispose() => Stop();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RawInputDevice[] devices, uint count, uint size);
    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(IntPtr rawInput, uint command, byte[] data, ref uint size, uint headerSize);
}
