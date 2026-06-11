using MacroHid.Core;

namespace MacroHid.Runtime;

public interface IPlaybackDelayStrategy
{
    void WaitUntil(long dueTick, long qpcFrequency, CancellationToken cancellationToken, bool noWait);
}

public sealed class QpcPlaybackDelayStrategy : IPlaybackDelayStrategy
{
    public void WaitUntil(long dueTick, long qpcFrequency, CancellationToken cancellationToken, bool noWait)
    {
        if (noWait)
        {
            return;
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RuntimeNativeMethods.QueryPerformanceCounter(out var now);
            var remainingTicks = dueTick - now;
            if (remainingTicks <= 0)
            {
                return;
            }

            var remainingUs = ToMicroseconds(remainingTicks, qpcFrequency);
            if (remainingUs > 2_000)
            {
                Thread.Sleep(Math.Max(0, (int)(remainingUs / 1_000) - 1));
            }
            else if (remainingUs > 200)
            {
                Thread.Yield();
            }
            else
            {
                Thread.SpinWait(64);
            }
        }
    }

    private static long ToMicroseconds(long ticks, long qpcFrequency)
    {
        return (long)Math.Round(ticks * 1_000_000.0 / qpcFrequency, MidpointRounding.AwayFromZero);
    }
}

public interface IMacroReportSink
{
    bool IsAvailable { get; }

    void Submit(uint sequence, byte[] report);

    MacroDriverStats? GetStats();
}

public sealed class MacroPlaybackExecutor : IMacroPlaybackExecutor
{
    private readonly IMacroReportSink reportSink;
    private readonly IPlaybackDelayStrategy delayStrategy;
    private readonly Func<PixelCondition, bool> livePixelEvaluator;

    public MacroPlaybackExecutor(
        IMacroReportSink reportSink,
        IPlaybackDelayStrategy? delayStrategy = null,
        Func<PixelCondition, bool>? livePixelEvaluator = null)
    {
        this.reportSink = reportSink;
        this.delayStrategy = delayStrategy ?? new QpcPlaybackDelayStrategy();
        this.livePixelEvaluator = livePixelEvaluator ?? ScreenPixelSampler.Matches;
    }

    public Task<PlaybackRunResult> RunAsync(
        MacroDocument document,
        PlaybackExecutionOptions options,
        CancellationToken cancellationToken)
    {
        return Task.Run(() => Run(document, options, cancellationToken), cancellationToken);
    }

    private PlaybackRunResult Run(
        MacroDocument document,
        PlaybackExecutionOptions options,
        CancellationToken cancellationToken)
    {
        if (options.Count < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Playback count must be at least 1.");
        }

        if (!reportSink.IsAvailable)
        {
            return new PlaybackRunResult(PlaybackRunStatus.DriverMissing, 0, 0, Cancelled: false, DriverStats: null);
        }

        RuntimeNativeMethods.QueryPerformanceFrequency(out var qpcFrequency);
        var iterationsTarget = options.Mode == PlaybackMode.FixedCount ? options.Count : int.MaxValue;
        var iterationsCompleted = 0;
        var reportsSubmitted = 0;
        var sequence = 1u;

        try
        {
            while (iterationsCompleted < iterationsTarget)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var reports = MacroReportCompiler.Compile(
                    document,
                    startTick: 0,
                    qpcFrequency,
                    GetPixelEvaluator(options.PixelMode));
                RuntimeNativeMethods.QueryPerformanceCounter(out var iterationStartTick);

                foreach (var report in reports)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    delayStrategy.WaitUntil(iterationStartTick + report.DueTick, qpcFrequency, cancellationToken, options.NoWait);
                    cancellationToken.ThrowIfCancellationRequested();
                    reportSink.Submit(sequence++, report.Report);
                    reportsSubmitted++;
                }

                iterationsCompleted++;
            }

            return new PlaybackRunResult(
                PlaybackRunStatus.Completed,
                iterationsCompleted,
                reportsSubmitted,
                Cancelled: false,
                reportSink.GetStats());
        }
        catch (OperationCanceledException)
        {
            SubmitSafeReleaseReports(ref sequence, ref reportsSubmitted);
            return new PlaybackRunResult(
                PlaybackRunStatus.Completed,
                iterationsCompleted,
                reportsSubmitted,
                Cancelled: true,
                reportSink.GetStats());
        }
    }

    private Func<PixelCondition, bool> GetPixelEvaluator(PixelEvaluationMode mode)
    {
        return mode switch
        {
            PixelEvaluationMode.Skip => _ => false,
            PixelEvaluationMode.MatchAll => _ => true,
            PixelEvaluationMode.Live => livePixelEvaluator,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported pixel evaluation mode.")
        };
    }

    private void SubmitSafeReleaseReports(ref uint sequence, ref int reportsSubmitted)
    {
        foreach (var report in SafeReleaseReports())
        {
            reportSink.Submit(sequence++, report);
            reportsSubmitted++;
        }
    }

    private static IEnumerable<byte[]> SafeReleaseReports()
    {
        yield return [HidReportEncoder.KeyboardReportId, 0, 0, 0, 0, 0, 0, 0, 0];
        yield return [HidReportEncoder.MouseReportId, 0, 0, 0, 0, 0, 0, 0];
        yield return [HidReportEncoder.ConsumerReportId, 0, 0];
    }
}
