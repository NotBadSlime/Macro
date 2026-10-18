namespace MacroHid.Core;

public readonly record struct MacroDurationRange(TimeSpan Min, TimeSpan Max);

public static class MacroDurationEstimator
{
    private static readonly MacroDurationRange Zero = new(TimeSpan.Zero, TimeSpan.Zero);

    public static MacroDurationRange EstimateSteps(
        IReadOnlyList<MacroStep> steps,
        Func<string, MacroDocument?>? macroResolver = null)
    {
        var minTicks = 0L;
        var maxTicks = 0L;
        var visiting = new HashSet<MacroDocument>();
        foreach (var step in steps)
        {
            var range = EstimateStep(step, macroResolver, visiting, depth: 0);
            minTicks += range.Min.Ticks;
            maxTicks += range.Max.Ticks;
        }

        return new MacroDurationRange(TimeSpan.FromTicks(minTicks), TimeSpan.FromTicks(maxTicks));
    }

    private static MacroDurationRange EstimateStep(
        MacroStep step,
        Func<string, MacroDocument?>? macroResolver,
        HashSet<MacroDocument> visiting,
        int depth)
    {
        if (depth > 16)
        {
            return Zero;
        }

        return step switch
        {
            KeyStep key => Fixed(key.Hold),
            MouseMoveStep move => Fixed(move.Duration),
            MouseButtonStep button => Fixed(button.Hold),
            OcrClickStep ocrClick => Fixed(TimeSpan.FromTicks(
                ocrClick.Hold.Ticks * Math.Clamp(ocrClick.ClickCount, 1, 3)
                + ocrClick.Interval.Ticks * Math.Max(0, Math.Clamp(ocrClick.ClickCount, 1, 3) - 1))),
            ConsumerStep consumer => Fixed(consumer.Hold),
            WaitStep wait => new MacroDurationRange(wait.Duration, wait.MaxDuration ?? wait.Duration),
            RepeatStep repeat => Multiply(
                EstimateSteps(repeat.Steps, macroResolver, visiting, depth + 1),
                Math.Max(0, repeat.Count)),
            PixelWhenStep pixel => EstimateSteps(pixel.ThenSteps, macroResolver, visiting, depth + 1),
            MacroCallStep macro => EstimateMacroCall(macro, macroResolver, visiting, depth),
            _ => Zero
        };
    }

    private static MacroDurationRange EstimateSteps(
        IReadOnlyList<MacroStep> steps,
        Func<string, MacroDocument?>? macroResolver,
        HashSet<MacroDocument> visiting,
        int depth)
    {
        var minTicks = 0L;
        var maxTicks = 0L;
        foreach (var step in steps)
        {
            var range = EstimateStep(step, macroResolver, visiting, depth);
            minTicks += range.Min.Ticks;
            maxTicks += range.Max.Ticks;
        }

        return new MacroDurationRange(TimeSpan.FromTicks(minTicks), TimeSpan.FromTicks(maxTicks));
    }

    private static MacroDurationRange EstimateMacroCall(
        MacroCallStep macro,
        Func<string, MacroDocument?>? macroResolver,
        HashSet<MacroDocument> visiting,
        int depth)
    {
        if (macroResolver?.Invoke(macro.Macro) is not { } document)
        {
            return Zero;
        }

        if (!visiting.Add(document))
        {
            return Zero;
        }

        try
        {
            return EstimateSteps(document.Steps, macroResolver, visiting, depth + 1);
        }
        finally
        {
            visiting.Remove(document);
        }
    }

    private static MacroDurationRange Fixed(TimeSpan duration) => new(duration, duration);

    private static MacroDurationRange Multiply(MacroDurationRange range, int count)
    {
        return new MacroDurationRange(
            TimeSpan.FromTicks(range.Min.Ticks * count),
            TimeSpan.FromTicks(range.Max.Ticks * count));
    }
}
