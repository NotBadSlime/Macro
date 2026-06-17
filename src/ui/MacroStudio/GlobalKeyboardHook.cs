using System.Runtime.InteropServices;
using MacroHid.Core;

namespace MacroStudio;

public sealed record HotkeyBinding(string Id, HotkeyGesture Gesture);

public sealed class HotkeyTriggeredEventArgs : EventArgs
{
    public HotkeyTriggeredEventArgs(string id, HotkeyGesture gesture)
    {
        Id = id;
        Gesture = gesture;
    }

    public string Id { get; }
    public HotkeyGesture Gesture { get; }
}

public sealed class GlobalKeyboardHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int WmXButtonDown = 0x020B;
    private const int WmXButtonUp = 0x020C;
    private const int LlkHfInjected = 0x10;
    private const int LlmHfInjected = 0x1;
    private const int XButton1 = 0x0001;
    private const int XButton2 = 0x0002;

    private readonly LowLevelHookProc keyboardHookProc;
    private readonly LowLevelHookProc mouseHookProc;
    private readonly HashSet<int> pressedKeys = [];
    private readonly HashSet<MouseButton> pressedMouseButtons = [];
    private readonly Dictionary<string, HotkeyGesture> gestures = [];
    private readonly HashSet<string> triggersDown = [];
    private IntPtr hookHandle;
    private IntPtr mouseHookHandle;

    public GlobalKeyboardHook()
    {
        keyboardHookProc = KeyboardHookCallback;
        mouseHookProc = MouseHookCallback;
    }

    public event EventHandler<HotkeyTriggeredEventArgs>? TriggerPressed;

    public event EventHandler<HotkeyTriggeredEventArgs>? TriggerReleased;

    public void Start(HotkeyGesture trigger)
    {
        Start([new HotkeyBinding(string.Empty, trigger)]);
    }

    public void Start(IEnumerable<HotkeyBinding> bindings)
    {
        Stop();
        var distinctBindings = bindings
            .Select(binding => new HotkeyBinding(binding.Id ?? string.Empty, binding.Gesture))
            .GroupBy(binding => binding.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        if (distinctBindings.Count == 0)
        {
            throw new InvalidOperationException("At least one hotkey binding is required.");
        }

        foreach (var binding in distinctBindings)
        {
            gestures[binding.Id] = binding.Gesture;
        }

        triggersDown.Clear();
        pressedKeys.Clear();
        pressedMouseButtons.Clear();
        hookHandle = SetWindowsHookEx(WhKeyboardLl, keyboardHookProc, IntPtr.Zero, 0);
        if (hookHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Failed to install keyboard hook. error={Marshal.GetLastWin32Error()}");
        }

        mouseHookHandle = SetWindowsHookEx(WhMouseLl, mouseHookProc, IntPtr.Zero, 0);
        if (mouseHookHandle == IntPtr.Zero)
        {
            Stop();
            throw new InvalidOperationException($"Failed to install mouse hook. error={Marshal.GetLastWin32Error()}");
        }
    }

    public void Stop()
    {
        if (hookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(hookHandle);
            hookHandle = IntPtr.Zero;
        }

        if (mouseHookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(mouseHookHandle);
            mouseHookHandle = IntPtr.Zero;
        }

        gestures.Clear();
        triggersDown.Clear();
        pressedKeys.Clear();
        pressedMouseButtons.Clear();
    }

    public void Dispose()
    {
        Stop();
    }

    public static bool TryMapVirtualKeyToHidKey(int virtualKey, out HidKey key)
    {
        if (virtualKey >= 0x41 && virtualKey <= 0x5A)
        {
            key = (HidKey)((int)HidKey.A + (virtualKey - 0x41));
            return true;
        }

        if (virtualKey >= 0x31 && virtualKey <= 0x39)
        {
            key = (HidKey)((int)HidKey.D1 + (virtualKey - 0x31));
            return true;
        }

        if (virtualKey == 0x30)
        {
            key = HidKey.D0;
            return true;
        }

        if (virtualKey >= 0x70 && virtualKey <= 0x87)
        {
            key = (HidKey)((int)HidKey.F1 + (virtualKey - 0x70));
            return true;
        }

        key = virtualKey switch
        {
            0x08 => HidKey.Backspace,
            0x09 => HidKey.Tab,
            0x0D => HidKey.Enter,
            0x1B => HidKey.Escape,
            0x20 => HidKey.Space,
            0x2D => HidKey.Insert,
            0x2E => HidKey.Delete,
            0x23 => HidKey.End,
            0x24 => HidKey.Home,
            0x21 => HidKey.PageUp,
            0x22 => HidKey.PageDown,
            0x25 => HidKey.LeftArrow,
            0x26 => HidKey.UpArrow,
            0x27 => HidKey.RightArrow,
            0x28 => HidKey.DownArrow,
            0xBA => HidKey.Semicolon,
            0xBB => HidKey.Equal,
            0xBC => HidKey.Comma,
            0xBD => HidKey.Minus,
            0xBE => HidKey.Period,
            0xBF => HidKey.Slash,
            0xC0 => HidKey.Grave,
            0xDB => HidKey.LeftBracket,
            0xDC => HidKey.Backslash,
            0xDD => HidKey.RightBracket,
            0xDE => HidKey.Quote,
            _ => HidKey.None
        };

        return key != HidKey.None;
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && gestures.Count > 0)
        {
            var message = wParam.ToInt32();
            var data = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            if ((data.Flags & LlkHfInjected) == 0)
            {
                if (message is WmKeyDown or WmSysKeyDown)
                {
                    HandleKeyDown(data.VirtualKeyCode);
                }
                else if (message is WmKeyUp or WmSysKeyUp)
                {
                    HandleKeyUp(data.VirtualKeyCode);
                }
            }
        }

        return CallNextHookEx(hookHandle, nCode, wParam, lParam);
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && gestures.Count > 0)
        {
            var message = wParam.ToInt32();
            if (message is WmXButtonDown or WmXButtonUp)
            {
                var data = Marshal.PtrToStructure<MouseLlHookStruct>(lParam);
                if ((data.Flags & LlmHfInjected) == 0
                    && TryGetXButton(data.MouseData, out var button))
                {
                    if (message == WmXButtonDown)
                    {
                        HandleMouseDown(button);
                    }
                    else
                    {
                        HandleMouseUp(button);
                    }
                }
            }
        }

        return CallNextHookEx(mouseHookHandle, nCode, wParam, lParam);
    }

    private void HandleKeyDown(int virtualKey)
    {
        var wasAdded = pressedKeys.Add(virtualKey);
        if (!wasAdded)
        {
            return;
        }

        EvaluatePressedTriggers();
    }

    private void HandleKeyUp(int virtualKey)
    {
        pressedKeys.Remove(virtualKey);
        EvaluateReleasedTriggers();
    }

    private void HandleMouseDown(MouseButton button)
    {
        var wasAdded = pressedMouseButtons.Add(button);
        if (!wasAdded)
        {
            return;
        }

        EvaluatePressedTriggers();
    }

    private void HandleMouseUp(MouseButton button)
    {
        pressedMouseButtons.Remove(button);
        EvaluateReleasedTriggers();
    }

    private void EvaluatePressedTriggers()
    {
        foreach (var (id, currentGesture) in gestures)
        {
            if (triggersDown.Contains(id) || !GestureIsDown(currentGesture))
            {
                continue;
            }

            triggersDown.Add(id);
            TriggerPressed?.Invoke(this, new HotkeyTriggeredEventArgs(id, currentGesture));
        }
    }

    private void EvaluateReleasedTriggers()
    {
        foreach (var id in triggersDown.ToList())
        {
            if (!gestures.TryGetValue(id, out var currentGesture) || GestureIsDown(currentGesture))
            {
                continue;
            }

            triggersDown.Remove(id);
            TriggerReleased?.Invoke(this, new HotkeyTriggeredEventArgs(id, currentGesture));
        }
    }

    private bool GestureIsDown(HotkeyGesture trigger)
    {
        if (trigger.Key == HidKey.None && trigger.MouseButton == MouseButton.None)
        {
            return ModifiersMatch(trigger.Modifiers);
        }

        if (trigger.MouseButton != MouseButton.None)
        {
            return pressedMouseButtons.Contains(trigger.MouseButton) && ModifiersMatch(trigger.Modifiers);
        }

        return IsKeyDown(trigger.Key) && ModifiersMatch(trigger.Modifiers);
    }

    private bool IsKeyDown(HidKey key)
    {
        return pressedKeys.Any(virtualKey => TryMapVirtualKeyToHidKey(virtualKey, out var pressed) && pressed == key);
    }

    private bool ModifiersMatch(HidModifier modifiers)
    {
        return ModifierGroupMatches(modifiers, HidModifier.LeftCtrl | HidModifier.RightCtrl, 0xA2, 0xA3)
            && ModifierGroupMatches(modifiers, HidModifier.LeftShift | HidModifier.RightShift, 0xA0, 0xA1)
            && ModifierGroupMatches(modifiers, HidModifier.LeftAlt | HidModifier.RightAlt, 0xA4, 0xA5)
            && ModifierGroupMatches(modifiers, HidModifier.LeftGui | HidModifier.RightGui, 0x5B, 0x5C);
    }

    private bool ModifierGroupMatches(HidModifier modifiers, HidModifier mask, int leftVirtualKey, int rightVirtualKey)
    {
        return (modifiers & mask) == 0 || pressedKeys.Contains(leftVirtualKey) || pressedKeys.Contains(rightVirtualKey);
    }

    private static bool TryGetXButton(int mouseData, out MouseButton button)
    {
        var highWord = (mouseData >> 16) & 0xFFFF;
        button = highWord switch
        {
            XButton1 => MouseButton.X1,
            XButton2 => MouseButton.X2,
            _ => MouseButton.None
        };

        return button != MouseButton.None;
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
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseLlHookStruct
    {
        public Point Point;
        public int MouseData;
        public int Flags;
        public int Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int idHook,
        LowLevelHookProc lpfn,
        IntPtr hmod,
        uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
}
