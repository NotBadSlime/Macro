using System.Runtime.InteropServices;
using MacroHid.Core;

namespace MacroHid.Runtime;

internal static class BackgroundWindowInput
{
    public static void Submit(IReadOnlyList<InputAction> actions)
    {
        foreach (var action in actions)
        {
            switch (action)
            {
                case KeyInputAction key:
                    PostKey(key);
                    break;
                case TextInputAction text:
                    PostText(text.Text);
                    break;
                case MouseMoveInputAction move:
                    PostMove(move);
                    break;
                case MouseButtonInputAction button:
                    PostButton(button);
                    break;
                case MouseWheelInputAction wheel:
                    PostWheel(wheel);
                    break;
                case ConsumerInputAction consumer:
                    PostConsumer(consumer);
                    break;
            }
        }
    }

    private static void PostKey(KeyInputAction action)
    {
        if (action.Kind == KeyActionKind.Tap)
        {
            PostKey(action with { Kind = KeyActionKind.Down });
            PostKey(action with { Kind = KeyActionKind.Up });
            return;
        }

        foreach (var packet in SendInputEncoder.Encode(action))
        {
            PostKeyboardPacket(packet);
        }
    }

    private static void PostConsumer(ConsumerInputAction action)
    {
        foreach (var packet in SendInputEncoder.Encode(action))
        {
            PostKeyboardPacket(packet);
        }
    }

    private static void PostText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        foreach (var character in text)
        {
            Post(BackgroundMessageKind.Char, character, 0, MouseButton.None, activate: false);
        }
    }

    private static void PostKeyboardPacket(SendInputPacket packet)
    {
        var keyUp = (packet.Flags & RuntimeNativeMethods.KeyEventFKeyUp) != 0;
        if ((packet.Flags & RuntimeNativeMethods.KeyEventFUnicode) != 0)
        {
            if (!keyUp && packet.ScanCode != 0)
            {
                Post(BackgroundMessageKind.Char, packet.ScanCode, 0, MouseButton.None, activate: false);
            }

            return;
        }

        if (packet.VirtualKey == 0)
        {
            return;
        }

        var scan = (ushort)RuntimeNativeMethods.MapVirtualKey(packet.VirtualKey, 0);
        var lParam = 1 | (scan << 16);
        if (IsExtendedVirtualKey(packet.VirtualKey))
        {
            lParam |= 1 << 24;
        }

        if (keyUp)
        {
            lParam |= 1 << 30;
            lParam |= 1 << 31;
            Post(BackgroundMessageKind.KeyUp, packet.VirtualKey, lParam, MouseButton.None, activate: false);
            return;
        }

        Post(BackgroundMessageKind.KeyDown, packet.VirtualKey, lParam, MouseButton.None, activate: true);
    }

    private static void PostMove(MouseMoveInputAction action)
    {
        if (action.Mode == MouseMoveMode.Relative)
        {
            EnsureTargetForeground();
            foreach (var slice in MouseMoveSmoothing.Split(action.Mode, action.X, action.Y, TimeSpan.Zero))
            {
                PostRelativeSlice(slice.X, slice.Y);
            }

            return;
        }

        var window = RequireTarget();
        BackgroundWindowSession.MoveCursor(action.X - window.ClientLeft, action.Y - window.ClientTop);
        PostMouseMove();
    }

    private static void PostRelativeSlice(int dx, int dy)
    {
        var window = RequireTarget();
        var cursor = BackgroundWindowSession.Cursor;
        var nextX = cursor.X + dx;
        var nextY = cursor.Y + dy;
        if (WouldOverflowPostedCoordinate(nextX) || WouldOverflowPostedCoordinate(nextY))
        {
            nextX = (window.ClientWidth / 2) + dx;
            nextY = (window.ClientHeight / 2) + dy;
        }

        BackgroundWindowSession.MoveCursor(nextX, nextY, clampToClient: false);
        PostMouseMove();
        InjectRelativeHardware(dx, dy);
        if (BackgroundWindowSession.TestPost is null)
        {
            Thread.Sleep(8);
        }
    }

    private static void PostMouseMove()
    {
        var cursor = BackgroundWindowSession.Cursor;
        Post(
            BackgroundMessageKind.MouseMove,
            MouseKeyState(BackgroundWindowSession.HeldButtons),
            MouseLParam(cursor.X, cursor.Y),
            MouseButton.None,
            activate: false);
    }

    private static bool WouldOverflowPostedCoordinate(int value)
    {
        return value < short.MinValue + 1 || value > short.MaxValue - 1;
    }

    private static void PostButton(MouseButtonInputAction action)
    {
        if (action.Button == MouseButton.None)
        {
            return;
        }

        if (action.Kind == ButtonActionKind.Click)
        {
            PostButton(action with { Kind = ButtonActionKind.Down });
            PostButton(action with { Kind = ButtonActionKind.Up });
            return;
        }

        var unsupported = action.Button & (MouseButton.Button6 | MouseButton.Button7 | MouseButton.Button8);
        if (unsupported != MouseButton.None)
        {
            throw new NotSupportedException($"后台窗口只接受左键、右键、中键、X1 和 X2。不支持：{unsupported}。");
        }

        var cursor = BackgroundWindowSession.Cursor;
        var held = BackgroundWindowSession.HeldButtons;
        foreach (var button in new[] { MouseButton.Left, MouseButton.Right, MouseButton.Middle, MouseButton.X1, MouseButton.X2 })
        {
            if ((action.Button & button) == 0)
            {
                continue;
            }

            var down = action.Kind == ButtonActionKind.Down;
            held = down ? held | button : held & ~button;
            BackgroundWindowSession.SetHeldButtons(held);
            var kind = down ? BackgroundMessageKind.MouseDown : BackgroundMessageKind.MouseUp;
            var wParam = MouseKeyState(held);
            if (button is MouseButton.X1 or MouseButton.X2)
            {
                wParam |= (button == MouseButton.X1 ? 1 : 2) << 16;
            }

            Post(kind, wParam, MouseLParam(cursor.X, cursor.Y), button, activate: false);
            InjectButtonHardware(button, down);
        }
    }

    private static void PostWheel(MouseWheelInputAction action)
    {
        var window = RequireTarget();
        var cursor = BackgroundWindowSession.Cursor;
        var screenX = window.ClientLeft + cursor.X;
        var screenY = window.ClientTop + cursor.Y;
        if (action.Vertical != 0)
        {
            Post(BackgroundMessageKind.Wheel, (action.Vertical * 120) << 16, MouseLParam(screenX, screenY), MouseButton.None, activate: false);
        }

        if (action.Horizontal != 0)
        {
            Post(BackgroundMessageKind.HorizontalWheel, (action.Horizontal * 120) << 16, MouseLParam(screenX, screenY), MouseButton.None, activate: false);
        }
    }

    private static void Post(BackgroundMessageKind kind, int wParam, int lParam, MouseButton button, bool activate)
    {
        var window = RequireTarget();
        if (activate)
        {
            PostOne(new BackgroundPostedMessage(window.Hwnd, BackgroundMessageKind.Activate, 1, 0, BackgroundWindowSession.Cursor.X, BackgroundWindowSession.Cursor.Y, MouseButton.None));
        }

        var cursor = BackgroundWindowSession.Cursor;
        PostOne(new BackgroundPostedMessage(window.Hwnd, kind, wParam, lParam, cursor.X, cursor.Y, button));
    }

    private static void PostOne(BackgroundPostedMessage message)
    {
        if (BackgroundWindowSession.TestPost is not null)
        {
            if (!BackgroundWindowSession.TryPost(message))
            {
                throw new InvalidOperationException("后台输入没有送进目标窗口。");
            }

            return;
        }

        _ = BackgroundWindowSession.TryPost(message);
        if (message.Kind is BackgroundMessageKind.MouseMove or BackgroundMessageKind.MouseDown or BackgroundMessageKind.MouseUp)
        {
            foreach (var child in WindowMessagePoster.UnityWindows(message.Hwnd))
            {
                WindowMessagePoster.Post(message with { Hwnd = child });
            }

            return;
        }

        WindowMessagePoster.Send(message);
    }

    private static void EnsureTargetForeground()
    {
        if (BackgroundWindowSession.TestPost is not null)
        {
            return;
        }

        var hwnd = RequireTarget().Hwnd;
        if (hwnd == 0 || RuntimeNativeMethods.GetForegroundWindow() == hwnd)
        {
            return;
        }

        RuntimeNativeMethods.SetForegroundWindow(hwnd);
    }

    private static void InjectButtonHardware(MouseButton button, bool down)
    {
        if (BackgroundWindowSession.TestPost is not null || button == MouseButton.None)
        {
            return;
        }

        EnsureTargetForeground();
        EnsureSystemCursorInClient();
        var kind = down ? ButtonActionKind.Down : ButtonActionKind.Up;
        InjectPrepared([new MouseButtonInputAction(button, kind)]);
    }

    private static void InjectRelativeHardware(int dx, int dy)
    {
        if (BackgroundWindowSession.TestPost is not null)
        {
            BackgroundWindowSession.TestInjectRelative?.Invoke(dx, dy);
            return;
        }

        EnsureTargetForeground();
        EnsureSystemCursorInClient();
        if (MouseCursorUnclip.WouldLeaveMovementBounds(dx, dy)
            && (BackgroundWindowSession.HeldButtons & MouseButton.Left) != 0)
        {
            InjectPrepared([new MouseButtonInputAction(MouseButton.Left, ButtonActionKind.Up)]);
            MouseCursorUnclip.RecenterIfNeeded(dx, dy);
            InjectPrepared([new MouseButtonInputAction(MouseButton.Left, ButtonActionKind.Down)]);
        }
        else
        {
            MouseCursorUnclip.RecenterIfNeeded(dx, dy);
        }

        InjectPrepared([new MouseMoveInputAction(MouseMoveMode.Relative, dx, dy)]);
    }

    private static void EnsureSystemCursorInClient()
    {
        var window = RequireTarget();
        if (!RuntimeNativeMethods.GetCursorPos(out var point))
        {
            return;
        }

        if (point.X >= window.ClientLeft
            && point.X < window.ClientLeft + window.ClientWidth
            && point.Y >= window.ClientTop
            && point.Y < window.ClientTop + window.ClientHeight)
        {
            return;
        }

        RuntimeNativeMethods.SetCursorPos(
            window.ClientLeft + window.ClientWidth / 2,
            window.ClientTop + window.ClientHeight / 2);
    }

    private static void InjectPrepared(InputAction[] actions)
    {
        var prepared = PreparedInputBatch.FromActions(actions);
        if (prepared.NativeInputCount == 0)
        {
            return;
        }

        RuntimeNativeMethods.SendInput(
            (uint)prepared.NativeInputCount,
            prepared.NativeInputs,
            Marshal.SizeOf<NativeInput>());
    }

    private static BackgroundWindowTarget RequireTarget()
    {
        return BackgroundWindowSession.CurrentTarget
            ?? throw new InvalidOperationException("后台输入没有送进目标窗口。");
    }

    private static int MouseLParam(int x, int y)
    {
        return (y << 16) | (x & 0xFFFF);
    }

    private static int MouseKeyState(MouseButton buttons)
    {
        var state = 0;
        if ((buttons & MouseButton.Left) != 0) state |= 0x0001;
        if ((buttons & MouseButton.Right) != 0) state |= 0x0002;
        if ((buttons & MouseButton.Middle) != 0) state |= 0x0010;
        if ((buttons & MouseButton.X1) != 0) state |= 0x0020;
        if ((buttons & MouseButton.X2) != 0) state |= 0x0040;
        return state;
    }

    private static bool IsExtendedVirtualKey(ushort virtualKey)
    {
        return virtualKey is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28
            or 0x2D or 0x2E or 0x5B or 0x5C or 0x5D or 0x6F or 0xA3 or 0xA5;
    }
}

internal static class WindowMessagePoster
{
    public static bool Post(BackgroundPostedMessage message)
    {
        if (message.Hwnd == 0)
        {
            return false;
        }

        var (messageId, wParam, lParam) = ToNative(message);
        return PostMessage(message.Hwnd, messageId, (nint)wParam, (nint)lParam);
    }

    public static void Send(BackgroundPostedMessage message)
    {
        if (message.Hwnd == 0)
        {
            return;
        }

        var (messageId, wParam, lParam) = ToNative(message);
        SendMessageTimeout(
            message.Hwnd,
            messageId,
            (nint)wParam,
            (nint)lParam,
            SmtoAbortIfHung | SmtoBlock,
            10,
            out _);
    }

    public static IEnumerable<nint> UnityWindows(nint parent)
    {
        if (parent == 0)
        {
            yield break;
        }

        nint child = 0;
        while (true)
        {
            child = FindWindowEx(parent, child, "UnityWndClass", null);
            if (child == 0)
            {
                yield break;
            }

            if (child != parent)
            {
                yield return child;
            }
        }
    }

    private static (uint Message, int WParam, int LParam) ToNative(BackgroundPostedMessage message)
    {
        return message.Kind switch
        {
            BackgroundMessageKind.Activate => (WmActivate, message.WParam, message.LParam),
            BackgroundMessageKind.KeyDown => (WmKeyDown, message.WParam, message.LParam),
            BackgroundMessageKind.KeyUp => (WmKeyUp, message.WParam, message.LParam),
            BackgroundMessageKind.Char => (WmChar, message.WParam, message.LParam),
            BackgroundMessageKind.MouseMove => (WmMouseMove, message.WParam, message.LParam),
            BackgroundMessageKind.MouseDown => (MouseDownMessage(message.Button), message.WParam, message.LParam),
            BackgroundMessageKind.MouseUp => (MouseUpMessage(message.Button), message.WParam, message.LParam),
            BackgroundMessageKind.Wheel => (WmMouseWheel, message.WParam, message.LParam),
            BackgroundMessageKind.HorizontalWheel => (WmMouseHWheel, message.WParam, message.LParam),
            _ => (0, 0, 0)
        };
    }

    private static uint MouseDownMessage(MouseButton button)
    {
        return button switch
        {
            MouseButton.Right => WmRButtonDown,
            MouseButton.Middle => WmMButtonDown,
            MouseButton.X1 or MouseButton.X2 => WmXButtonDown,
            _ => WmLButtonDown
        };
    }

    private static uint MouseUpMessage(MouseButton button)
    {
        return button switch
        {
            MouseButton.Right => WmRButtonUp,
            MouseButton.Middle => WmMButtonUp,
            MouseButton.X1 or MouseButton.X2 => WmXButtonUp,
            _ => WmLButtonUp
        };
    }

    private const uint WmActivate = 0x0006;
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint WmChar = 0x0102;
    private const uint WmMouseMove = 0x0200;
    private const uint WmLButtonDown = 0x0201;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmRButtonDown = 0x0204;
    private const uint WmRButtonUp = 0x0205;
    private const uint WmMButtonDown = 0x0207;
    private const uint WmMButtonUp = 0x0208;
    private const uint WmMouseWheel = 0x020A;
    private const uint WmXButtonDown = 0x020B;
    private const uint WmXButtonUp = 0x020C;
    private const uint WmMouseHWheel = 0x020E;
    private const uint SmtoBlock = 0x0001;
    private const uint SmtoAbortIfHung = 0x0002;

    [DllImport("user32.dll")]
    private static extern bool PostMessage(nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowEx(nint parent, nint childAfter, string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessageTimeout(
        nint hwnd,
        uint message,
        nint wParam,
        nint lParam,
        uint flags,
        uint timeoutMilliseconds,
        out nint result);
}
