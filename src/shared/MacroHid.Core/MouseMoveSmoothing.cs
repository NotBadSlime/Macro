namespace MacroHid.Core;

public readonly record struct MouseMoveSlice(MouseMoveMode Mode, int X, int Y, TimeSpan Offset);

public static class MouseMoveSmoothing
{
    public static readonly TimeSpan SliceInterval = TimeSpan.FromMilliseconds(8);
    public static readonly int MaxDeltaPerSlice = 96;

    public static IReadOnlyList<MouseMoveSlice> Split(MouseMoveMode mode, int x, int y, TimeSpan duration)
    {
        if (mode != MouseMoveMode.Relative || (x == 0 && y == 0))
        {
            return [new MouseMoveSlice(mode, x, y, TimeSpan.Zero)];
        }

        if (duration < SliceInterval)
        {
            return SplitDelta(x, y, TimeSpan.Zero);
        }

        var steps = Math.Max(2, (int)Math.Ceiling(duration.TotalMilliseconds / SliceInterval.TotalMilliseconds));
        var slices = new List<MouseMoveSlice>();
        var sentX = 0;
        var sentY = 0;
        for (var i = 1; i <= steps; i++)
        {
            var progress = EaseOut(i / (double)steps);
            var targetX = (int)Math.Round(x * progress, MidpointRounding.AwayFromZero);
            var targetY = (int)Math.Round(y * progress, MidpointRounding.AwayFromZero);
            var dx = targetX - sentX;
            var dy = targetY - sentY;
            sentX = targetX;
            sentY = targetY;
            if (dx == 0 && dy == 0)
            {
                continue;
            }

            var offset = TimeSpan.FromMilliseconds(duration.TotalMilliseconds * (i - 1) / steps);
            slices.AddRange(SplitDelta(dx, dy, offset));
        }

        return slices.Count == 0
            ? [new MouseMoveSlice(mode, x, y, TimeSpan.Zero)]
            : slices;
    }

    private static IReadOnlyList<MouseMoveSlice> SplitDelta(int x, int y, TimeSpan offset)
    {
        var distance = Math.Max(Math.Abs(x), Math.Abs(y));
        if (distance <= MaxDeltaPerSlice)
        {
            return [new MouseMoveSlice(MouseMoveMode.Relative, x, y, offset)];
        }

        var parts = Math.Max(2, (int)Math.Ceiling(distance / (double)MaxDeltaPerSlice));
        var slices = new List<MouseMoveSlice>(parts);
        var sentX = 0;
        var sentY = 0;
        for (var i = 1; i <= parts; i++)
        {
            var targetX = (int)Math.Round(x * (i / (double)parts), MidpointRounding.AwayFromZero);
            var targetY = (int)Math.Round(y * (i / (double)parts), MidpointRounding.AwayFromZero);
            var dx = targetX - sentX;
            var dy = targetY - sentY;
            sentX = targetX;
            sentY = targetY;
            if (dx == 0 && dy == 0)
            {
                continue;
            }

            slices.Add(new MouseMoveSlice(MouseMoveMode.Relative, dx, dy, offset));
        }

        return slices.Count == 0
            ? [new MouseMoveSlice(MouseMoveMode.Relative, x, y, offset)]
            : slices;
    }

    private static double EaseOut(double t)
    {
        var remaining = 1 - Math.Clamp(t, 0, 1);
        return 1 - (remaining * remaining * remaining);
    }
}
