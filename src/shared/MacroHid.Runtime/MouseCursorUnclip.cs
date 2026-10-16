namespace MacroHid.Runtime;

internal static class MouseCursorUnclip
{
    private const int EdgeMargin = 16;

    public static bool WouldLeaveMovementBounds(int dx, int dy) =>
        TryGetMove(dx, dy, out _, out _, out _, out _, out var leaves) && leaves;

    public static void RecenterIfNeeded(int dx, int dy)
    {
        // One cursor and bounds query per relative move (this used to query the bounds twice).
        if (!TryGetMove(dx, dy, out var left, out var top, out var width, out var height, out var leaves) || !leaves)
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

    private static bool TryGetMove(int dx, int dy, out int left, out int top, out int width, out int height, out bool leaves)
    {
        left = top = width = height = 0;
        leaves = false;
        if (dx == 0 && dy == 0 || !RuntimeNativeMethods.GetCursorPos(out var point)
            || !TryGetMovementBounds(out left, out top, out width, out height))
        {
            return false;
        }

        var nextX = point.X + dx;
        var nextY = point.Y + dy;
        leaves = nextX < left + EdgeMargin
            || nextX > left + width - 1 - EdgeMargin
            || nextY < top + EdgeMargin
            || nextY > top + height - 1 - EdgeMargin;
        return true;
    }

    private static bool TryGetMovementBounds(out int left, out int top, out int width, out int height)
    {
        // GetClipCursor reports the virtual screen when nothing clips, so the system metrics are only a fallback.
        if (RuntimeNativeMethods.GetClipCursor(out var clip) && clip.Right - clip.Left > 1 && clip.Bottom - clip.Top > 1)
        {
            left = clip.Left;
            top = clip.Top;
            width = clip.Right - clip.Left;
            height = clip.Bottom - clip.Top;
            return true;
        }

        left = RuntimeNativeMethods.GetSystemMetrics(RuntimeNativeMethods.SmXVirtualScreen);
        top = RuntimeNativeMethods.GetSystemMetrics(RuntimeNativeMethods.SmYVirtualScreen);
        width = RuntimeNativeMethods.GetSystemMetrics(RuntimeNativeMethods.SmCxVirtualScreen);
        height = RuntimeNativeMethods.GetSystemMetrics(RuntimeNativeMethods.SmCyVirtualScreen);
        return width > 1 && height > 1;
    }
}
