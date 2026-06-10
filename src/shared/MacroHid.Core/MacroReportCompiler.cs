namespace MacroHid.Core;

public sealed record ScheduledHidReport(byte ReportId, byte[] Report, long DueTick, MacroStep SourceStep);

public static class MacroReportCompiler
{
    public static IReadOnlyList<ScheduledHidReport> Compile(
        MacroDocument document,
        long startTick,
        long qpcFrequency,
        Func<PixelCondition, bool>? pixelEvaluator = null)
    {
        if (qpcFrequency <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(qpcFrequency), "QPC frequency must be positive.");
        }

        var reports = new List<ScheduledHidReport>();
        var elapsedTicks = 0L;
        CompileSteps(document.Steps, reports, startTick, qpcFrequency, pixelEvaluator, ref elapsedTicks);
        return reports;
    }

    private static void CompileSteps(
        IReadOnlyList<MacroStep> steps,
        List<ScheduledHidReport> reports,
        long startTick,
        long qpcFrequency,
        Func<PixelCondition, bool>? pixelEvaluator,
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
                        CompileSteps(repeat.Steps, reports, startTick, qpcFrequency, pixelEvaluator, ref elapsedTicks);
                    }

                    break;
                case PixelWhenStep pixel:
                    if (pixelEvaluator?.Invoke(pixel.Condition) == true)
                    {
                        CompileSteps(pixel.ThenSteps, reports, startTick, qpcFrequency, pixelEvaluator, ref elapsedTicks);
                    }

                    break;
                case KeyStep key:
                    CompileKey(key, reports, startTick, qpcFrequency, ref elapsedTicks);
                    break;
                case MouseButtonStep button:
                    CompileMouseButton(button, reports, startTick, qpcFrequency, ref elapsedTicks);
                    break;
                case MouseMoveStep move:
                    AddReport(reports, HidReportEncoder.EncodeMouseMove(move), startTick + elapsedTicks, move);
                    elapsedTicks += ToTicks(move.Duration, qpcFrequency);
                    break;
                case MouseWheelStep wheel:
                    AddReport(reports, HidReportEncoder.EncodeMouseWheel(wheel), startTick + elapsedTicks, wheel);
                    break;
                case ConsumerStep consumer:
                    CompileConsumer(consumer, reports, startTick, qpcFrequency, ref elapsedTicks);
                    break;
                default:
                    throw new NotSupportedException($"Unsupported macro step '{step.GetType().Name}'.");
            }
        }
    }

    private static void CompileKey(
        KeyStep step,
        List<ScheduledHidReport> reports,
        long startTick,
        long qpcFrequency,
        ref long elapsedTicks)
    {
        if (step.Kind == KeyActionKind.Tap)
        {
            AddReport(reports, HidReportEncoder.EncodeKeyboard(step), startTick + elapsedTicks, step);
            elapsedTicks += ToTicks(step.Hold, qpcFrequency);
            AddReport(reports, HidReportEncoder.EncodeKeyboard(step with { Kind = KeyActionKind.Up }), startTick + elapsedTicks, step);
            return;
        }

        AddReport(reports, HidReportEncoder.EncodeKeyboard(step), startTick + elapsedTicks, step);
    }

    private static void CompileMouseButton(
        MouseButtonStep step,
        List<ScheduledHidReport> reports,
        long startTick,
        long qpcFrequency,
        ref long elapsedTicks)
    {
        if (step.Kind == ButtonActionKind.Click)
        {
            AddReport(reports, HidReportEncoder.EncodeMouseButton(step with { Kind = ButtonActionKind.Down }), startTick + elapsedTicks, step);
            elapsedTicks += ToTicks(step.Hold, qpcFrequency);
            AddReport(reports, HidReportEncoder.EncodeMouseButton(step with { Kind = ButtonActionKind.Up }), startTick + elapsedTicks, step);
            return;
        }

        AddReport(reports, HidReportEncoder.EncodeMouseButton(step), startTick + elapsedTicks, step);
    }

    private static void CompileConsumer(
        ConsumerStep step,
        List<ScheduledHidReport> reports,
        long startTick,
        long qpcFrequency,
        ref long elapsedTicks)
    {
        if (step.Kind == ButtonActionKind.Click)
        {
            AddReport(reports, HidReportEncoder.EncodeConsumer(step.Control), startTick + elapsedTicks, step);
            elapsedTicks += ToTicks(step.Hold, qpcFrequency);
            AddReport(reports, HidReportEncoder.EncodeConsumerRelease(), startTick + elapsedTicks, step);
            return;
        }

        var report = step.Kind == ButtonActionKind.Up
            ? HidReportEncoder.EncodeConsumerRelease()
            : HidReportEncoder.EncodeConsumer(step.Control);
        AddReport(reports, report, startTick + elapsedTicks, step);
    }

    private static void AddReport(List<ScheduledHidReport> reports, byte[] report, long dueTick, MacroStep sourceStep)
    {
        reports.Add(new ScheduledHidReport(report[0], report, dueTick, sourceStep));
    }

    private static long ToTicks(TimeSpan duration, long qpcFrequency)
    {
        return (long)Math.Round(duration.TotalSeconds * qpcFrequency, MidpointRounding.AwayFromZero);
    }
}
