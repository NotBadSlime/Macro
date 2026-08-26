using System.Diagnostics;
using System.Runtime.InteropServices;
using MacroHid.Core;

namespace MacroStudio;

public sealed class MacroInputRecorder : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int WmMouseMove = 0x0200;
    private const int WmMouseWheel = 0x020A;
    private const int WmMouseHWheel = 0x020E;
    private const int WmXButtonDown = 0x020B;
    private const int WmXButtonUp = 0x020C;
    private const int LlkHfExtended = 0x01;
    private const int LlkHfInjected = 0x10;
    private const int LlmHfInjected = 0x01;
    private const int VkControl = 0x11;
    private const int VkShift = 0x10;
    private const int VkLControl = 0xA2;
    private const int VkRControl = 0xA3;
    private const int VkLShift = 0xA0;
    private const int VkRShift = 0xA1;
    private const int VkF12 = 0x7B;
    private const int WheelDelta = 120;

    private readonly LowLevelHookProc keyboardHookProc;
    private readonly LowLevelHookProc mouseHookProc;
    private readonly MacroRecordingSession session;
    private readonly HashSet<int> pressedVirtualKeys = [];
    private readonly int ownProcessId = Environment.ProcessId;
    private IntPtr keyboardHook;
    private IntPtr mouseHook;
    private long startedAt;
    private bool stopRequested;
    private bool disposed;

    public MacroInputRecorder(MacroRecordingOptions? options = null)
    {
        session = new MacroRecordingSession(options);
        keyboardHookProc = KeyboardHookCallback;
        mouseHookProc = MouseHookCallback;
    }

    public event EventHandler? StopHotkeyPressed;
    public event EventHandler? InputCaptured;

    public int InputCount => session.InputCount;
    public bool IsRecording => keyboardHook != IntPtr.Zero || mouseHook != IntPtr.Zero;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (IsRecording)
        {
            return;
        }

        pressedVirtualKeys.Clear();
        stopRequested = false;
        startedAt = Stopwatch.GetTimestamp();
        keyboardHook = SetWindowsHookEx(WhKeyboardLl, keyboardHookProc, IntPtr.Zero, 0);
        if (keyboardHook == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Failed to start keyboard recording. error={Marshal.GetLastWin32Error()}");
        }

        mouseHook = SetWindowsHookEx(WhMouseLl, mouseHookProc, IntPtr.Zero, 0);
        if (mouseHook == IntPtr.Zero)
        {
            StopHooks();
            throw new InvalidOperationException($"Failed to start mouse recording. error={Marshal.GetLastWin32Error()}");
        }
    }

    public IReadOnlyList<MacroStep> Stop()
    {
        StopHooks();
        return session.Complete();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        StopHooks();
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && !stopRequested)
        {
            var message = wParam.ToInt32();
            var data = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            if ((data.Flags & LlkHfInjected) == 0)
            {
                var isDown = message is WmKeyDown or WmSysKeyDown;
                var isUp = message is WmKeyUp or WmSysKeyUp;
                if (isDown)
                {
                    pressedVirtualKeys.Add(data.VirtualKeyCode);
                    if (data.VirtualKeyCode == VkF12 && StopChordIsDown())
                    {
                        stopRequested = true;
                        session.DiscardTrailingStopHotkey();
                        StopHotkeyPressed?.Invoke(this, EventArgs.Empty);
                        // Reserve F12 for the recorder so the target application does not
                        // also react to Ctrl+Shift+F12 while recording is being stopped.
                        return new IntPtr(1);
                    }
                }

                if ((isDown || isUp)
                    && !IsOwnProcessForeground()
                    && GlobalKeyboardHook.TryMapVirtualKeyToHidKey(
                        data.VirtualKeyCode,
                        data.ScanCode,
                        (data.Flags & LlkHfExtended) != 0,
                        out var key)
                    && session.RecordKey(Elapsed(), key, isDown))
                {
                    InputCaptured?.Invoke(this, EventArgs.Empty);
                }

                if (isUp)
                {
                    pressedVirtualKeys.Remove(data.VirtualKeyCode);
                }
            }
        }

        return CallNextHookEx(keyboardHook, nCode, wParam, lParam);
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && !stopRequested)
        {
            var message = wParam.ToInt32();
            var data = Marshal.PtrToStructure<MouseLlHookStruct>(lParam);
            if ((data.Flags & LlmHfInjected) == 0 && !IsOwnProcessForeground())
            {
                var captured = message switch
                {
                    WmMouseMove => session.RecordMouseMove(Elapsed(), data.Point.X, data.Point.Y),
                    WmMouseWheel => session.RecordMouseWheel(Elapsed(), WheelUnits(data.MouseData), 0),
                    WmMouseHWheel => session.RecordMouseWheel(Elapsed(), 0, WheelUnits(data.MouseData)),
                    _ => RecordMouseButton(message, data)
                };
                if (captured)
                {
                    InputCaptured?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        return CallNextHookEx(mouseHook, nCode, wParam, lParam);
    }

    private bool RecordMouseButton(int message, MouseLlHookStruct data)
    {
        if (message is not WmXButtonDown and not WmXButtonUp
            && !GlobalKeyboardHook.TryMapMouseMessage(message, data.MouseData, out _, out _))
        {
            return false;
        }

        return GlobalKeyboardHook.TryMapMouseMessage(message, data.MouseData, out var button, out var isDown)
            && session.RecordMouseButton(Elapsed(), button, isDown, data.Point.X, data.Point.Y);
    }

    private bool StopChordIsDown()
    {
        return IsAnyDown(VkControl, VkLControl, VkRControl)
            && IsAnyDown(VkShift, VkLShift, VkRShift);
    }

    private bool IsAnyDown(params int[] keys) => keys.Any(pressedVirtualKeys.Contains);

    private TimeSpan Elapsed() => Stopwatch.GetElapsedTime(startedAt, Stopwatch.GetTimestamp());

    private bool IsOwnProcessForeground()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return false;
        }

        _ = GetWindowThreadProcessId(foreground, out var processId);
        return processId == ownProcessId;
    }

    private static int WheelUnits(int mouseData)
    {
        var delta = unchecked((short)((mouseData >> 16) & 0xFFFF));
        if (delta == 0)
        {
            return 0;
        }

        var units = delta / WheelDelta;
        return units != 0 ? units : Math.Sign(delta);
    }

    private void StopHooks()
    {
        if (keyboardHook != IntPtr.Zero)
        {
            _ = UnhookWindowsHookEx(keyboardHook);
            keyboardHook = IntPtr.Zero;
        }

        if (mouseHook != IntPtr.Zero)
        {
            _ = UnhookWindowsHookEx(mouseHook);
            mouseHook = IntPtr.Zero;
        }

        pressedVirtualKeys.Clear();
    }

    private delegate IntPtr LowLevelHookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public int VirtualKeyCode;
        public int ScanCode;
        public int Flags;
        public int Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseLlHookStruct
    {
        public NativePoint Point;
        public int MouseData;
        public int Flags;
        public int Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int idHook,
        LowLevelHookProc callback,
        IntPtr module,
        uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);
}
