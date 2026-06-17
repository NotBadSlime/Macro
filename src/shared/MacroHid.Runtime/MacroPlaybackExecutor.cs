using System.Runtime;
using MacroHid.Core;

namespace MacroHid.Runtime;

public interface IPlaybackDelayStrategy
{
    void WaitUntil(long dueTick, long qpcFrequency, CancellationToken cancellationToken, bool noWait);
}

public sealed class QpcPlaybackDelayStrategy : IPlaybackDelayStrategy
{
    private const long SleepOneThresholdUs = 8_000;
    private const long YieldThresholdUs = 1_500;
    private const long AggressiveSpinThresholdUs = 250;
    private readonly IHighResolutionClock clock;
    private readonly int calibratedSpinIterations;

    public QpcPlaybackDelayStrategy(IHighResolutionClock? clock = null)
    {
        this.clock = clock ?? new QpcHighResolutionClock();
        calibratedSpinIterations = CalibrateSpinCount();
    }

    public void WaitUntil(long dueTick, long qpcFrequency, CancellationToken cancellationToken, bool noWait)
    {
        if (noWait)
        {
            return;
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = clock.GetTimestamp();
            var remainingTicks = dueTick - now;
            if (remainingTicks <= 0)
            {
                return;
            }

            var remainingUs = ToMicroseconds(remainingTicks, qpcFrequency);
            if (remainingUs > SleepOneThresholdUs)
            {
                Thread.Sleep(1);
            }
            else if (remainingUs > YieldThresholdUs)
            {
                Thread.Sleep(0);
            }
            else if (remainingUs > AggressiveSpinThresholdUs)
            {
                Thread.SpinWait(calibratedSpinIterations * 4);
            }
            else if (remainingUs > 75)
            {
                Thread.SpinWait(calibratedSpinIterations * 2);
            }
            else
            {
                Thread.SpinWait(calibratedSpinIterations);
            }
        }
    }

    private int CalibrateSpinCount()
    {
        const int targetMicroseconds = 5;
        const int calibrationRounds = 10;
        const int testIterations = 1000;

        long totalTicks = 0;
        for (int round = 0; round < calibrationRounds; round++)
        {
            var start = clock.GetTimestamp();
            Thread.SpinWait(testIterations);
            var end = clock.GetTimestamp();
            totalTicks += (end - start);
        }

        var avgTicksPerIteration = (double)totalTicks / (calibrationRounds * testIterations);
        var targetTicks = targetMicroseconds * clock.Frequency / 1_000_000.0;
        return (int)Math.Clamp(targetTicks / avgTicksPerIteration, 16, 2048);
    }

    private static long ToMicroseconds(long ticks, long qpcFrequency)
    {
        return (long)Math.Round(ticks * 1_000_000.0 / qpcFrequency, MidpointRounding.AwayFromZero);
    }
}

public interface IMacroInputSink
{
    bool IsAvailable { get; }

    void Submit(uint sequence, InputAction action);

    InputSubmissionStats? GetStats();
}

public sealed class MacroPlaybackExecutor : IMacroPlaybackExecutor
{
    private readonly IMacroInputSink inputSink;
    private readonly IPlaybackDelayStrategy delayStrategy;
    private readonly Func<PixelCondition, bool> livePixelEvaluator;
    private readonly Func<string, MacroDocument?>? macroResolver;
    private readonly IHighResolutionClock clock;

    public MacroPlaybackExecutor(
        IMacroInputSink inputSink,
        IPlaybackDelayStrategy? delayStrategy = null,
        Func<PixelCondition, bool>? livePixelEvaluator = null,
        Func<string, MacroDocument?>? macroResolver = null,
        IHighResolutionClock? clock = null)
    {
        this.inputSink = inputSink;
        this.clock = clock ?? new QpcHighResolutionClock();
        this.delayStrategy = delayStrategy ?? new QpcPlaybackDelayStrategy(this.clock);
        this.livePixelEvaluator = livePixelEvaluator ?? ScreenPixelSampler.Matches;
        this.macroResolver = macroResolver;
    }

    public Task<PlaybackRunResult> RunAsync(
        MacroDocument document,
        PlaybackExecutionOptions options,
        CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<PlaybackRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try
            {
                var result = RunWithPrecisionContext(document, options, cancellationToken);
                tcs.TrySetResult(result);
            }
            catch (OperationCanceledException)
            {
                tcs.TrySetCanceled(cancellationToken);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.Highest,
            Name = "MacroHID-Playback"
        };

        thread.Start();
        return tcs.Task;
    }

    private PlaybackRunResult RunWithPrecisionContext(
        MacroDocument document,
        PlaybackExecutionOptions options,
        CancellationToken cancellationToken)
    {
        using var precisionContext = PrecisionPlaybackContext.Enter(options.Precision);
        return Run(document, options, cancellationToken);
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

        if (!inputSink.IsAvailable)
        {
            return new PlaybackRunResult(PlaybackRunStatus.InputUnavailable, 0, 0, Cancelled: false, InputStats: null);
        }

        var qpcFrequency = clock.Frequency;
        var iterationsTarget = options.Mode == PlaybackMode.FixedCount ? options.Count : int.MaxValue;
        var iterationsCompleted = 0;
        var actionsSubmitted = 0;
        var sequence = 1u;
        var conditionEvaluator = new CompositeConditionEvaluator(livePixelEvaluator);
        var timingRecorder = new PlaybackTimingRecorder();
        var plan = CompiledPlaybackPlan.Create(
            document,
            qpcFrequency,
            GetPixelEvaluator(options.PixelMode),
            macroResolver);

        try
        {
            while (iterationsCompleted < iterationsTarget)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var iterationPlan = plan.RequiresResampling || options.PixelMode == PixelEvaluationMode.Live
                    ? plan.Resample()
                    : plan;
                var iterationStartTick = clock.GetTimestamp();

                var monitors = CreateConditionMonitors(document, conditionEvaluator, iterationStartTick, qpcFrequency);
                try
                {
                    var currentStepIndex = 0;

                    for (var batchIndex = 0; batchIndex < iterationPlan.Batches.Count; batchIndex++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        currentStepIndex = EstimateStepIndex(batchIndex, iterationPlan.Batches.Count, document.Steps);
                        ActivateMonitorsForStep(monitors, currentStepIndex);

                        var batch = iterationPlan.Batches[batchIndex];
                        var dueTick = iterationStartTick + batch.DueTick;
                        delayStrategy.WaitUntil(dueTick, qpcFrequency, cancellationToken, options.NoWait);
                        cancellationToken.ThrowIfCancellationRequested();
                        RecordJitter(timingRecorder, dueTick, qpcFrequency);

                        if (inputSink is SendInputMacroSink batchSink)
                        {
                            batchSink.SubmitPrepared(sequence, batch.PreparedBatch);
                            sequence += (uint)batch.PreparedBatch.ActionCount;
                            actionsSubmitted += batch.PreparedBatch.ActionCount;
                        }
                        else
                        {
                            foreach (var action in batch.PreparedBatch.Actions)
                            {
                                inputSink.Submit(sequence++, action);
                                actionsSubmitted++;
                            }
                        }
                    }

                    DeactivateAllMonitors(monitors);
                }
                finally
                {
                    DisposeMonitors(monitors);
                }

                iterationsCompleted++;
            }

            ApplyTimingStats(timingRecorder);
            return new PlaybackRunResult(
                PlaybackRunStatus.Completed,
                iterationsCompleted,
                actionsSubmitted,
                Cancelled: false,
                inputSink.GetStats());
        }
        catch (OperationCanceledException)
        {
            SubmitSafeReleaseActions(ref sequence, ref actionsSubmitted);
            ApplyTimingStats(timingRecorder);
            return new PlaybackRunResult(
                PlaybackRunStatus.Completed,
                iterationsCompleted,
                actionsSubmitted,
                Cancelled: true,
                inputSink.GetStats());
        }
    }

    private List<ConditionMonitor> CreateConditionMonitors(
        MacroDocument document,
        CompositeConditionEvaluator evaluator,
        long macroStartTick,
        long qpcFrequency)
    {
        var monitors = new List<ConditionMonitor>();
        foreach (var cond in document.EffectiveConditions)
        {
            monitors.Add(new ConditionMonitor(cond, evaluator, inputSink, macroResolver, macroStartTick, qpcFrequency));
        }
        return monitors;
    }

    private static void ActivateMonitorsForStep(List<ConditionMonitor> monitors, int stepIndex)
    {
        foreach (var monitor in monitors)
        {
            if (stepIndex >= monitor.StartStepIndex && stepIndex <= monitor.EndStepIndex && !monitor.HasTriggered)
            {
                monitor.Activate();
            }
            else if (stepIndex > monitor.EndStepIndex)
            {
                monitor.Deactivate();
            }
        }
    }

    private static void DeactivateAllMonitors(List<ConditionMonitor> monitors)
    {
        foreach (var monitor in monitors)
            monitor.Deactivate();
    }

    private static void DisposeMonitors(List<ConditionMonitor> monitors)
    {
        foreach (var monitor in monitors)
            monitor.Dispose();
    }

    private void ApplyTimingStats(PlaybackTimingRecorder timingRecorder)
    {
        if (inputSink is SendInputMacroSink sendInput)
        {
            PlaybackTimingStats timingStats = timingRecorder.ToStats();
            sendInput.SetTimingStats(timingStats);
        }
    }

    private void RecordJitter(PlaybackTimingRecorder timingRecorder, long dueTick, long qpcFrequency)
    {
        timingRecorder.RecordJitter(clock.GetTimestamp(), dueTick, qpcFrequency);
    }

    private static int EstimateStepIndex(int batchIndex, int batchCount, IReadOnlyList<MacroStep> steps)
    {
        if (steps.Count == 0 || batchCount <= 0)
        {
            return 0;
        }

        return Math.Clamp(batchIndex * steps.Count / batchCount, 0, steps.Count - 1);
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

    private void SubmitSafeReleaseActions(ref uint sequence, ref int actionsSubmitted)
    {
        foreach (var action in SafeReleaseActions())
        {
            inputSink.Submit(sequence++, action);
            actionsSubmitted++;
        }
    }

    private static IEnumerable<InputAction> SafeReleaseActions()
    {
        yield return new KeyInputAction(
            KeyActionKind.Up,
            HidKey.None,
            HidModifier.LeftCtrl
                | HidModifier.LeftShift
                | HidModifier.LeftAlt
                | HidModifier.LeftGui
                | HidModifier.RightCtrl
                | HidModifier.RightShift
                | HidModifier.RightAlt
                | HidModifier.RightGui);
        yield return new MouseButtonInputAction(
            MouseButton.Left | MouseButton.Right | MouseButton.Middle | MouseButton.X1 | MouseButton.X2,
            ButtonActionKind.Up);

        foreach (var control in Enum.GetValues<ConsumerControl>())
        {
            yield return new ConsumerInputAction(control, ButtonActionKind.Up);
        }
    }

    private sealed class TimerResolutionScope : IDisposable
    {
        private readonly bool ntdllActive;
        private readonly bool winmmActive;
        private readonly uint ntdllResolution;

        private TimerResolutionScope(bool ntdllActive, uint ntdllResolution, bool winmmActive)
        {
            this.ntdllActive = ntdllActive;
            this.ntdllResolution = ntdllResolution;
            this.winmmActive = winmmActive;
        }

        public static TimerResolutionScope TryBeginHighResolution()
        {
            if (!OperatingSystem.IsWindows())
            {
                return new TimerResolutionScope(false, 0, false);
            }

            var status = RuntimeNativeMethods.NtSetTimerResolution(5000, true, out var actualResolution);
            if (status == 0)
            {
                return new TimerResolutionScope(ntdllActive: true, actualResolution, winmmActive: false);
            }

            var winmmResult = RuntimeNativeMethods.timeBeginPeriod(1) == 0;
            return new TimerResolutionScope(ntdllActive: false, 0, winmmActive: winmmResult);
        }

        public void Dispose()
        {
            if (ntdllActive)
            {
                RuntimeNativeMethods.NtSetTimerResolution(ntdllResolution, false, out _);
            }
            else if (winmmActive)
            {
                RuntimeNativeMethods.timeEndPeriod(1);
            }
        }
    }
}
