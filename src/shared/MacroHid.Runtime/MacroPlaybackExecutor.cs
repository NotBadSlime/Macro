using System.Runtime;
using MacroHid.Core;

namespace MacroHid.Runtime;

public interface IPlaybackDelayStrategy
{
    void WaitUntil(long dueTick, long qpcFrequency, CancellationToken cancellationToken, bool noWait);
}

public sealed record PlaybackDelayProfile(
    PrecisionMode Precision,
    long SleepOneThresholdUs,
    long YieldThresholdUs,
    long AggressiveSpinThresholdUs,
    long FinalSpinWindowUs,
    bool UseHighResolutionWaitableTimer,
    bool NoSleepForSubTwoMillisecond)
{
    public static PlaybackDelayProfile ForPrecisionMode(PrecisionMode mode)
    {
        return mode switch
        {
            PrecisionMode.Balanced => new PlaybackDelayProfile(
                mode,
                SleepOneThresholdUs: 4_000,
                YieldThresholdUs: 750,
                AggressiveSpinThresholdUs: 150,
                FinalSpinWindowUs: 250,
                UseHighResolutionWaitableTimer: true,
                NoSleepForSubTwoMillisecond: false),
            PrecisionMode.UltraLowJitter => new PlaybackDelayProfile(
                mode,
                SleepOneThresholdUs: 20_000,
                YieldThresholdUs: 2_500,
                AggressiveSpinThresholdUs: 2_000,
                FinalSpinWindowUs: 2_000,
                UseHighResolutionWaitableTimer: true,
                NoSleepForSubTwoMillisecond: true),
            _ => new PlaybackDelayProfile(
                PrecisionMode.ExtremeDuringPlayback,
                SleepOneThresholdUs: 8_000,
                YieldThresholdUs: 1_500,
                AggressiveSpinThresholdUs: 350,
                FinalSpinWindowUs: 800,
                UseHighResolutionWaitableTimer: true,
                NoSleepForSubTwoMillisecond: false)
        };
    }
}

public sealed class QpcPlaybackDelayStrategy : IPlaybackDelayStrategy
{
    private readonly IHighResolutionClock clock;
    private readonly PlaybackDelayProfile profile;
    private readonly int calibratedSpinIterations;

    public QpcPlaybackDelayStrategy(IHighResolutionClock? clock = null, PrecisionMode precision = PrecisionMode.ExtremeDuringPlayback)
        : this(clock, PlaybackDelayProfile.ForPrecisionMode(precision))
    {
    }

    public QpcPlaybackDelayStrategy(IHighResolutionClock? clock, PlaybackDelayProfile profile)
    {
        this.clock = clock ?? new QpcHighResolutionClock();
        this.profile = profile;
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
            if (profile.UseHighResolutionWaitableTimer
                && remainingUs > profile.FinalSpinWindowUs + 750
                && TryWaitWithHighResolutionWaitableTimer(remainingUs - profile.FinalSpinWindowUs))
            {
                continue;
            }

            if (profile.NoSleepForSubTwoMillisecond && remainingUs <= 2_000)
            {
                SpinForRemainingWindow(remainingUs);
            }
            else if (remainingUs > profile.SleepOneThresholdUs)
            {
                Thread.Sleep(1);
            }
            else if (remainingUs > profile.YieldThresholdUs)
            {
                Thread.Sleep(0);
            }
            else if (remainingUs > profile.AggressiveSpinThresholdUs)
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

    private void SpinForRemainingWindow(long remainingUs)
    {
        if (remainingUs > profile.AggressiveSpinThresholdUs)
        {
            Thread.SpinWait(calibratedSpinIterations * 8);
        }
        else if (remainingUs > 125)
        {
            Thread.SpinWait(calibratedSpinIterations * 4);
        }
        else
        {
            Thread.SpinWait(calibratedSpinIterations * 2);
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

    private static bool TryWaitWithHighResolutionWaitableTimer(long waitUs)
    {
        if (!OperatingSystem.IsWindows() || waitUs <= 0)
        {
            return false;
        }

        var timer = RuntimeNativeMethods.CreateWaitableTimerExW(
            IntPtr.Zero,
            null,
            RuntimeNativeMethods.CreateWaitableTimerHighResolution,
            RuntimeNativeMethods.TimerAllAccess);
        if (timer == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var dueTime100ns = -Math.Max(1, waitUs * 10);
            if (!RuntimeNativeMethods.SetWaitableTimerEx(
                    timer,
                    ref dueTime100ns,
                    lPeriod: 0,
                    pfnCompletionRoutine: IntPtr.Zero,
                    lpArgToCompletionRoutine: IntPtr.Zero,
                    wakeContext: IntPtr.Zero,
                    tolerableDelay: 0))
            {
                return false;
            }

            var timeoutMs = (uint)Math.Clamp(waitUs / 1_000 + 8, 1, 60_000);
            return RuntimeNativeMethods.WaitForSingleObject(timer, timeoutMs) == RuntimeNativeMethods.WaitObject0;
        }
        finally
        {
            RuntimeNativeMethods.CloseHandle(timer);
        }
    }
}

public interface IMacroInputSink
{
    bool IsAvailable { get; }

    void Submit(uint sequence, InputAction action);

    InputSubmissionStats? GetStats();
}

public sealed class MacroPlaybackExecutor : IMacroPlaybackExecutor, IDisposable
{
    private readonly IMacroInputSink inputSink;
    private readonly IPlaybackDelayStrategy? configuredDelayStrategy;
    private readonly Func<PixelCondition, bool> livePixelEvaluator;
    private readonly Func<string, MacroDocument?>? macroResolver;
    private readonly IHighResolutionClock clock;
    private readonly object preparedPlanGate = new();
    private MacroDocument? cachedDocument;
    private PlaybackExecutionOptions? cachedOptions;
    private CompiledPlaybackPlan? cachedCompiledPlan;
    private NativePlaybackPreparedPlan? cachedNativePlan;

    public MacroPlaybackExecutor(
        IMacroInputSink inputSink,
        IPlaybackDelayStrategy? delayStrategy = null,
        Func<PixelCondition, bool>? livePixelEvaluator = null,
        Func<string, MacroDocument?>? macroResolver = null,
        IHighResolutionClock? clock = null)
    {
        this.inputSink = inputSink;
        this.clock = clock ?? new QpcHighResolutionClock();
        this.configuredDelayStrategy = delayStrategy;
        this.livePixelEvaluator = livePixelEvaluator ?? ScreenPixelSampler.Matches;
        this.macroResolver = macroResolver;
    }

    public void Prepare(MacroDocument document, PlaybackExecutionOptions options)
    {
        if (!CanUseNativePreparedPlan(document, options))
        {
            ClearPreparedPlan();
            return;
        }

        var compiledPlan = CompiledPlaybackPlan.Create(
            document,
            clock.Frequency,
            GetPixelEvaluator(options.PixelMode),
            macroResolver);
        var nativePlan = TryCreateNativePreparedPlan(document, options, compiledPlan);
        lock (preparedPlanGate)
        {
            cachedNativePlan?.Dispose();
            cachedDocument = document;
            cachedOptions = options;
            cachedCompiledPlan = compiledPlan;
            cachedNativePlan = nativePlan;
        }
    }

    public void Dispose()
    {
        ClearPreparedPlan();
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
        using var affinityScope = ProcessAffinityScope.TryEnter(options.AffinityMask);
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
        var delayStrategy = configuredDelayStrategy ?? new QpcPlaybackDelayStrategy(clock, options.Precision);
        var iterationsTarget = options.Mode == PlaybackMode.FixedCount ? options.Count : int.MaxValue;
        var iterationsCompleted = 0;
        var actionsSubmitted = 0;
        var sequence = 1u;
        using var conditionEvaluator = new CompositeConditionEvaluator(livePixelEvaluator);
        var timingRecorder = new PlaybackTimingRecorder();
        var hasCachedPlan = TryGetPreparedPlan(document, options, out var cachedPlan, out var cachedPreparedPlan);
        var plan = cachedPlan ?? CompiledPlaybackPlan.Create(
                document,
                qpcFrequency,
                GetPixelEvaluator(options.PixelMode),
                macroResolver);
        var localPreparedPlan = hasCachedPlan
            ? null
            : TryCreateNativePreparedPlan(document, options, plan);
        var nativePreparedPlan = cachedPreparedPlan ?? localPreparedPlan;

        var plannedIterationStartTick = clock.GetTimestamp();

        try
        {
            while (iterationsCompleted < iterationsTarget)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var iterationPlan = plan.RequiresResampling || options.PixelMode == PixelEvaluationMode.Live
                    ? plan.Resample()
                    : plan;
                var iterationStartTick = plannedIterationStartTick;
                var iterationDurationTicks = Math.Max(1, iterationPlan.DurationTicks);

                if (!TryRunNativeIterationWithConditionMonitors(
                        document,
                        options,
                        iterationPlan,
                        nativePreparedPlan,
                        conditionEvaluator,
                        delayStrategy,
                        iterationStartTick,
                        iterationDurationTicks,
                        qpcFrequency,
                        cancellationToken,
                        ref sequence,
                        ref actionsSubmitted))
                {
                    var monitors = CreateConditionMonitors(document, conditionEvaluator, iterationStartTick, qpcFrequency);
                    try
                    {
                        ActivateAllMonitors(monitors);

                        for (var batchIndex = 0; batchIndex < iterationPlan.Batches.Count; batchIndex++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

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

                        WaitForIterationEndBeforeDeactivatingConditions(
                            monitors,
                            delayStrategy,
                            iterationStartTick + iterationDurationTicks,
                            qpcFrequency,
                            cancellationToken,
                            options.NoWait);
                        CompleteAllMonitorsAfterCurrentEvaluation(monitors);
                        WaitForTriggeredConditionActions(monitors, cancellationToken);
                        DeactivateAllMonitors(monitors);
                    }
                    finally
                    {
                        DisposeMonitors(monitors);
                    }
                }

                iterationsCompleted++;
                plannedIterationStartTick += iterationDurationTicks;
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
        finally
        {
            localPreparedPlan?.Dispose();
        }
    }

    private bool TryRunNativeIterationWithConditionMonitors(
        MacroDocument document,
        PlaybackExecutionOptions options,
        CompiledPlaybackPlan iterationPlan,
        NativePlaybackPreparedPlan? nativePreparedPlan,
        CompositeConditionEvaluator conditionEvaluator,
        IPlaybackDelayStrategy delayStrategy,
        long iterationStartTick,
        long iterationDurationTicks,
        long qpcFrequency,
        CancellationToken cancellationToken,
        ref uint sequence,
        ref int actionsSubmitted)
    {
        if (!CanAttemptNativeIteration(options))
        {
            return false;
        }

        if (TryReportPixelWhenNativeFallback(document, options))
        {
            return false;
        }

        var monitors = CreateConditionMonitors(document, conditionEvaluator, iterationStartTick, qpcFrequency);
        try
        {
            ActivateAllMonitors(monitors);
            if (!TryRunNativeIteration(document, options, iterationPlan, nativePreparedPlan, cancellationToken, ref sequence, ref actionsSubmitted))
            {
                return false;
            }

            WaitForIterationEndBeforeDeactivatingConditions(
                monitors,
                delayStrategy,
                iterationStartTick + iterationDurationTicks,
                qpcFrequency,
                cancellationToken,
                options.NoWait);
            CompleteAllMonitorsAfterCurrentEvaluation(monitors);
            WaitForTriggeredConditionActions(monitors, cancellationToken);
            DeactivateAllMonitors(monitors);
            return true;
        }
        finally
        {
            DisposeMonitors(monitors);
        }
    }

    private bool TryRunNativeIteration(
        MacroDocument document,
        PlaybackExecutionOptions options,
        CompiledPlaybackPlan iterationPlan,
        NativePlaybackPreparedPlan? nativePreparedPlan,
        CancellationToken cancellationToken,
        ref uint sequence,
        ref int actionsSubmitted)
    {
        if (!CanAttemptNativeIteration(options))
        {
            return false;
        }

        if (TryReportPixelWhenNativeFallback(document, options))
        {
            return false;
        }

        var sendInput = (SendInputMacroSink)inputSink;
        NativePlaybackRunDiagnostics diagnostics;
        string fallbackReason;
        bool ranNative;
        if (nativePreparedPlan is not null)
        {
            ranNative = NativePlaybackEngine.TryRunPrepared(
                nativePreparedPlan,
                options.Precision,
                cancellationToken,
                out diagnostics,
                out fallbackReason,
                enableCpuScan: NativePlaybackWarmup.CpuScanReady);
        }
        else
        {
            ranNative = NativePlaybackEngine.TryRun(
                iterationPlan,
                options.Precision,
                cancellationToken,
                out diagnostics,
                out fallbackReason,
                enableCpuScan: NativePlaybackWarmup.CpuScanReady);
        }

        if (!ranNative)
        {
            sendInput.SetNativePlaybackDiagnostics(diagnostics with { NativeFallbackReason = fallbackReason });
            return false;
        }

        sendInput.SetNativePlaybackDiagnostics(diagnostics);
        if (diagnostics.Cancelled || cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        sequence += (uint)Math.Max(0, diagnostics.ActionsSubmitted);
        actionsSubmitted += Math.Max(0, diagnostics.ActionsSubmitted);
        return true;
    }

    private bool CanAttemptNativeIteration(PlaybackExecutionOptions options)
    {
        return CanUseNativePrecision(options.Precision)
            && !options.NoWait
            && configuredDelayStrategy is null
            && inputSink is SendInputMacroSink;
    }

    private bool TryReportPixelWhenNativeFallback(MacroDocument document, PlaybackExecutionOptions options)
    {
        if (options.PixelMode == PixelEvaluationMode.Live
            && ContainsPixelWhen(document.Steps)
            && inputSink is SendInputMacroSink sendInput)
        {
            sendInput.SetNativePlaybackDiagnostics(
                NativePlaybackEngine.CreateFallbackDiagnostics("inline PixelWhen requires managed step activation."));
            return true;
        }

        return false;
    }

    private NativePlaybackPreparedPlan? TryCreateNativePreparedPlan(
        MacroDocument document,
        PlaybackExecutionOptions options,
        CompiledPlaybackPlan plan)
    {
        if (!CanUseNativePreparedPlan(document, options)
            || plan.RequiresResampling)
        {
            return null;
        }

        return NativePlaybackEngine.TryCreatePreparedPlan(
            plan.ExportNativeTimeline(),
            plan.QpcFrequency,
            out var preparedPlan,
            out _,
            out _)
            ? preparedPlan
            : null;
    }

    private bool TryGetPreparedPlan(
        MacroDocument document,
        PlaybackExecutionOptions options,
        out CompiledPlaybackPlan? compiledPlan,
        out NativePlaybackPreparedPlan? nativePlan)
    {
        lock (preparedPlanGate)
        {
            if (ReferenceEquals(document, cachedDocument)
                && cachedOptions == options
                && cachedCompiledPlan is not null)
            {
                compiledPlan = cachedCompiledPlan;
                nativePlan = cachedNativePlan;
                return true;
            }
        }

        compiledPlan = null;
        nativePlan = null;
        return false;
    }

    private void ClearPreparedPlan()
    {
        lock (preparedPlanGate)
        {
            cachedNativePlan?.Dispose();
            cachedNativePlan = null;
            cachedCompiledPlan = null;
            cachedDocument = null;
            cachedOptions = null;
        }
    }

    private bool CanUseNativePreparedPlan(MacroDocument document, PlaybackExecutionOptions options)
    {
        return CanUseNativePrecision(options.Precision)
            && !options.NoWait
            && configuredDelayStrategy is null
            && inputSink is SendInputMacroSink
            && (options.PixelMode != PixelEvaluationMode.Live || !ContainsPixelWhen(document.Steps));
    }

    private static bool CanUseNativePrecision(PrecisionMode precision)
    {
        return precision is PrecisionMode.ExtremeDuringPlayback or PrecisionMode.UltraLowJitter;
    }

    private static bool ContainsPixelWhen(IReadOnlyList<MacroStep> steps)
    {
        foreach (var step in steps)
        {
            switch (step)
            {
                case PixelWhenStep:
                    return true;
                case RepeatStep repeat when ContainsPixelWhen(repeat.Steps):
                    return true;
            }
        }

        return false;
    }

    private List<ConditionMonitor> CreateConditionMonitors(
        MacroDocument document,
        CompositeConditionEvaluator evaluator,
        long macroStartTick,
        long qpcFrequency)
    {
        var monitors = new List<ConditionMonitor>();
        var conditionWindows = CreateConditionTimeWindows(document, qpcFrequency);
        foreach (var cond in document.EffectiveConditions)
        {
            monitors.Add(new ConditionMonitor(
                ApplyConditionTimeWindow(cond, conditionWindows, qpcFrequency),
                evaluator,
                inputSink,
                macroResolver,
                macroStartTick,
                qpcFrequency));
        }
        return monitors;
    }

    private static void ActivateAllMonitors(List<ConditionMonitor> monitors)
    {
        foreach (var monitor in monitors)
        {
            monitor.Activate();
        }
    }

    private static void DeactivateAllMonitors(List<ConditionMonitor> monitors)
    {
        foreach (var monitor in monitors)
            monitor.Deactivate();
    }

    private static void CompleteAllMonitorsAfterCurrentEvaluation(List<ConditionMonitor> monitors)
    {
        foreach (var monitor in monitors)
            monitor.CompleteAfterCurrentEvaluation();
    }

    private static void WaitForIterationEndBeforeDeactivatingConditions(
        List<ConditionMonitor> monitors,
        IPlaybackDelayStrategy delayStrategy,
        long iterationEndTick,
        long qpcFrequency,
        CancellationToken cancellationToken,
        bool noWait)
    {
        if (monitors.Count > 0)
        {
            delayStrategy.WaitUntil(iterationEndTick, qpcFrequency, cancellationToken, noWait);
        }
    }

    private static void WaitForTriggeredConditionActions(List<ConditionMonitor> monitors, CancellationToken cancellationToken)
    {
        foreach (var monitor in monitors)
        {
            monitor.WaitForTriggeredCompletion(cancellationToken);
        }
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

    private IReadOnlyList<StepTimeWindow> CreateConditionTimeWindows(MacroDocument document, long qpcFrequency)
    {
        var windows = new List<StepTimeWindow>();
        var elapsedTicks = 0L;
        AddStepTimeWindows(document.Steps, [], windows, ref elapsedTicks, qpcFrequency, depth: 0);
        return windows;
    }

    private void AddStepTimeWindows(
        IReadOnlyList<MacroStep> steps,
        IReadOnlyList<int> parentPath,
        List<StepTimeWindow> windows,
        ref long elapsedTicks,
        long qpcFrequency,
        int depth)
    {
        if (depth > 16)
        {
            return;
        }

        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var path = parentPath.Concat([i]).ToArray();
            var startTicks = elapsedTicks;
            var durationTicks = EstimateStepDurationTicks(step, qpcFrequency, depth);

            windows.Add(new StepTimeWindow(windows.Count, path, startTicks, startTicks + durationTicks));

            if (step is RepeatStep repeat)
            {
                var oneIterationTicks = EstimateStepsDurationTicks(repeat.Steps, qpcFrequency, depth + 1);
                var firstIterationElapsed = elapsedTicks;
                AddStepTimeWindows(repeat.Steps, path, windows, ref firstIterationElapsed, qpcFrequency, depth + 1);
                elapsedTicks += oneIterationTicks * Math.Max(1, repeat.Count);
            }
            else
            {
                elapsedTicks += durationTicks;
            }
        }
    }

    private long EstimateStepsDurationTicks(IReadOnlyList<MacroStep> steps, long qpcFrequency, int depth)
    {
        var total = 0L;
        foreach (var step in steps)
        {
            total += EstimateStepDurationTicks(step, qpcFrequency, depth);
        }

        return total;
    }

    private long EstimateStepDurationTicks(MacroStep step, long qpcFrequency, int depth)
    {
        static long ToTicks(TimeSpan duration, long frequency) =>
            (long)Math.Round(duration.TotalSeconds * frequency, MidpointRounding.AwayFromZero);

        return step switch
        {
            KeyStep key => ToTicks(key.Hold, qpcFrequency),
            MouseMoveStep move => ToTicks(move.Duration, qpcFrequency),
            MouseButtonStep button => ToTicks(button.Hold, qpcFrequency),
            ConsumerStep consumer => ToTicks(consumer.Hold, qpcFrequency),
            WaitStep wait => ToTicks(wait.MaxDuration ?? wait.Duration, qpcFrequency),
            RepeatStep repeat => EstimateStepsDurationTicks(repeat.Steps, qpcFrequency, depth + 1) * Math.Max(1, repeat.Count),
            MacroCallStep macro when macroResolver is not null && macroResolver(macro.Macro) is { } document =>
                EstimateStepsDurationTicks(document.Steps, qpcFrequency, depth + 1),
            PixelWhenStep pixel => EstimateStepsDurationTicks(pixel.ThenSteps, qpcFrequency, depth + 1),
            _ => 0L
        };
    }

    private static ConditionalDirective ApplyConditionTimeWindow(
        ConditionalDirective directive,
        IReadOnlyList<StepTimeWindow> stepTimeWindows,
        long qpcFrequency)
    {
        if (stepTimeWindows.Count == 0)
        {
            return directive;
        }

        var startWindow = FindStepTimeWindow(stepTimeWindows, directive.StartStepPath, directive.StartStepIndex);
        var endWindow = FindStepTimeWindow(stepTimeWindows, directive.EndStepPath, directive.EndStepIndex) ?? startWindow;
        if (startWindow is null || endWindow is null)
        {
            return directive;
        }

        var stepStart = ToTimeSpan(startWindow.StartTicks, qpcFrequency);
        var selectedEnd = Math.Max(endWindow.EndTicks, startWindow.StartTicks);
        var iterationEnd = Math.Max(selectedEnd, stepTimeWindows.Max(window => window.EndTicks));
        var iterationEndTime = ToTimeSpan(iterationEnd, qpcFrequency);

        var windowStart = MaxTimeSpan(directive.WindowStart, stepStart);
        var windowEnd = directive.WindowEnd is { } explicitEnd
            ? MinTimeSpan(explicitEnd, iterationEndTime) ?? explicitEnd
            : iterationEndTime;
        if (windowEnd <= windowStart)
        {
            windowEnd = windowStart + directive.EffectivePollInterval;
        }

        return directive with
        {
            WindowStart = windowStart,
            WindowEnd = windowEnd
        };
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
        return TimeSpan.FromSeconds(ticks / (double)qpcFrequency);
    }

    private static TimeSpan MaxTimeSpan(TimeSpan? first, TimeSpan second)
    {
        return first is { } value && value > second ? value : second;
    }

    private static TimeSpan? MinTimeSpan(TimeSpan? first, TimeSpan second)
    {
        return first is { } value && value < second ? value : second;
    }

    private sealed record StepTimeWindow(int Index, IReadOnlyList<int> Path, long StartTicks, long EndTicks);

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
