using System.ComponentModel;
using System.Runtime.InteropServices;
using MacroHid.Core;
using MacroHid.Runtime;

namespace MacroStudio;

// Raw Input front end for GlobalKeyboardHook. It feeds the same HandleKeyDown/HandleKeyUp/HandleMouseDown/HandleMouseUp
// state machine as the low-level hooks, on the same "MacroHID-Hotkeys" thread, so trigger evaluation is identical.
//
// Why: a WH_KEYBOARD_LL hook sits in the synchronous input chain. Every key anyone injects (our own playback included)
// waits for our callback before SendInput returns; measured here a hook-free process added ~80 us p50 and ~350 us p99 to
// each key SendInput. Raw Input is delivered asynchronously and never blocks the sender.
//
// Differences that matter:
// - Injected input: Raw Input marks injected events with hDevice == 0, which is how we keep ignoring our own playback.
// - Swallowing: Raw Input cannot block a key. Only the colour-sample key is swallowed; that uses RegisterHotKey (same
//   effect for a plain key or key + modifiers). If the gesture cannot be registered (taken by another app, or a mouse /
//   multi-key gesture), the whole hook falls back to the low-level hooks so behaviour is never silently weaker.
public sealed partial class GlobalKeyboardHook
{
    private const int WmInput = 0x00FF;
    private const int WmHotkey = 0x0312;
    private const int RidInput = 0x10000003;
    private const int RimTypeMouse = 0;
    private const int RimTypeKeyboard = 1;
    private const int RideviceInputSink = 0x00000100;
    private const int RideviceRemove = 0x00000001;
    private const int RiKeyBreak = 0x01;
    private const int RiKeyE0 = 0x02;
    private const int ColorSampleHotkeyId = 0x4D48; // "MH"
    private const int ModNoRepeat = 0x4000;

    private System.Windows.Interop.HwndSource? rawWindow;
    private bool rawInputActive;
    private bool rawMouseRegistered;
    private bool swallowRegistered;
    private HotkeyGesture? registeredSwallow;
    private byte[] rawBuffer = new byte[64];

    private bool TryStartRawInput()
    {
        if (!TryCreateRawWindow()) return false;
        // The mouse is only registered when a binding uses a mouse button: a registered mouse turns every mouse move on
        // the system, injected ones included, into a WM_INPUT for us, which measurably slowed SendInput of moves.
        var wantMouse = gestures.Values.Any(gesture => gesture.MouseButtons.Count > 0);
        var devices = wantMouse
            ? new[]
            {
                new RawInputDevice { UsagePage = 0x01, Usage = 0x06, Flags = RideviceInputSink, Target = rawWindow!.Handle },
                new RawInputDevice { UsagePage = 0x01, Usage = 0x02, Flags = RideviceInputSink, Target = rawWindow.Handle }
            }
            : [new RawInputDevice { UsagePage = 0x01, Usage = 0x06, Flags = RideviceInputSink, Target = rawWindow!.Handle }];
        rawMouseRegistered = wantMouse;
        if (!RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RawInputDevice>()))
        {
            AppLog.Error("Hotkeys", $"Raw Input registration failed ({Marshal.GetLastWin32Error()}); using low-level hooks.");
            DestroyRawWindow();
            return false;
        }

        rawInputActive = true;
        if (!UpdateRawSwallowHotkey())
        {
            AppLog.Info("Hotkeys", "Colour-sample key cannot be swallowed via RegisterHotKey; using low-level hooks.");
            StopRawInput();
            return false;
        }

        AppLog.Info("Hotkeys", "Hotkey detection: Raw Input");
        return true;
    }

    /// <summary>Re-register the swallowed colour-sample key after a binding change. False if it cannot be registered.</summary>
    private bool UpdateRawSwallowHotkey()
    {
        if (!rawInputActive || rawWindow is null) return true;
        // A new binding needs the mouse: restart Raw Input with it registered (Start falls through to TryStartRawInput).
        if (!rawMouseRegistered && gestures.Values.Any(gesture => gesture.MouseButtons.Count > 0))
        {
            var mouse = new[] { new RawInputDevice { UsagePage = 0x01, Usage = 0x02, Flags = RideviceInputSink, Target = rawWindow.Handle } };
            rawMouseRegistered = RegisterRawInputDevices(mouse, 1, (uint)Marshal.SizeOf<RawInputDevice>());
        }
        gestures.TryGetValue(CoreHotkeys.ColorSampleId, out var wanted);
        if (Equals(wanted, registeredSwallow) && (wanted is null || swallowRegistered)) return true;
        if (swallowRegistered) { UnregisterHotKey(rawWindow.Handle, ColorSampleHotkeyId); swallowRegistered = false; }
        registeredSwallow = wanted;
        if (wanted is null) return true;
        if (wanted.Keys.Count != 1 || wanted.MouseButtons.Count != 0) return false;
        var virtualKey = SendInputEncoder.VirtualKeyOf(wanted.Keys[0]);
        if (virtualKey == 0) return false;
        swallowRegistered = RegisterHotKey(rawWindow.Handle, ColorSampleHotkeyId, ToHotkeyModifiers(wanted.Modifiers) | ModNoRepeat, virtualKey);
        return swallowRegistered;
    }

    private void StopRawInput()
    {
        if (!rawInputActive && rawWindow is null) return;
        if (rawWindow is not null)
        {
            if (swallowRegistered) UnregisterHotKey(rawWindow.Handle, ColorSampleHotkeyId);
            var remove = rawMouseRegistered
                ? new[]
                {
                    new RawInputDevice { UsagePage = 0x01, Usage = 0x06, Flags = RideviceRemove, Target = IntPtr.Zero },
                    new RawInputDevice { UsagePage = 0x01, Usage = 0x02, Flags = RideviceRemove, Target = IntPtr.Zero }
                }
                : [new RawInputDevice { UsagePage = 0x01, Usage = 0x06, Flags = RideviceRemove, Target = IntPtr.Zero }];
            RegisterRawInputDevices(remove, (uint)remove.Length, (uint)Marshal.SizeOf<RawInputDevice>());
        }
        rawMouseRegistered = false;
        swallowRegistered = false;
        registeredSwallow = null;
        rawInputActive = false;
        DestroyRawWindow();
    }

    private bool TryCreateRawWindow()
    {
        if (rawWindow is not null) return true;
        try
        {
            // Message-only window on the hotkeys thread (this runs inside Start on that dispatcher).
            var parameters = new System.Windows.Interop.HwndSourceParameters("MacroHID-RawInput")
            {
                ParentWindow = new IntPtr(-3), // HWND_MESSAGE
                WindowStyle = 0,
                Width = 0,
                Height = 0
            };
            rawWindow = new System.Windows.Interop.HwndSource(parameters);
            rawWindow.AddHook(RawWindowProc);
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            AppLog.Error("Hotkeys", "Raw Input window creation failed", ex);
            rawWindow = null;
            return false;
        }
    }

    private void DestroyRawWindow()
    {
        if (rawWindow is null) return;
        rawWindow.RemoveHook(RawWindowProc);
        rawWindow.Dispose();
        rawWindow = null;
    }

    private IntPtr RawWindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotkey && wParam.ToInt32() == ColorSampleHotkeyId)
        {
            // The key was swallowed by RegisterHotKey, so Raw Input may still report it (it does for the down edge):
            // trigger evaluation runs from the raw events below, nothing to do here.
            handled = true;
            return IntPtr.Zero;
        }

        if (message != WmInput) return IntPtr.Zero;
        callbackQpc = System.Diagnostics.Stopwatch.GetTimestamp();
        if (gestures.Count > 0) ProcessRawInput(lParam);
        SelfTime.Record(callbackQpc, System.Diagnostics.Stopwatch.GetTimestamp());
        // Let DefWindowProc run (it must, for WM_INPUT cleanup).
        return IntPtr.Zero;
    }

    private void ProcessRawInput(IntPtr handle)
    {
        uint size = 0;
        var headerSize = (uint)Marshal.SizeOf<RawInputHeader>();
        if (GetRawInputData(handle, RidInput, IntPtr.Zero, ref size, headerSize) != 0 || size == 0) return;
        if (rawBuffer.Length < size) rawBuffer = new byte[Math.Max(size, rawBuffer.Length * 2)];
        var pin = GCHandle.Alloc(rawBuffer, GCHandleType.Pinned);
        try
        {
            if (GetRawInputData(handle, RidInput, pin.AddrOfPinnedObject(), ref size, headerSize) != size) return;
        }
        finally { pin.Free(); }

        var span = rawBuffer.AsSpan(0, (int)size);
        var header = MemoryMarshal.Read<RawInputHeader>(span);
        // Injected input (SendInput, including our own playback) has no device handle.
        if (header.Device == IntPtr.Zero) return;
        var body = span[(int)headerSize..];
        if (header.Type == RimTypeKeyboard)
        {
            var keyboard = MemoryMarshal.Read<RawKeyboard>(body);
            if (keyboard.VirtualKey is 0 or 0xFF) return; // fake shift / overflow
            var extended = (keyboard.Flags & RiKeyE0) != 0;
            var virtualKey = NormalizeRawVirtualKey(keyboard.VirtualKey, keyboard.MakeCode, extended);
            if ((keyboard.Flags & RiKeyBreak) != 0) HandleKeyUp(virtualKey, keyboard.MakeCode, extended);
            else HandleKeyDown(virtualKey, keyboard.MakeCode, extended);
        }
        else if (header.Type == RimTypeMouse)
        {
            var flags = MemoryMarshal.Read<RawMouse>(body).ButtonFlags;
            if ((flags & 0x0001) != 0) HandleMouseDown(MouseButton.Left);
            if ((flags & 0x0002) != 0) HandleMouseUp(MouseButton.Left);
            if ((flags & 0x0004) != 0) HandleMouseDown(MouseButton.Right);
            if ((flags & 0x0008) != 0) HandleMouseUp(MouseButton.Right);
            if ((flags & 0x0010) != 0) HandleMouseDown(MouseButton.Middle);
            if ((flags & 0x0020) != 0) HandleMouseUp(MouseButton.Middle);
            if ((flags & 0x0040) != 0) HandleMouseDown(MouseButton.X1);
            if ((flags & 0x0080) != 0) HandleMouseUp(MouseButton.X1);
            if ((flags & 0x0100) != 0) HandleMouseDown(MouseButton.X2);
            if ((flags & 0x0200) != 0) HandleMouseUp(MouseButton.X2);
        }
    }

    /// <summary>
    /// Raw Input reports the generic VK_SHIFT / VK_CONTROL / VK_MENU; the low-level hook (and the trigger matcher) use the
    /// left/right codes. Shift sides differ by scan code, Ctrl/Alt sides by the E0 prefix.
    /// </summary>
    internal static int NormalizeRawVirtualKey(int virtualKey, int makeCode, bool e0) => virtualKey switch
    {
        0x10 => makeCode == 0x36 ? 0xA1 : 0xA0,
        0x11 => e0 ? 0xA3 : 0xA2,
        0x12 => e0 ? 0xA5 : 0xA4,
        _ => virtualKey
    };

    private static uint ToHotkeyModifiers(HidModifier modifiers)
    {
        uint result = 0;
        if ((modifiers & (HidModifier.LeftAlt | HidModifier.RightAlt)) != 0) result |= 0x0001;
        if ((modifiers & (HidModifier.LeftCtrl | HidModifier.RightCtrl)) != 0) result |= 0x0002;
        if ((modifiers & (HidModifier.LeftShift | HidModifier.RightShift)) != 0) result |= 0x0004;
        if ((modifiers & (HidModifier.LeftGui | HidModifier.RightGui)) != 0) result |= 0x0008;
        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice { public ushort UsagePage, Usage; public int Flags; public IntPtr Target; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader { public int Type, Size; public IntPtr Device, WParam; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawKeyboard { public ushort MakeCode, Flags, Reserved, VirtualKey; public uint Message, ExtraInformation; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawMouse { public ushort Flags, Padding, ButtonFlags, ButtonData; public uint RawButtons; public int LastX, LastY; public uint ExtraInformation; }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(RawInputDevice[] devices, uint count, uint size);

    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(IntPtr rawInput, int command, IntPtr data, ref uint size, uint headerSize);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
