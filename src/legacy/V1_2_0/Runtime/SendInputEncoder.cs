using MacroHid.Core;

namespace MacroHid.Runtime;

public enum SendInputPacketKind
{
    Keyboard,
    Mouse
}

public readonly record struct VirtualDesktopBounds(int Left, int Top, int Width, int Height);

public readonly record struct SendInputPacket(
    SendInputPacketKind Kind,
    ushort VirtualKey,
    uint Flags,
    int MouseX,
    int MouseY,
    uint MouseData,
    ushort ScanCode = 0);

public static class SendInputEncoder
{
    private const int WheelDelta = 120;
    private const uint XButton1 = 0x0001;
    private const uint XButton2 = 0x0002;

    public static IReadOnlyList<SendInputPacket> Encode(InputAction action, VirtualDesktopBounds? virtualDesktop = null)
    {
        return action switch
        {
            KeyInputAction key => EncodeKey(key),
            TextInputAction text => EncodeText(text),
            MouseMoveInputAction move => EncodeMouseMove(move, virtualDesktop),
            MouseButtonInputAction button => EncodeMouseButton(button),
            MouseWheelInputAction wheel => EncodeMouseWheel(wheel),
            ConsumerInputAction consumer => EncodeConsumer(consumer),
            _ => throw new NotSupportedException($"Unsupported input action '{action.GetType().Name}'.")
        };
    }

    private static IReadOnlyList<SendInputPacket> EncodeKey(KeyInputAction action)
    {
        var packets = new List<SendInputPacket>();
        if (action.Kind == KeyActionKind.Down)
        {
            foreach (var modifierVirtualKey in ModifierVirtualKeys(action.Modifiers))
            {
                packets.Add(KeyboardPacket(modifierVirtualKey, keyUp: false));
            }

            if (action.Key != HidKey.None)
            {
                packets.Add(KeyboardPacket(MapKey(action.Key), keyUp: false));
            }

            return packets;
        }

        if (action.Key != HidKey.None)
        {
            packets.Add(KeyboardPacket(MapKey(action.Key), keyUp: true));
        }

        foreach (var modifierVirtualKey in ModifierVirtualKeys(action.Modifiers).Reverse())
        {
            packets.Add(KeyboardPacket(modifierVirtualKey, keyUp: true));
        }

        return packets;
    }

    private static IReadOnlyList<SendInputPacket> EncodeText(TextInputAction action)
    {
        if (string.IsNullOrEmpty(action.Text))
        {
            return [];
        }

        var packets = new List<SendInputPacket>(action.Text.Length * 2);
        foreach (var ch in action.Text)
        {
            packets.Add(UnicodePacket(ch, keyUp: false));
            packets.Add(UnicodePacket(ch, keyUp: true));
        }

        return packets;
    }

    private static IReadOnlyList<SendInputPacket> EncodeMouseMove(
        MouseMoveInputAction action,
        VirtualDesktopBounds? virtualDesktop)
    {
        var x = action.X;
        var y = action.Y;
        var flags = RuntimeNativeMethods.MouseEventFMove;

        if (action.Mode == MouseMoveMode.Absolute)
        {
            flags |= RuntimeNativeMethods.MouseEventFAbsolute | RuntimeNativeMethods.MouseEventFVirtualDesk;
            if (virtualDesktop is { } bounds)
            {
                x = NormalizeAbsoluteCoordinate(action.X, bounds.Left, bounds.Width);
                y = NormalizeAbsoluteCoordinate(action.Y, bounds.Top, bounds.Height);
            }
        }

        return [new SendInputPacket(SendInputPacketKind.Mouse, 0, flags, x, y, 0)];
    }

    private static IReadOnlyList<SendInputPacket> EncodeMouseButton(MouseButtonInputAction action)
    {
        if (action.Button == MouseButton.None)
        {
            return [];
        }

        var packets = new List<SendInputPacket>();
        if (action.Kind == ButtonActionKind.Click)
        {
            AddMouseButtonPackets(packets, action.Button, ButtonActionKind.Down);
            AddMouseButtonPackets(packets, action.Button, ButtonActionKind.Up);
            return packets;
        }

        AddMouseButtonPackets(packets, action.Button, action.Kind);
        return packets;
    }

    private static IReadOnlyList<SendInputPacket> EncodeMouseWheel(MouseWheelInputAction action)
    {
        var packets = new List<SendInputPacket>();
        if (action.Vertical != 0)
        {
            packets.Add(new SendInputPacket(
                SendInputPacketKind.Mouse,
                0,
                RuntimeNativeMethods.MouseEventFWheel,
                0,
                0,
                unchecked((uint)(action.Vertical * WheelDelta))));
        }

        if (action.Horizontal != 0)
        {
            packets.Add(new SendInputPacket(
                SendInputPacketKind.Mouse,
                0,
                RuntimeNativeMethods.MouseEventFHWheel,
                0,
                0,
                unchecked((uint)(action.Horizontal * WheelDelta))));
        }

        return packets;
    }

    private static IReadOnlyList<SendInputPacket> EncodeConsumer(ConsumerInputAction action)
    {
        var keyUp = action.Kind == ButtonActionKind.Up;
        if (action.Kind == ButtonActionKind.Click)
        {
            var virtualKey = MapConsumer(action.Control);
            return [KeyboardPacket(virtualKey, keyUp: false), KeyboardPacket(virtualKey, keyUp: true)];
        }

        return [KeyboardPacket(MapConsumer(action.Control), keyUp)];
    }

    public static VirtualDesktopBounds GetVirtualDesktopBounds()
    {
        return new VirtualDesktopBounds(
            RuntimeNativeMethods.GetSystemMetrics(RuntimeNativeMethods.SmXVirtualScreen),
            RuntimeNativeMethods.GetSystemMetrics(RuntimeNativeMethods.SmYVirtualScreen),
            RuntimeNativeMethods.GetSystemMetrics(RuntimeNativeMethods.SmCxVirtualScreen),
            RuntimeNativeMethods.GetSystemMetrics(RuntimeNativeMethods.SmCyVirtualScreen));
    }

    private static int NormalizeAbsoluteCoordinate(int value, int origin, int size)
    {
        if (size <= 1)
        {
            return 0;
        }

        return (int)Math.Round((value - origin) * 65535.0 / (size - 1), MidpointRounding.AwayFromZero);
    }

    private static void AddMouseButtonPackets(List<SendInputPacket> packets, MouseButton button, ButtonActionKind kind)
    {
        var unsupported = button & (MouseButton.Button6 | MouseButton.Button7 | MouseButton.Button8);
        if (unsupported != MouseButton.None)
        {
            throw new NotSupportedException($"SendInput supports left, right, middle, X1, and X2 mouse buttons. Unsupported buttons: {unsupported}.");
        }

        var keyUp = kind == ButtonActionKind.Up;
        AddIfPresent(packets, button, MouseButton.Left, keyUp ? RuntimeNativeMethods.MouseEventFLeftUp : RuntimeNativeMethods.MouseEventFLeftDown, 0);
        AddIfPresent(packets, button, MouseButton.Right, keyUp ? RuntimeNativeMethods.MouseEventFRightUp : RuntimeNativeMethods.MouseEventFRightDown, 0);
        AddIfPresent(packets, button, MouseButton.Middle, keyUp ? RuntimeNativeMethods.MouseEventFMiddleUp : RuntimeNativeMethods.MouseEventFMiddleDown, 0);
        AddIfPresent(packets, button, MouseButton.X1, keyUp ? RuntimeNativeMethods.MouseEventFXUp : RuntimeNativeMethods.MouseEventFXDown, XButton1);
        AddIfPresent(packets, button, MouseButton.X2, keyUp ? RuntimeNativeMethods.MouseEventFXUp : RuntimeNativeMethods.MouseEventFXDown, XButton2);
    }

    private static void AddIfPresent(
        List<SendInputPacket> packets,
        MouseButton actual,
        MouseButton expected,
        uint flags,
        uint mouseData)
    {
        if ((actual & expected) == 0)
        {
            return;
        }

        packets.Add(new SendInputPacket(SendInputPacketKind.Mouse, 0, flags, 0, 0, mouseData));
    }

    private static SendInputPacket KeyboardPacket(ushort virtualKey, bool keyUp)
    {
        var flags = keyUp ? RuntimeNativeMethods.KeyEventFKeyUp : 0u;
        if (IsExtendedVirtualKey(virtualKey))
        {
            flags |= RuntimeNativeMethods.KeyEventFExtendedKey;
        }

        return new SendInputPacket(SendInputPacketKind.Keyboard, virtualKey, flags, 0, 0, 0);
    }

    private static SendInputPacket UnicodePacket(char value, bool keyUp)
    {
        var flags = RuntimeNativeMethods.KeyEventFUnicode;
        if (keyUp)
        {
            flags |= RuntimeNativeMethods.KeyEventFKeyUp;
        }

        return new SendInputPacket(SendInputPacketKind.Keyboard, 0, flags, 0, 0, 0, value);
    }

    private static IEnumerable<ushort> ModifierVirtualKeys(HidModifier modifiers)
    {
        if ((modifiers & HidModifier.LeftCtrl) != 0) yield return 0xA2;
        if ((modifiers & HidModifier.LeftShift) != 0) yield return 0xA0;
        if ((modifiers & HidModifier.LeftAlt) != 0) yield return 0xA4;
        if ((modifiers & HidModifier.LeftGui) != 0) yield return 0x5B;
        if ((modifiers & HidModifier.RightCtrl) != 0) yield return 0xA3;
        if ((modifiers & HidModifier.RightShift) != 0) yield return 0xA1;
        if ((modifiers & HidModifier.RightAlt) != 0) yield return 0xA5;
        if ((modifiers & HidModifier.RightGui) != 0) yield return 0x5C;
    }

    private static ushort MapKey(HidKey key)
    {
        if (key is >= HidKey.A and <= HidKey.Z)
        {
            return (ushort)('A' + (key - HidKey.A));
        }

        if (key is >= HidKey.D1 and <= HidKey.D9)
        {
            return (ushort)('1' + (key - HidKey.D1));
        }

        if (key is >= HidKey.F1 and <= HidKey.F24)
        {
            return (ushort)(0x70 + (key - HidKey.F1));
        }

        if (key is >= HidKey.Numpad1 and <= HidKey.Numpad9)
        {
            return (ushort)(0x61 + (key - HidKey.Numpad1));
        }

        return key switch
        {
            HidKey.D0 => 0x30,
            HidKey.Enter or HidKey.Return => 0x0D,
            HidKey.Escape => 0x1B,
            HidKey.Backspace or HidKey.NumpadBackspace => 0x08,
            HidKey.Tab or HidKey.NumpadTab => 0x09,
            HidKey.Space or HidKey.NumpadSpace => 0x20,
            HidKey.Minus => 0xBD,
            HidKey.Equal => 0xBB,
            HidKey.LeftBracket => 0xDB,
            HidKey.RightBracket => 0xDD,
            HidKey.Backslash => 0xDC,
            HidKey.Semicolon => 0xBA,
            HidKey.Quote => 0xDE,
            HidKey.Grave => 0xC0,
            HidKey.Comma => 0xBC,
            HidKey.Period => 0xBE,
            HidKey.Slash => 0xBF,
            HidKey.CapsLock or HidKey.LockingCapsLock => 0x14,
            HidKey.PrintScreen or HidKey.SysReq => 0x2C,
            HidKey.ScrollLock or HidKey.LockingScrollLock => 0x91,
            HidKey.Pause => 0x13,
            HidKey.Insert => 0x2D,
            HidKey.Home => 0x24,
            HidKey.PageUp or HidKey.Prior => 0x21,
            HidKey.Delete => 0x2E,
            HidKey.End => 0x23,
            HidKey.PageDown => 0x22,
            HidKey.RightArrow => 0x27,
            HidKey.LeftArrow => 0x25,
            HidKey.DownArrow => 0x28,
            HidKey.UpArrow => 0x26,
            HidKey.NumLock or HidKey.LockingNumLock => 0x90,
            HidKey.NumpadDivide => 0x6F,
            HidKey.NumpadMultiply => 0x6A,
            HidKey.NumpadMinus => 0x6D,
            HidKey.NumpadPlus => 0x6B,
            HidKey.NumpadEnter => 0x0D,
            HidKey.Numpad0 => 0x60,
            HidKey.NumpadDecimal => 0x6E,
            HidKey.Application or HidKey.Menu => 0x5D,
            HidKey.Power => 0x5F,
            HidKey.Help => 0x2F,
            HidKey.Stop => 0xB2,
            HidKey.Again => 0xF5,
            HidKey.Undo => 0x5A,
            HidKey.Cut => 0x58,
            HidKey.Copy => 0x43,
            HidKey.Paste => 0x56,
            HidKey.Find => 0x46,
            HidKey.Mute => 0xAD,
            HidKey.VolumeUp => 0xAF,
            HidKey.VolumeDown => 0xAE,
            HidKey.LeftControl => 0xA2,
            HidKey.LeftShift => 0xA0,
            HidKey.LeftAlt => 0xA4,
            HidKey.LeftGui => 0x5B,
            HidKey.RightControl => 0xA3,
            HidKey.RightShift => 0xA1,
            HidKey.RightAlt => 0xA5,
            HidKey.RightGui => 0x5C,
            _ => throw new NotSupportedException($"Key '{key}' does not have a SendInput virtual-key mapping.")
        };
    }

    private static ushort MapConsumer(ConsumerControl control)
    {
        return control switch
        {
            ConsumerControl.PlayPause => 0xB3,
            ConsumerControl.ScanNextTrack => 0xB0,
            ConsumerControl.ScanPreviousTrack => 0xB1,
            ConsumerControl.Stop => 0xB2,
            ConsumerControl.Mute => 0xAD,
            ConsumerControl.VolumeUp => 0xAF,
            ConsumerControl.VolumeDown => 0xAE,
            ConsumerControl.BrowserHome => 0xAC,
            ConsumerControl.BrowserBack => 0xA6,
            ConsumerControl.BrowserForward => 0xA7,
            ConsumerControl.BrowserRefresh => 0xA8,
            _ => throw new NotSupportedException($"Consumer control '{control}' does not have a SendInput virtual-key mapping.")
        };
    }

    private static bool IsExtendedVirtualKey(ushort virtualKey)
    {
        return virtualKey is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28
            or 0x2D or 0x2E or 0x5B or 0x5C or 0x5D or 0x6F or 0xA3 or 0xA5;
    }
}
