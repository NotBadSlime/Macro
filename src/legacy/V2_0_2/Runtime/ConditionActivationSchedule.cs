using MacroHid.Core;

namespace MacroHid.Runtime;

public sealed record StepTimeWindow(int Index, IReadOnlyList<int> Path, long StartTicks, long EndTicks);

/// <summary>
/// One inclusive activation window relative to <see cref="Anchor"/>.
/// <see cref="End"/> null means open-ended.
/// </summary>
public readonly record struct ConditionTimeInterval(
    TimeSpan Start,
    TimeSpan? End,
    ConditionTimeBase Anchor = ConditionTimeBase.PlaybackTrigger);

/// <summary>
/// Builds OR activation intervals from step range and/or explicit time window.
/// </summary>
public static class ConditionActivationSchedule
{
    public static IReadOnlyList<ConditionTimeInterval> Resolve(
        ConditionalDirective directive,
        IReadOnlyList<StepTimeWindow> stepTimeWindows,
        long qpcFrequency)
    {
        var intervals = new List<ConditionTimeInterval>(2);

        if (directive.HasStepRange && stepTimeWindows.Count > 0)
        {
            var startWindow = FindStepTimeWindow(stepTimeWindows, directive.StartStepPath, directive.StartStepIndex);
            var endWindow = FindStepTimeWindow(stepTimeWindows, directive.EndStepPath, directive.EndStepIndex) ?? startWindow;
            if (startWindow is not null && endWindow is not null)
            {
                var stepStart = ToTimeSpan(startWindow.StartTicks, qpcFrequency);
                var stepEnd = ToTimeSpan(Math.Max(endWindow.EndTicks, startWindow.StartTicks), qpcFrequency);
                if (stepEnd <= stepStart)
                {
                    stepEnd = stepStart + directive.EffectivePollInterval;
                }

                intervals.Add(new ConditionTimeInterval(
                    stepStart,
                    stepEnd,
                    ConditionTimeBase.MainIteration));
            }
        }

        if (directive.HasTimeRange)
        {
            var timeStart = directive.WindowStart ?? TimeSpan.Zero;
            var timeEnd = directive.WindowEnd;
            if (timeEnd is { } end && end <= timeStart)
            {
                timeEnd = timeStart + directive.EffectivePollInterval;
            }

            intervals.Add(new ConditionTimeInterval(timeStart, timeEnd, directive.TimeBase));
        }

        return MergeOverlapping(intervals);
    }

    /// <summary>
    /// Startup gates always wait from the resolved trigger/base tick until the matcher succeeds.
    /// Step range and explicit time windows do not apply.
    /// </summary>
    public static IReadOnlyList<ConditionTimeInterval> ResolveStartupGate()
        => [new ConditionTimeInterval(TimeSpan.Zero, null)];

    public static bool Contains(IReadOnlyList<ConditionTimeInterval> intervals, double elapsedMs)
    {
        foreach (var interval in intervals)
        {
            if (elapsedMs < interval.Start.TotalMilliseconds)
            {
                continue;
            }

            if (interval.End is { } end && elapsedMs > end.TotalMilliseconds)
            {
                continue;
            }

            return true;
        }

        return false;
    }

    public static bool IsPastAll(IReadOnlyList<ConditionTimeInterval> intervals, double elapsedMs)
    {
        if (intervals.Count == 0)
        {
            return true;
        }

        foreach (var interval in intervals)
        {
            if (interval.End is null)
            {
                return false;
            }

            if (elapsedMs <= interval.End.Value.TotalMilliseconds)
            {
                return false;
            }
        }

        return true;
    }

    public static TimeSpan? NextStartAfter(IReadOnlyList<ConditionTimeInterval> intervals, double elapsedMs)
    {
        TimeSpan? next = null;
        foreach (var interval in intervals)
        {
            if (elapsedMs < interval.Start.TotalMilliseconds)
            {
                if (next is null || interval.Start < next)
                {
                    next = interval.Start;
                }
            }
        }

        return next;
    }

    public static TimeSpan? LatestEnd(IReadOnlyList<ConditionTimeInterval> intervals)
    {
        TimeSpan? latest = null;
        foreach (var interval in intervals)
        {
            if (interval.End is null)
            {
                return null;
            }

            if (latest is null || interval.End > latest)
            {
                latest = interval.End;
            }
        }

        return latest;
    }

    private static IReadOnlyList<ConditionTimeInterval> MergeOverlapping(List<ConditionTimeInterval> intervals)
    {
        if (intervals.Count <= 1)
        {
            return intervals;
        }

        var merged = new List<ConditionTimeInterval>(intervals.Count);
        foreach (var group in intervals.GroupBy(interval => interval.Anchor))
        {
            merged.AddRange(MergeOverlappingSameAnchor(group.ToList()));
        }

        return merged;
    }

    private static IReadOnlyList<ConditionTimeInterval> MergeOverlappingSameAnchor(List<ConditionTimeInterval> intervals)
    {
        if (intervals.Count <= 1)
        {
            return intervals;
        }

        intervals.Sort((left, right) => left.Start.CompareTo(right.Start));
        var merged = new List<ConditionTimeInterval>(intervals.Count) { intervals[0] };
        for (var i = 1; i < intervals.Count; i++)
        {
            var current = intervals[i];
            var last = merged[^1];
            if (last.End is null)
            {
                continue;
            }

            if (current.Start <= last.End.Value
                || (current.Start - last.End.Value) < TimeSpan.FromMilliseconds(0.001))
            {
                var end = current.End is null
                    ? null
                    : (last.End is { } lastEnd && current.End > lastEnd ? current.End : last.End);
                if (current.End is null)
                {
                    end = null;
                }
                else if (last.End is { } le && current.End is { } ce)
                {
                    end = ce > le ? ce : le;
                }

                merged[^1] = new ConditionTimeInterval(last.Start, end, last.Anchor);
            }
            else
            {
                merged.Add(current);
            }
        }

        return merged;
    }

    private static StepTimeWindow? FindStepTimeWindow(
        IReadOnlyList<StepTimeWindow> windows,
        IReadOnlyList<int>? path,
        int fallbackIndex)
    {
        if (path is { Count: > 0 })
        {
            var match = windows.FirstOrDefault(window => window.Path.SequenceEqual(path));
            if (match is not null)
            {
                return match;
            }
        }

        return fallbackIndex >= 0 && fallbackIndex < windows.Count
            ? windows[fallbackIndex]
            : null;
    }

    private static TimeSpan ToTimeSpan(long ticks, long qpcFrequency)
    {
        if (qpcFrequency <= 0)
        {
            return TimeSpan.Zero;
        }

        return TimeSpan.FromSeconds(ticks / (double)qpcFrequency);
    }
}
