namespace MacroHid.Core;

public readonly record struct DelayBatchAdjustmentResult(
    IReadOnlyList<MacroStep> Steps,
    int DelayCount,
    int AdjustedCount);

public static class DelayBatchAdjustment
{
    public static DelayBatchAdjustmentResult Shift(IReadOnlyList<MacroStep> steps, TimeSpan delta) =>
        Shift(steps, delta, TimeSpan.Zero);

    public static DelayBatchAdjustmentResult Shift(IReadOnlyList<MacroStep> steps, TimeSpan delta, TimeSpan floor)
    {
        if (floor < TimeSpan.Zero)
        {
            floor = TimeSpan.Zero;
        }

        var delayCount = 0;
        var adjustedCount = 0;
        var updated = ShiftList(steps, delta, floor, ref delayCount, ref adjustedCount);
        return new DelayBatchAdjustmentResult(updated, delayCount, adjustedCount);
    }

    private static IReadOnlyList<MacroStep> ShiftList(
        IReadOnlyList<MacroStep> steps,
        TimeSpan delta,
        TimeSpan floor,
        ref int delayCount,
        ref int adjustedCount)
    {
        var changed = false;
        var result = new List<MacroStep>(steps.Count);
        foreach (var step in steps)
        {
            var next = ShiftStep(step, delta, floor, ref delayCount, ref adjustedCount);
            if (!ReferenceEquals(next, step))
            {
                changed = true;
            }

            result.Add(next);
        }

        return changed ? result : steps;
    }

    private static MacroStep ShiftStep(
        MacroStep step,
        TimeSpan delta,
        TimeSpan floor,
        ref int delayCount,
        ref int adjustedCount)
    {
        switch (step)
        {
            case WaitStep wait:
                delayCount++;
                var nextMin = ApplyDelta(wait.Duration, delta, floor);
                var nextMax = wait.MaxDuration is { } max ? ApplyDelta(max, delta, floor) : (TimeSpan?)null;
                if (nextMax is { } capped && capped < nextMin)
                {
                    nextMax = nextMin;
                }

                if (nextMin == wait.Duration && nextMax == wait.MaxDuration)
                {
                    return wait;
                }

                adjustedCount++;
                return new WaitStep(nextMin, nextMax);
            case RepeatStep repeat:
                var repeatSteps = ShiftList(repeat.Steps, delta, floor, ref delayCount, ref adjustedCount);
                return ReferenceEquals(repeatSteps, repeat.Steps) ? repeat : repeat with { Steps = repeatSteps };
            case PixelWhenStep pixel:
                var thenSteps = ShiftList(pixel.ThenSteps, delta, floor, ref delayCount, ref adjustedCount);
                return ReferenceEquals(thenSteps, pixel.ThenSteps) ? pixel : pixel with { ThenSteps = thenSteps };
            case ParallelBranchStep branch:
                var branchSteps = ShiftList(branch.Steps, delta, floor, ref delayCount, ref adjustedCount);
                return ReferenceEquals(branchSteps, branch.Steps) ? branch : branch with { Steps = branchSteps };
            case ParallelStep parallel:
                var left = (ParallelBranchStep)ShiftStep(parallel.Left, delta, floor, ref delayCount, ref adjustedCount);
                var right = (ParallelBranchStep)ShiftStep(parallel.Right, delta, floor, ref delayCount, ref adjustedCount);
                return ReferenceEquals(left, parallel.Left) && ReferenceEquals(right, parallel.Right)
                    ? parallel
                    : parallel with { Left = left, Right = right };
            case TextWhenStep text:
                var textSteps = ShiftList(text.ThenSteps, delta, floor, ref delayCount, ref adjustedCount);
                return ReferenceEquals(textSteps, text.ThenSteps) ? text : text with { ThenSteps = textSteps };
            default:
                return step;
        }
    }

    private static TimeSpan ApplyDelta(TimeSpan value, TimeSpan delta, TimeSpan floor)
    {
        var next = value + delta;
        if (next >= floor)
        {
            return next;
        }

        return value < floor ? value : floor;
    }
}
