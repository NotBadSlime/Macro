namespace MacroHid.Runtime;

internal static class MouseCursorUnclip
{
    private const int EdgeMargin = 16;

    public static bool WouldLeaveMovementBounds(int dx, int dy)
    {
        if (dx == 0 && dy == 0 || !RuntimeNativeMethods.GetCursorPos(out var point))
        {
            return false;
        }

        if (!TryGetMovementBounds(out var left, out var top, out var width, out var height))
        {
            return false;
        }

        var nextX = point.X + dx;
        var nextY = point.Y + dy;
        return nextX < left + EdgeMargin
            || nextX > left + width - 1 - EdgeMargin
            || nextY < top + EdgeMargin
            || nextY > top + height - 1 - EdgeMargin;
    }

    public static void RecenterIfNeeded(int dx, int dy)
    {
        if (!WouldLeaveMovementBounds(dx, dy))
        {
            return;
        }

        if (!TryGetMovementBounds(out var left, out var top, out var width, out var height))
        {
            return;
        }

        RuntimeNativeMethods.ClipCursor(0);
        RuntimeNativeMethods.SetCursorPos(left + width / 2, top + height / 2);
    }

    public static bool IsRelativeMouse(NativeInput input)
    {
        return input.Type == RuntimeNativeMethods.InputMouse
            && (input.Union.Mouse.Flags & RuntimeNativeMethods.MouseEventFMove) != 0
            && (input.Union.Mouse.Flags & RuntimeNativeMethods.MouseEventFAbsolute) == 0;
    }

    private static bool TryGetMovementBounds(out int left, out int top, out int width, out int height)
    {
        left = RuntimeNativeMethods.GetSystemMetrics(RuntimeNativeMethods.SmXVirtualScreen);
        top = RuntimeNativeMethods.GetSystemMetrics(RuntimeNativeMethods.SmYVirtualScreen);
        width = RuntimeNativeMethods.GetSystemMetrics(RuntimeNativeMethods.SmCxVirtualScreen);
        height = RuntimeNativeMethods.GetSystemMetrics(RuntimeNativeMethods.SmCyVirtualScreen);
        if (RuntimeNativeMethods.GetClipCursor(out var clip))
        {
            var clipWidth = clip.Right - clip.Left;
            var clipHeight = clip.Bottom - clip.Top;
            if (clipWidth > 1 && clipHeight > 1)
            {
                left = clip.Left;
                top = clip.Top;
                width = clipWidth;
                height = clipHeight;
            }
        }

        return width > 1 && height > 1;
    }
}
