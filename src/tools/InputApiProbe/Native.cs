using System.Runtime.InteropServices;

namespace InputApiProbe;

internal enum InjectVariant { Vk, Scan, VkScan, KeybdEvent, MergedPair }

/// <summary>All P/Invoke used by the probe, plus the keyboard injection variants under test.</summary>
internal static class Native
{
    public const ushort VkF24 = 0x87;
    public const ulong Tag = 0x4D484150; // "MHAP", dwExtraInfo on injected input

    private const uint InputKeyboard = 1, KeyUpFlag = 0x2, ScanCodeFlag = 0x8;

    /// <summary>Scan code of F24, resolved once so MapVirtualKey is never inside a timed region. 0 if unmapped.</summary>
    public static readonly ushort ScanF24 = (ushort)MapVirtualKey(VkF24, 0);

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput { public ushort Vk, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct InputUnion { [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint Type; public InputUnion Union; }

    [StructLayout(LayoutKind.Sequential)]
    public struct Msg
    {
        public IntPtr Hwnd;
        public uint Id;
        public UIntPtr W;
        public IntPtr L;
        public uint Time;
        public int X, Y;
        public uint Private;
    }

    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] public static extern int GetMessage(out Msg message, IntPtr hwnd, uint minimum, uint maximum);
    [DllImport("user32.dll")] public static extern bool TranslateMessage(ref Msg message);
    [DllImport("user32.dll")] public static extern IntPtr DispatchMessage(ref Msg message);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint message, UIntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool PostThreadMessage(uint thread, uint message, UIntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint milliseconds);
    [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint milliseconds);

    private static Input Key(ushort vk, ushort scan, uint flags) => new()
    {
        Type = InputKeyboard,
        Union = new() { Keyboard = new() { Vk = vk, Scan = scan, Flags = flags, Extra = new UIntPtr(Tag) } }
    };

    // Pre-built after ScanF24 (static initialisers run in textual order) so injection never allocates.
    private static readonly Input[] VkDown = { Key(VkF24, 0, 0) };
    private static readonly Input[] VkUp = { Key(VkF24, 0, KeyUpFlag) };
    private static readonly Input[] ScanDown = { Key(0, ScanF24, ScanCodeFlag) };
    private static readonly Input[] ScanUp = { Key(0, ScanF24, ScanCodeFlag | KeyUpFlag) };
    private static readonly Input[] VkScanDown = { Key(VkF24, ScanF24, 0) };
    private static readonly Input[] VkScanUp = { Key(VkF24, ScanF24, KeyUpFlag) };
    private static readonly Input[] MergedPairInputs = { Key(VkF24, 0, 0), Key(VkF24, 0, KeyUpFlag) };
    private static readonly int InputSize = Marshal.SizeOf<Input>();

    /// <summary>Forces static initialisation. With requireScan, throws if F24 has no scan code mapping.</summary>
    public static void Warmup(bool requireScan = true)
    {
        if (requireScan && ScanF24 == 0) throw new InvalidOperationException("MapVirtualKey(VK_F24) returned 0; no scan code available");
    }

    private static void Send(Input[] inputs)
    {
        if (SendInput((uint)inputs.Length, inputs, InputSize) != inputs.Length)
            throw new InvalidOperationException($"SendInput failed, error={Marshal.GetLastSystemError()}");
    }

    /// <summary>Key-down of F24 using the given API variant. MergedPair sends down and up in one SendInput call.</summary>
    public static void KeyDown(InjectVariant variant)
    {
        switch (variant)
        {
            case InjectVariant.Vk: Send(VkDown); break;
            case InjectVariant.Scan: Send(ScanDown); break;
            case InjectVariant.VkScan: Send(VkScanDown); break;
            case InjectVariant.KeybdEvent: keybd_event((byte)VkF24, (byte)ScanF24, 0, new UIntPtr(Tag)); break;
            case InjectVariant.MergedPair: Send(MergedPairInputs); break;
        }
    }

    /// <summary>Key-up of F24. A no-op for MergedPair because KeyDown already sent the up event.</summary>
    public static void KeyUp(InjectVariant variant)
    {
        switch (variant)
        {
            case InjectVariant.Vk: Send(VkUp); break;
            case InjectVariant.Scan: Send(ScanUp); break;
            case InjectVariant.VkScan: Send(VkScanUp); break;
            case InjectVariant.KeybdEvent: keybd_event((byte)VkF24, (byte)ScanF24, KeyUpFlag, new UIntPtr(Tag)); break;
            case InjectVariant.MergedPair: break;
        }
    }
}
