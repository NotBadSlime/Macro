namespace MacroHid.Core;

public sealed record ScheduledMacroStep(MacroStep Step, long DueTick);

public static class MacroScheduler
{
    public static IReadOnlyList<ScheduledMacroStep> Compile(
        MacroDocument document,
        long startTick,
        long qpcFrequency)
    {
        if (qpcFrequency <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(qpcFrequency), "QPC frequency must be positive.");
        }

        var scheduled = new List<ScheduledMacroStep>();
        var elapsedTicks = 0L;
        CompileSteps(document.Steps, scheduled, startTick, qpcFrequency, ref elapsedTicks);
        return scheduled;
    }

    private static void CompileSteps(
        IReadOnlyList<MacroStep> steps,
        List<ScheduledMacroStep> scheduled,
        long startTick,
        long qpcFrequency,
        ref long elapsedTicks)
    {
        foreach (var step in steps)
        {
            switch (step)
            {
                case WaitStep wait:
                    elapsedTicks += ToTicks(wait.Duration, qpcFrequency);
                    break;
                case RepeatStep repeat:
                    for (var i = 0; i < repeat.Count; i++)
                    {
                        CompileSteps(repeat.Steps, scheduled, startTick, qpcFrequency, ref elapsedTicks);
                    }

                    break;
                default:
                    scheduled.Add(new ScheduledMacroStep(step, startTick + elapsedTicks));
                    break;
            }
        }
    }

    private static long ToTicks(TimeSpan duration, long qpcFrequency)
    {
        return (long)Math.Round(duration.TotalSeconds * qpcFrequency, MidpointRounding.AwayFromZero);
    }
}
