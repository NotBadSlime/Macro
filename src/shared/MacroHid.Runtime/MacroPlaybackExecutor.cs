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
                FinalSpinWindowUs: PlaybackPerformancePolicy.For(mode).FinalSpinMicroseconds,
                UseHighResolutionWaitableTimer: true,
                NoSleepForSubTwoMillisecond: false),
            PrecisionMode.UltraLowJitter => new PlaybackDelayProfile(
                mode,
                SleepOneThresholdUs: 20_000,
                YieldThresholdUs: 2_500,
                AggressiveSpinThresholdUs: 2_000,
                FinalSpinWindowUs: PlaybackPerformancePolicy.For(mode).FinalSpinMicroseconds,
                UseHighResolutionWaitableTimer: true,
                NoSleepForSubTwoMillisecond: false),
            _ => new PlaybackDelayProfile(
                PrecisionMode.ExtremeDuringPlayback,
                SleepOneThresholdUs: 8_000,
                YieldThresholdUs: 1_500,
                AggressiveSpinThresholdUs: 350,
                FinalSpinWindowUs: PlaybackPerformancePolicy.For(mode).FinalSpinMicroseconds,
                UseHighResolutionWaitableTimer: true,
                NoSleepForSubTwoMillisecond: false)
        };
    }
}

public sealed class QpcPlaybackDelayStrategy : IPlaybackDelayStrategy
{
    /// <summary>
    /// Cap each waitable-timer sleep so <see cref="WaitUntil"/> can re-check
    /// cancellation promptly (Stop during long delays / gate then-actions).
    /// </summary>
    private const long CancellationPollChunkUs = 15_000;

    private readonly IHighResolutionClock clock;
    private readonly PlaybackDelayProfile profile;
    private readonly int calibratedSpinIterations;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(PrecisionMode, int), int> Calibration = new();

    private static IReadOnlyDictionary<int, int> CoreClasses = new Dictionary<int, int>();
    private static int CurrentClass() => Volatile.Read(ref CoreClasses).GetValueOrDefault(Thread.GetCurrentProcessorId(), -1);
    public static void Prepare(PrecisionMode precision)
    {
        if (CoreClasses.Count == 0)
            Volatile.Write(ref CoreClasses, LogicalProcessorInventory.Query().ToDictionary(p => p.ProcessorNumber, p => p.EfficiencyClass));
        var key = (precision, CurrentClass());
        if (Calibration.ContainsKey(key)) return;
        var clock = new QpcHighResolutionClock();
        var strategy = new QpcPlaybackDelayStrategy(clock, precision);
        Calibration.TryAdd(key, strategy.CalibrateSpinCount());
    }

    public QpcPlaybackDelayStrategy(IHighResolutionClock? clock = null, PrecisionMode precision = PrecisionMode.ExtremeDuringPlayback)
        : this(clock, PlaybackDelayProfile.ForPrecisionMode(precision))
    {
    }

    public QpcPlaybackDelayStrategy(IHighResolutionClock? clock, PlaybackDelayProfile profile)
    {
        this.clock = clock ?? new QpcHighResolutionClock();
        this.profile = profile;
        // Never calibrate on a trigger. Custom clocks retain their deterministic test behavior.
        calibratedSpinIterations = this.clock is QpcHighResolutionClock
            ? Calibration.GetValueOrDefault((profile.Precision, CurrentClass()), 16)
            : 16;
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
                && remainingUs > profile.FinalSpinWindowUs + 50
                && TryWaitWithHighResolutionWaitableTimer(
                    Math.Min(remainingUs - profile.FinalSpinWindowUs, CancellationPollChunkUs), cancellationToken))
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

    private static bool TryWaitWithHighResolutionWaitableTimer(long waitUs, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || waitUs <= 0)
        {
            return false;
        }

        var timer = threadTimer ??= new ThreadTimer();
        if (timer.Handle == IntPtr.Zero) return false;
        return timer.Wait(waitUs, cancellationToken);
    }

    /// <summary>Sleeps about <paramref name="waitUs"/> on this thread's high-resolution waitable timer. False if unavailable.</summary>
    internal static bool TryHighResolutionSleep(long waitUs) => TryWaitWithHighResolutionWaitableTimer(waitUs, CancellationToken.None);

    [ThreadStatic] private static ThreadTimer? threadTimer;
    internal static void ReleaseThreadTimer() { threadTimer?.Dispose(); threadTimer = null; }
    private sealed class ThreadTimer : IDisposable
    {
        private IntPtr handle = RuntimeNativeMethods.CreateWaitableTimerExW(
            IntPtr.Zero,
            null,
            RuntimeNativeMethods.CreateWaitableTimerHighResolution,
            RuntimeNativeMethods.TimerAllAccess);
        internal IntPtr Handle => handle;
        private readonly IntPtr[] handles = new IntPtr[2];
        internal bool Wait(long waitUs, CancellationToken token)
        {
            var due = -Math.Max(1, waitUs * 10);
            if (!RuntimeNativeMethods.SetWaitableTimerEx(Handle, ref due, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0)) return false;
            handles[0] = Handle;
            handles[1] = token.WaitHandle.SafeWaitHandle.DangerousGetHandle();
            var result = WaitForMultipleObjects(2, handles, false, (uint)Math.Clamp(waitUs / 1000 + 8, 1, 60000));
            token.ThrowIfCancellationRequested();
            return result == 0;
        }
        private void Close() { var owned = Interlocked.Exchange(ref handle, IntPtr.Zero); if (owned != IntPtr.Zero) RuntimeNativeMethods.CloseHandle(owned); }
        public void Dispose() { Close(); GC.SuppressFinalize(this); }
        ~ThreadTimer() { Close(); }
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForMultipleObjects(uint count, IntPtr[] handles, bool waitAll, uint milliseconds);
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
    private readonly Func<string, MacroDocument?>? configuredMacroResolver;
    private Func<string, MacroDocument?>? macroResolver => PlaybackMacroSnapshot.Resolver ?? configuredMacroResolver;
    private readonly IHighResolutionClock clock;
    private readonly object preparedPlanGate = new();
    private MacroDocument? cachedDocument;
    private PlaybackExecutionOptions? cachedOptions;
    private CompiledPlaybackPlan? cachedCompiledPlan;
    // Derived from the same document and resolver as the compiled plan, so it shares that cache's lifetime.
    private PlaybackInputSafetyPlan? cachedSafetyPlan;
    private NativePlaybackPreparedPlan? cachedNativePlan;
    private MacroDocument? warmedDocument;
    private bool nativePreparationAvailable;
    private readonly ConditionActionPlanCache.Store preparedConditions = new();
    private MacroDocument? conditionPreparedDocument;
    private PlaybackExecutionOptions? conditionPreparedOptions;

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
        configuredMacroResolver = macroResolver;
    }

    /// <summary>Hotkey-anchored first start, or 0 when there is no hotkey time, no lead, or a non-QPC test clock.</summary>
    private long StartAnchorTick(PlaybackExecutionOptions options, long nowTick)
    {
        var trigger = PlaybackTrigger.CallbackQpc.Value;
        if (options.StartLeadMicroseconds <= 0 || trigger <= 0 || clock is not QpcHighResolutionClock) return 0;
        var anchor = trigger + options.StartLeadMicroseconds * clock.Frequency / 1_000_000;
        PlaybackTrigger.RecordStart(anchor > nowTick);
        return anchor;
    }

    public void Prepare(MacroDocument document, PlaybackExecutionOptions options)
    {
        if (Environment.GetEnvironmentVariable("MACROHID_DISABLE_PREPARE") == "1") return;
        QpcPlaybackDelayStrategy.Prepare(options.Precision);
        nativePreparationAvailable = PlaybackPerformancePolicy.For(options.Precision).PreferNative
            && NativePlaybackWarmup.PrepareForPrecision(options.Precision, options.AffinityMask, options.PressFloor.Generation);
        if (!ReferenceEquals(warmedDocument, document))
        {
            if (warmedDocument is not null)
            {
                EffectPathWarmup.Disarm(warmedDocument);
            }

            EffectPathWarmup.Arm(document, macroResolver);
            warmedDocument = document;
        }

        preparedConditions.Dispose();
        ConditionActionPlanCache.Prewarm(document, preparedConditions, clock.Frequency, macroResolver, options.PressFloor,
            CanAttemptNativeIteration(options));
        conditionPreparedDocument = document; conditionPreparedOptions = options;
        if (RequiresManagedControlFlow(document, options))
        {
            ClearPreparedPlan();
            return;
        }

        var compiledPlan = CompiledPlaybackPlan.Create(
            document,
            clock.Frequency,
            GetPixelEvaluator(options.PixelMode),
            macroResolver,
            waitDurationSampler: wait => wait.Duration, // Prepare a skeleton; never consume random samples here.
            pressFloor: options.PressFloor);
        var nativePlan = TryCreateNativePreparedPlan(document, options, compiledPlan);
        var safetyPlan = PlaybackInputSafetyPlan.Create(document, macroResolver);
        lock (preparedPlanGate)
        {
            cachedNativePlan?.Dispose();
            cachedDocument = document;
            cachedOptions = options;
            cachedCompiledPlan = compiledPlan;
            cachedSafetyPlan = safetyPlan;
            cachedNativePlan = nativePlan;
        }
    }

    public void Dispose()
    {
        if (warmedDocument is not null)
        {
            EffectPathWarmup.Disarm(warmedDocument);
            warmedDocument = null;
        }

        ClearPreparedPlan();
        preparedConditions.Dispose();
    }

    public Task<PlaybackRunResult> RunAsync(
        MacroDocument document,
        PlaybackExecutionOptions options,
        CancellationToken cancellationToken)
    {
        return PlaybackWorkers.Playback.Run(() => RunWithPrecisionContext(document, options, cancellationToken));
    }

    private PlaybackRunResult RunWithPrecisionContext(
        MacroDocument document,
        PlaybackExecutionOptions options,
        CancellationToken cancellationToken)
    {
        PlaybackTrace.Mark(PlaybackTraceStage.WorkerStart);
        var thread = Thread.CurrentThread;
        var previousPriority = thread.Priority;
        try
        {
            thread.Priority = PlaybackPerformancePolicy.For(options.Precision).ExecutionThreadPriority;
            using var precisionContext = PrecisionPlaybackContext.Enter(options.Precision);
            PlaybackTrace.Mark(PlaybackTraceStage.PrecisionEntered);
            PlaybackRuntimeStatus.ReportBackend("managed");
            return Run(document, options, cancellationToken);
        }
        finally { thread.Priority = previousPriority; }
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

        if (inputSink is SendInputMacroSink sendInput)
        {
            sendInput.ModifierChordHold = options.PressFloor.ModifierChordHold;
        }

        using var backgroundScope = BackgroundWindowSession.BeginScope(options);

        using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var effectScope = new PlaybackEffectScope();
        using var macroSnapshot = new PlaybackMacroSnapshot(document, configuredMacroResolver);
        using var callScope = new ConditionalMacroCallScope(document);
        using var conditionPlans = new ConditionActionPlanCache(ReferenceEquals(conditionPreparedDocument, document) && conditionPreparedOptions == options ? preparedConditions : null);
        cancellationToken = runCancellation.Token;
        PlaybackTrace.Mark(PlaybackTraceStage.ScopesReady);
        var hasCachedPlan = TryGetPreparedPlan(document, options, out var cachedPlan, out var cachedPreparedPlan, out var cachedSafetyPlan);
        var safetyPlan = cachedSafetyPlan ?? PlaybackInputSafetyPlan.Create(document, macroResolver);
        var completedIterationReleaseActions = options.Mode == PlaybackMode.FixedCount
            ? Array.Empty<InputAction>()
            : safetyPlan.IterationBoundaryReleaseActions;
        using var safetyRegistration = PlaybackInputSafetyRegistry.Register(
            inputSink,
            safetyPlan.EmergencyReleaseActions);
        PlaybackTrace.Mark(PlaybackTraceStage.SafetyReady);

        if (RequiresManagedControlFlow(document, options))
        {
            return RunManagedControlFlow(
                    document,
                    options,
                    runCancellation,
                    cancellationToken,
                    completedIterationReleaseActions,
                    safetyPlan.IterationBoundaryReleaseActions,
                    safetyPlan.EmergencyReleaseActions);
        }

        var qpcFrequency = clock.Frequency;
        var delayStrategy = configuredDelayStrategy ?? new QpcPlaybackDelayStrategy(clock, options.Precision);
        var iterationsTarget = options.Mode == PlaybackMode.FixedCount ? options.Count : int.MaxValue;
        var iterationsCompleted = 0;
        var actionsSubmitted = 0;
        var sequence = 1u;
        using var conditionEvaluator = new CompositeConditionEvaluator(livePixelEvaluator);
        var timingRecorder = new PlaybackTimingRecorder();
        var plan = cachedPlan ?? CompiledPlaybackPlan.Create(
                document,
                qpcFrequency,
                GetPixelEvaluator(options.PixelMode),
                macroResolver,
                pressFloor: options.PressFloor);
        var localPreparedPlan = hasCachedPlan
            ? null
            : TryCreateNativePreparedPlan(document, options, plan);
        var nativePreparedPlan = cachedPreparedPlan ?? localPreparedPlan;

        PlaybackTrace.Mark(PlaybackTraceStage.PlanReady);
        var plannedIterationStartTick = clock.GetTimestamp();
        var playbackTriggerTick = plannedIterationStartTick;
        // Start of the next iteration if this one ran on time: previous start + the macro's written duration. If the previous
        // iteration overran (condition waits, pauses, a stall), start from now instead of firing the missed time in a burst.
        // The first iteration is anchored to the hotkey: hook callback time + the configured start lead, so the variable
        // hand-off work before this point does not move the macro. If that moment has already passed, it starts from now.
        var phaseLockedNextStartTick = StartAnchorTick(options, plannedIterationStartTick);
        var hasConditionWindows = document.EffectiveConditions.Any();
        // The selected precision generation. Earlier generations start every iteration from now, repeat at the compiled duration
        // (press-floor delay included) and drop a trailing wait when no condition monitor exists.
        var earlierTimeline = PrecisionGenerations.UsesEarlierTimeline(options.PressFloor.Generation);
        // Tick of the next iteration's first action if its start is phase-locked (start + the first batch's offset).
        var phaseLockedFirstActionOffset = 0L;

        try
        {
            while (iterationsCompleted < iterationsTarget)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Keep the written phase while the next iteration's first action is still in the future. If it is already past
                // (the previous iteration overran, or a macro with no wait is simply back to back), start from now: running the
                // missed time would fire a burst. A loop head wait ("wait N, press, release") lands in the future, so the
                // previous iteration's delayed release and this iteration's wait overlap, as the written timeline says.
                var nowTick = clock.GetTimestamp();
                // Macros with conditions keep starting from now: their condition windows are measured from the iteration start and
                // have not been re-validated for an earlier start.
                plannedIterationStartTick = !hasConditionWindows && !earlierTimeline
                    && phaseLockedNextStartTick + phaseLockedFirstActionOffset > nowTick
                    ? phaseLockedNextStartTick
                    : nowTick;
                var timeline = new ConditionTimeline(
                    document.EffectiveConditions.Count,
                    playbackTriggerTick,
                    plannedIterationStartTick);
                var gateWait = TryWaitForStartupGatesOrCancel(
                    document,
                    conditionEvaluator,
                    delayStrategy,
                    options.Precision,
                    ref plannedIterationStartTick,
                    qpcFrequency,
                    cancellationToken,
                    playbackTriggerTick,
                    timeline,
                    options.PressFloor);
                if (gateWait == StartupGateWaitResult.Failed)
                {
                    SubmitSafetyReleaseActions(
                        completedIterationReleaseActions,
                        ref sequence,
                        ref actionsSubmitted);
                    ApplyTimingStats(timingRecorder);
                    return new PlaybackRunResult(
                        PlaybackRunStatus.Completed,
                        iterationsCompleted,
                        actionsSubmitted,
                        Cancelled: true,
                        inputSink.GetStats());
                }

                if (gateWait == StartupGateWaitResult.SkipIteration)
                {
                    SubmitSafetyReleaseActions(
                        completedIterationReleaseActions,
                        ref sequence,
                        ref actionsSubmitted);
                    iterationsCompleted++;
                    continue;
                }

                var iterationPlan = plan.RequiresResampling || (options.PixelMode == PixelEvaluationMode.Live && ContainsPixelWhen(document.Steps))
                    ? plan.Resample()
                    : plan;
                var iterationStartTick = plannedIterationStartTick;
                var iterationDurationTicks = Math.Max(1, earlierTimeline ? iterationPlan.DurationTicks : iterationPlan.LoopPeriodTicks); // the written period, see CompiledPlaybackPlan.LoopPeriodTicks

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
                        runCancellation.Cancel,
                        cancellationToken,
                        ref sequence,
                        ref actionsSubmitted,
                        out var nativeStopIteration,
                        timeline,
                        anotherIterationFollows: !earlierTimeline && iterationsCompleted + 1 < iterationsTarget))
                {
                    using var pauseCoordinator = HasPauseMainTimelineCondition(document)
                        ? new PlaybackPauseCoordinator(clock)
                        : null;
                    using var iterationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    var iterationToken = iterationCancellation.Token;
                    var conditionStopIterationRequested = 0;
                    void RequestStopIteration()
                    {
                        Interlocked.Exchange(ref conditionStopIterationRequested, 1);
                        iterationCancellation.Cancel();
                    }

                    var monitors = CreateConditionMonitors(
                        document,
                        conditionEvaluator,
                        iterationStartTick,
                        qpcFrequency,
                        options.Precision,
                        runCancellation.Cancel,
                        RequestStopIteration,
                        pauseCoordinator: pauseCoordinator,
                        timeline: timeline,
                        pressFloor: options.PressFloor);
                    try
                    {
                        ActivateAllMonitors(monitors);

                        for (var batchIndex = 0; batchIndex < iterationPlan.Batches.Count; batchIndex++)
                        {
                            iterationToken.ThrowIfCancellationRequested();

                            var batch = iterationPlan.Batches[batchIndex];
                            var dueTick = iterationStartTick + batch.DueTick;
                            if (pauseCoordinator is null)
                            {
                                delayStrategy.WaitUntil(dueTick, qpcFrequency, iterationToken, options.NoWait);
                            }
                            else
                            {
                                pauseCoordinator.WaitUntil(dueTick, delayStrategy, qpcFrequency, iterationToken, options.NoWait);
                            }
                            iterationToken.ThrowIfCancellationRequested();
                            PlaybackTrace.Mark(PlaybackTraceStage.Deadline, dueTick);
                            PlaybackTrace.Mark(PlaybackTraceStage.SubmitStart);
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
                            iterationToken,
                            options.NoWait,
                            pauseCoordinator,
                            anotherIterationFollows: !earlierTimeline && iterationsCompleted + 1 < iterationsTarget);
                        CompleteAllMonitorsAfterCurrentEvaluation(monitors);
                        WaitForTriggeredConditionActions(monitors, iterationToken);
                        DeactivateAllMonitors(monitors);
                    }
                    catch (OperationCanceledException) when (
                        Volatile.Read(ref conditionStopIterationRequested) != 0
                        && !cancellationToken.IsCancellationRequested)
                    {
                        nativeStopIteration = true;
                    }
                    finally
                    {
                        DisposeMonitors(monitors);
                    }

                    if (Volatile.Read(ref conditionStopIterationRequested) != 0)
                    {
                        nativeStopIteration = true;
                    }
                }

                if (nativeStopIteration)
                {
                    SubmitSafetyReleaseActions(
                        safetyPlan.IterationBoundaryReleaseActions,
                        ref sequence,
                        ref actionsSubmitted);
                    iterationsCompleted++;
                    plannedIterationStartTick = clock.GetTimestamp();
                    continue;
                }

                SubmitSafetyReleaseActions(
                    completedIterationReleaseActions,
                    ref sequence,
                    ref actionsSubmitted);
                iterationsCompleted++;
                phaseLockedNextStartTick = iterationStartTick + iterationDurationTicks;
                phaseLockedFirstActionOffset = iterationPlan.Batches.Count == 0 ? 0 : Math.Max(0, iterationPlan.Batches[0].DueTick);
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
            SubmitSafetyReleaseActions(
                safetyPlan.EmergencyReleaseActions,
                ref sequence,
                ref actionsSubmitted);
            ApplyTimingStats(timingRecorder);
            return new PlaybackRunResult(
                PlaybackRunStatus.Completed,
                iterationsCompleted,
                actionsSubmitted,
                Cancelled: true,
                inputSink.GetStats());
        }
        catch
        {
            SubmitSafetyReleaseActions(safetyPlan.EmergencyReleaseActions);
            throw;
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
        Action stopAllRequested,
        CancellationToken cancellationToken,
        ref uint sequence,
        ref int actionsSubmitted,
        out bool stopIterationRequested,
        ConditionTimeline? timeline = null,
        bool anotherIterationFollows = false)
    {
        stopIterationRequested = false;
        if (options.BackgroundWindow)
        {
            if (inputSink is SendInputMacroSink backgroundSink)
            {
                backgroundSink.SetNativePlaybackDiagnostics(
                    NativePlaybackEngine.CreateFallbackDiagnostics("background window input requires managed playback."));
            }

            return false;
        }

        if (!CanAttemptNativeIteration(options))
        {
            return false;
        }

        if (TryReportPixelWhenNativeFallback(document, options))
        {
            return false;
        }

        NativePlaybackRunControl? nativeControl = null;
        if (HasPauseMainTimelineCondition(document)
            && !NativePlaybackRunControl.TryCreate(out nativeControl))
        {
            return false;
        }

        using (nativeControl)
        using (var pauseCoordinator = nativeControl is null ? null : new PlaybackPauseCoordinator(clock, nativeControl))
        using (var iterationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            var iterationToken = iterationCancellation.Token;
            var conditionStopIterationRequested = 0;
            void RequestStopIteration()
            {
                Interlocked.Exchange(ref conditionStopIterationRequested, 1);
                iterationCancellation.Cancel();
            }

            var monitors = CreateConditionMonitors(
                document,
                conditionEvaluator,
                iterationStartTick,
                qpcFrequency,
                options.Precision,
                stopAllRequested,
                RequestStopIteration,
                pauseCoordinator: pauseCoordinator,
                timeline: timeline,
                pressFloor: options.PressFloor);
            try
            {
                ActivateAllMonitors(monitors);
                if (!TryRunNativeIteration(
                        document,
                        options,
                        iterationPlan,
                        nativePreparedPlan,
                        iterationToken,
                        ref sequence,
                        ref actionsSubmitted,
                        nativeControl, iterationStartTick))
                {
                    return false;
                }

                WaitForIterationEndBeforeDeactivatingConditions(
                    monitors,
                    delayStrategy,
                    iterationStartTick + iterationDurationTicks,
                    qpcFrequency,
                    iterationToken,
                    options.NoWait,
                    pauseCoordinator,
                    anotherIterationFollows);
                CompleteAllMonitorsAfterCurrentEvaluation(monitors);
                WaitForTriggeredConditionActions(monitors, iterationToken);
                DeactivateAllMonitors(monitors);
                stopIterationRequested = Volatile.Read(ref conditionStopIterationRequested) != 0;
                return true;
            }
            catch (OperationCanceledException) when (
                Volatile.Read(ref conditionStopIterationRequested) != 0
                && !cancellationToken.IsCancellationRequested)
            {
                stopIterationRequested = true;
                return true;
            }
            finally
            {
                DisposeMonitors(monitors);
            }
        }
    }

    private bool TryRunNativeIteration(
        MacroDocument document,
        PlaybackExecutionOptions options,
        CompiledPlaybackPlan iterationPlan,
        NativePlaybackPreparedPlan? nativePreparedPlan,
        CancellationToken cancellationToken,
        ref uint sequence,
        ref int actionsSubmitted,
        NativePlaybackRunControl? playbackControl = null, long timelineStart = 0)
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
                enableCpuScan: NativePlaybackWarmup.CpuScanReady,
                playbackControl: playbackControl, timelineStart: timelineStart,
                generation: options.PressFloor.Generation, compensateSendInput: options.CompensateSendInputCost);
        }
        else
        {
            ranNative = NativePlaybackEngine.TryRun(
                iterationPlan,
                options.Precision,
                cancellationToken,
                out diagnostics,
                out fallbackReason,
                enableCpuScan: NativePlaybackWarmup.CpuScanReady,
                playbackControl: playbackControl, timelineStart: timelineStart,
                generation: options.PressFloor.Generation, compensateSendInput: options.CompensateSendInputCost);
        }

        if (!ranNative)
        {
            PlaybackRuntimeStatus.ReportBackend(diagnostics.BackendName, fallbackReason);
            sendInput.SetNativePlaybackDiagnostics(diagnostics with { NativeFallbackReason = fallbackReason });
            return false;
        }

        PlaybackRuntimeStatus.ReportBackend(diagnostics.BackendName, diagnostics.NativeFallbackReason);
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
        return nativePreparationAvailable && !options.BackgroundWindow
            && CanUseNativePrecision(options.Precision)
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
        out NativePlaybackPreparedPlan? nativePlan,
        out PlaybackInputSafetyPlan? safetyPlan)
    {
        lock (preparedPlanGate)
        {
            if (ReferenceEquals(document, cachedDocument)
                && cachedOptions == options
                && cachedCompiledPlan is not null)
            {
                compiledPlan = cachedCompiledPlan;
                nativePlan = cachedNativePlan;
                safetyPlan = cachedSafetyPlan;
                return true;
            }
        }

        compiledPlan = null;
        nativePlan = null;
        safetyPlan = null;
        return false;
    }

    private void ClearPreparedPlan()
    {
        lock (preparedPlanGate)
        {
            cachedNativePlan?.Dispose();
            cachedNativePlan = null;
            cachedCompiledPlan = null;
            cachedSafetyPlan = null;
            cachedDocument = null;
            cachedOptions = null;
        }
    }

    private bool CanUseNativePreparedPlan(MacroDocument document, PlaybackExecutionOptions options)
    {
        return nativePreparationAvailable && !options.BackgroundWindow
            && CanUseNativePrecision(options.Precision)
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
                case PixelWhenStep or WeakNetStep or NetLimitStep or ClockShiftStep or ParallelStep or TextWhenStep or ParallelBranchStep or FrameLockStep or YsrFpsToggleStep:
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
        long qpcFrequency,
        PrecisionMode precision,
        Action stopAllRequested,
        Action? stopIterationRequested = null,
        bool applyStepWindows = true,
        PlaybackPauseCoordinator? pauseCoordinator = null,
        ConditionTimeline? timeline = null,
        InputPressFloor? pressFloor = null)
    {
        var monitors = new List<ConditionMonitor>();
        var conditionWindows = applyStepWindows
            ? CreateConditionTimeWindows(document, qpcFrequency)
            : [];
        var allConditions = document.EffectiveConditions;
        for (var index = 0; index < allConditions.Count; index++)
        {
            var cond = allConditions[index];
            if (cond.ExecutionMode == ConditionExecutionMode.GateMainSequence)
            {
                continue;
            }

            if (!cond.HasActivationConstraint)
            {
                continue;
            }

            var intervals = ConditionActivationSchedule.Resolve(cond, conditionWindows, qpcFrequency);
            if (intervals.Count == 0)
            {
                continue;
            }

            monitors.Add(new ConditionMonitor(
                cond,
                evaluator,
                inputSink,
                macroResolver,
                macroStartTick,
                qpcFrequency,
                precision,
                stopAllRequested,
                stopIterationRequested,
                pauseCoordinator,
                timeline,
                index,
                intervals,
                pressFloor));
        }
        return monitors;
    }

    private PlaybackRunResult RunManagedControlFlow(
        MacroDocument document,
        PlaybackExecutionOptions options,
        CancellationTokenSource runCancellation,
        CancellationToken cancellationToken,
        IReadOnlyList<InputAction> completedIterationReleaseActions,
        IReadOnlyList<InputAction> interruptedIterationReleaseActions,
        IReadOnlyList<InputAction> emergencyReleaseActions)
    {
        var qpcFrequency = clock.Frequency;
        var delayStrategy = configuredDelayStrategy ?? new QpcPlaybackDelayStrategy(clock, options.Precision);
        var iterationsTarget = options.Mode == PlaybackMode.FixedCount ? options.Count : int.MaxValue;
        var iterationsCompleted = 0;
        var actionsSubmitted = 0;
        var sequence = 1u;
        using var conditionEvaluator = new CompositeConditionEvaluator(livePixelEvaluator);
        var playbackTriggerTick = clock.GetTimestamp();
        try
        {
            while (iterationsCompleted < iterationsTarget)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var gateStartTick = clock.GetTimestamp();
                var timeline = new ConditionTimeline(
                    document.EffectiveConditions.Count,
                    playbackTriggerTick,
                    gateStartTick);
                var gateWait = TryWaitForStartupGatesOrCancel(
                    document,
                    conditionEvaluator,
                    delayStrategy,
                    options.Precision,
                    ref gateStartTick,
                    qpcFrequency,
                    cancellationToken,
                    playbackTriggerTick,
                    timeline,
                    options.PressFloor);
                if (gateWait == StartupGateWaitResult.Failed)
                {
                    SubmitSafetyReleaseActions(
                        completedIterationReleaseActions,
                        ref sequence,
                        ref actionsSubmitted);
                    return new PlaybackRunResult(
                        PlaybackRunStatus.Completed,
                        iterationsCompleted,
                        actionsSubmitted,
                        Cancelled: true,
                        inputSink.GetStats());
                }

                if (gateWait == StartupGateWaitResult.SkipIteration)
                {
                    SubmitSafetyReleaseActions(
                        completedIterationReleaseActions,
                        ref sequence,
                        ref actionsSubmitted);
                    iterationsCompleted++;
                    continue;
                }

                var iterationStartTick = gateStartTick;
                using var iterationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var iterationToken = iterationCancellation.Token;
                var conditionStopIterationRequested = 0;
                void StopIteration()
                {
                    Interlocked.Exchange(ref conditionStopIterationRequested, 1);
                    iterationCancellation.Cancel();
                }

                using var pauseCoordinator = new PlaybackPauseCoordinator(clock);
                var monitors = CreateConditionMonitors(
                    document,
                    conditionEvaluator,
                    iterationStartTick,
                    qpcFrequency,
                    options.Precision,
                    runCancellation.Cancel,
                    StopIteration,
                    pauseCoordinator: pauseCoordinator,
                    timeline: timeline,
                    pressFloor: options.PressFloor);
                var runner = new ManagedMacroControlFlowRunner(
                    inputSink,
                    delayStrategy,
                    clock,
                    GetPixelEvaluator(options.PixelMode),
                    macroResolver,
                    pauseCoordinator,
                    textEvaluator: (step, token) => conditionEvaluator.Evaluate(
                        new TextMatcher(step.Region, step.ExpectedText, step.Contains, step.Language, step.UseRegex),
                        token),
                    pressFloor: options.PressFloor, precision: options.Precision);
                var flow = MacroControlFlowResult.Completed;
                try
                {
                    ActivateAllMonitors(monitors);
                    flow = runner.Run(
                        document,
                        iterationStartTick,
                        qpcFrequency,
                        iterationToken,
                        options.NoWait,
                        ref sequence,
                        ref actionsSubmitted);
                    CompleteAllMonitorsAfterCurrentEvaluation(monitors);
                    WaitForTriggeredConditionActions(monitors, iterationToken);
                    DeactivateAllMonitors(monitors);
                }
                catch (OperationCanceledException) when (
                    Volatile.Read(ref conditionStopIterationRequested) != 0
                    && !cancellationToken.IsCancellationRequested)
                {
                    flow = MacroControlFlowResult.StopIteration;
                }
                finally
                {
                    DisposeMonitors(monitors);
                }

                var stoppedByCondition = Volatile.Read(ref conditionStopIterationRequested) != 0;
                if (stoppedByCondition && flow != MacroControlFlowResult.StopAll)
                {
                    flow = MacroControlFlowResult.StopIteration;
                }

                if (flow == MacroControlFlowResult.StopAll)
                {
                    SubmitSafetyReleaseActions(
                        emergencyReleaseActions,
                        ref sequence,
                        ref actionsSubmitted);
                    return new PlaybackRunResult(
                        PlaybackRunStatus.Completed,
                        iterationsCompleted,
                        actionsSubmitted,
                        Cancelled: true,
                        inputSink.GetStats());
                }

                if (flow == MacroControlFlowResult.StopIteration)
                {
                    SubmitSafetyReleaseActions(
                        interruptedIterationReleaseActions,
                        ref sequence,
                        ref actionsSubmitted);
                    iterationsCompleted++;
                    continue;
                }

                if (flow == MacroControlFlowResult.StopCurrent)
                {
                    SubmitSafetyReleaseActions(
                        interruptedIterationReleaseActions,
                        ref sequence,
                        ref actionsSubmitted);
                    iterationsCompleted++;
                    continue;
                }

                SubmitSafetyReleaseActions(
                    completedIterationReleaseActions,
                    ref sequence,
                    ref actionsSubmitted);
                iterationsCompleted++;
            }

            return new PlaybackRunResult(
                PlaybackRunStatus.Completed,
                iterationsCompleted,
                actionsSubmitted,
                Cancelled: false,
                inputSink.GetStats());
        }
        catch (OperationCanceledException)
        {
            SubmitSafetyReleaseActions(
                emergencyReleaseActions,
                ref sequence,
                ref actionsSubmitted);
            return new PlaybackRunResult(
                PlaybackRunStatus.Completed,
                iterationsCompleted,
                actionsSubmitted,
                Cancelled: true,
                inputSink.GetStats());
        }
        catch
        {
            SubmitSafetyReleaseActions(emergencyReleaseActions);
            throw;
        }
    }

    private bool RequiresManagedControlFlow(MacroDocument document, PlaybackExecutionOptions options)
    {
        return MacroControlFlowInspector.RequiresManagedExecution(document, macroResolver)
            || ConditionActionsRequireManagedControlFlow(document)
            || (HasPauseMainTimelineCondition(document) && !CanAttemptNativeIteration(options));
    }

    private bool ConditionActionsRequireManagedControlFlow(MacroDocument document)
    {
        return document.EffectiveConditions.Any(condition =>
            MacroControlFlowInspector.RequiresManagedExecution(
                new MacroDocument(1, "_condition_then", PlaybackSettings.Default, condition.ThenSteps, null),
                macroResolver));
    }

    private static bool HasPauseMainTimelineCondition(MacroDocument document)
    {
        return document.EffectiveConditions.Any(condition => condition.ExecutionMode == ConditionExecutionMode.PauseMainTimeline);
    }

    private static bool HasStartupGateCondition(MacroDocument document)
    {
        return document.EffectiveConditions.Any(condition => condition.ExecutionMode == ConditionExecutionMode.GateMainSequence);
    }

    /// <summary>
    /// Waits until every gate in the list currently matches (AND within the list).
    /// Prefer <see cref="WaitForStartupGatesSequentially"/> for ordered startup stages.
    /// Returns false on timeout; throws on cancellation.
    /// </summary>
    public static bool WaitForStartupGates(
        IReadOnlyList<ConditionalDirective> gates,
        IConditionEvaluator evaluator,
        IHighResolutionClock clock,
        IPlaybackDelayStrategy delayStrategy,
        long triggerTick,
        long qpcFrequency,
        CancellationToken cancellationToken,
        TimeSpan? pollInterval = null,
        IReadOnlyList<ConditionTimeInterval>? activationIntervals = null)
    {
        if (gates.Count == 0)
        {
            return true;
        }

        var poll = pollInterval is { } value && value > TimeSpan.Zero
            ? value
            : TimeSpan.FromMilliseconds(25);
        var pollTicks = Math.Max(1, (long)Math.Round(poll.TotalSeconds * qpcFrequency, MidpointRounding.AwayFromZero));
        var intervals = activationIntervals
            ?? BuildLegacyGateIntervals(gates);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = clock.GetTimestamp();
            var elapsedMs = (now - triggerTick) * 1000.0 / qpcFrequency;
            if (ConditionActivationSchedule.IsPastAll(intervals, elapsedMs))
            {
                return false;
            }

            if (!ConditionActivationSchedule.Contains(intervals, elapsedMs))
            {
                var nextStart = ConditionActivationSchedule.NextStartAfter(intervals, elapsedMs);
                if (nextStart is null)
                {
                    return false;
                }

                var dueTick = triggerTick
                    + (long)Math.Round(nextStart.Value.TotalSeconds * qpcFrequency, MidpointRounding.AwayFromZero);
                delayStrategy.WaitUntil(dueTick, qpcFrequency, cancellationToken, noWait: false);
                continue;
            }

            var allMatched = true;
            foreach (var gate in gates)
            {
                if (!evaluator.Evaluate(gate.Condition, cancellationToken))
                {
                    allMatched = false;
                    break;
                }
            }

            if (allMatched)
            {
                return true;
            }

            delayStrategy.WaitUntil(now + pollTicks, qpcFrequency, cancellationToken, noWait: false);
        }
    }

    private static IReadOnlyList<ConditionTimeInterval> BuildLegacyGateIntervals(
        IReadOnlyList<ConditionalDirective> gates)
    {
        var intervals = new List<ConditionTimeInterval>();
        foreach (var gate in gates)
        {
            if (!gate.HasTimeRange && !gate.HasStepRange)
            {
                continue;
            }

            intervals.Add(new ConditionTimeInterval(gate.WindowStart ?? TimeSpan.Zero, gate.WindowEnd));
        }

        return intervals.Count > 0
            ? intervals
            : [new ConditionTimeInterval(TimeSpan.Zero, null)];
    }

    /// <summary>
    /// Waits for each GateMainSequence condition in document order. After a gate matches,
    /// invokes <paramref name="onGatePassed"/> (used to run that gate's then-actions) before
    /// advancing to the next gate. Main sequence should start only after this returns true.
    /// </summary>
    public static bool WaitForStartupGatesSequentially(
        IReadOnlyList<ConditionalDirective> gates,
        IConditionEvaluator evaluator,
        IHighResolutionClock clock,
        IPlaybackDelayStrategy delayStrategy,
        long qpcFrequency,
        CancellationToken cancellationToken,
        Action<ConditionalDirective>? onGatePassed = null,
        TimeSpan? pollInterval = null,
        long? playbackTriggerTick = null,
        long? mainIterationTick = null,
        ConditionTimeline? timeline = null,
        IReadOnlyList<ConditionalDirective>? allConditions = null,
        IReadOnlyList<StepTimeWindow>? stepTimeWindows = null)
    {
        var playbackTick = playbackTriggerTick ?? clock.GetTimestamp();
        var iterationTick = mainIterationTick ?? playbackTick;
        var previousFinishedTick = iterationTick;
        var windows = stepTimeWindows ?? [];

        foreach (var gate in gates)
        {
            var conditionIndex = IndexOfCondition(allConditions, gate);
            var baseTick = ResolveGateBaseTick(gate, conditionIndex, playbackTick, iterationTick, previousFinishedTick);
            var intervals = ConditionActivationSchedule.ResolveStartupGate();

            if (!WaitForStartupGates(
                    [gate],
                    evaluator,
                    clock,
                    delayStrategy,
                    baseTick,
                    qpcFrequency,
                    cancellationToken,
                    pollInterval,
                    intervals))
            {
                return false;
            }

            onGatePassed?.Invoke(gate);
            previousFinishedTick = clock.GetTimestamp();
            if (conditionIndex >= 0)
            {
                timeline?.MarkFinished(conditionIndex, previousFinishedTick);
            }
        }

        return true;
    }

    private static int IndexOfCondition(IReadOnlyList<ConditionalDirective>? allConditions, ConditionalDirective gate)
    {
        if (allConditions is null)
        {
            return -1;
        }

        for (var i = 0; i < allConditions.Count; i++)
        {
            if (ReferenceEquals(allConditions[i], gate)
                || string.Equals(allConditions[i].Id, gate.Id, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static long ResolveGateBaseTick(
        ConditionalDirective gate,
        int conditionIndex,
        long playbackTriggerTick,
        long mainIterationTick,
        long previousFinishedTick)
    {
        return gate.TimeBase switch
        {
            ConditionTimeBase.MainIteration => mainIterationTick,
            ConditionTimeBase.AfterPreviousCondition when conditionIndex <= 0 => mainIterationTick,
            ConditionTimeBase.AfterPreviousCondition => previousFinishedTick,
            _ => playbackTriggerTick
        };
    }

    private enum StartupGateWaitResult
    {
        Proceed,
        SkipIteration,
        Failed
    }

    private StartupGateWaitResult TryWaitForStartupGatesOrCancel(
        MacroDocument document,
        CompositeConditionEvaluator conditionEvaluator,
        IPlaybackDelayStrategy delayStrategy,
        PrecisionMode precision,
        ref long plannedIterationStartTick,
        long qpcFrequency,
        CancellationToken cancellationToken,
        long playbackTriggerTick,
        ConditionTimeline timeline,
        InputPressFloor pressFloor)
    {
        // Fast path without an enumerator or list: almost every macro has no startup gate.
        var hasGate = false;
        foreach (var condition in document.EffectiveConditions)
        {
            if (condition.ExecutionMode == ConditionExecutionMode.GateMainSequence) { hasGate = true; break; }
        }
        if (!hasGate)
        {
            timeline.SetMainIterationTick(plannedIterationStartTick);
            return StartupGateWaitResult.Proceed;
        }

        var gates = document.EffectiveConditions
            .Where(condition => condition.ExecutionMode == ConditionExecutionMode.GateMainSequence)
            .ToList();
        if (gates.Count == 0)
        {
            timeline.SetMainIterationTick(plannedIterationStartTick);
            return StartupGateWaitResult.Proceed;
        }

        try
        {
            var stepWindows = CreateConditionTimeWindows(document, qpcFrequency);
            if (!WaitForStartupGatesSequentially(
                    gates,
                    conditionEvaluator,
                    clock,
                    delayStrategy,
                    qpcFrequency,
                    cancellationToken,
                    gate =>
                    {
                        var flow = ExecuteStartupGateThenSteps(gate, delayStrategy, precision, cancellationToken, pressFloor);
                        if (flow == MacroControlFlowResult.StopAll)
                        {
                            throw new OperationCanceledException();
                        }

                        if (flow is MacroControlFlowResult.StopIteration or MacroControlFlowResult.StopCurrent)
                        {
                            throw new StartupGateStopIterationException();
                        }
                    },
                    playbackTriggerTick: playbackTriggerTick,
                    mainIterationTick: plannedIterationStartTick,
                    timeline: timeline,
                    allConditions: document.EffectiveConditions,
                    stepTimeWindows: stepWindows))
            {
                return StartupGateWaitResult.Failed;
            }
        }
        catch (StartupGateStopIterationException)
        {
            // Stop-iteration in gate then-actions: skip main this round; Toggle/Hold loop continues.
            return StartupGateWaitResult.SkipIteration;
        }

        // Restart iteration timeline after the gates so main sequence DueTicks are relative to gate pass.
        plannedIterationStartTick = clock.GetTimestamp();
        timeline.SetMainIterationTick(plannedIterationStartTick);
        return StartupGateWaitResult.Proceed;
    }

    private MacroControlFlowResult ExecuteStartupGateThenSteps(
        ConditionalDirective gate,
        IPlaybackDelayStrategy delayStrategy,
        PrecisionMode precision,
        CancellationToken cancellationToken,
        InputPressFloor pressFloor)
    {
        if (gate.ThenSteps.Count == 0)
        {
            return MacroControlFlowResult.Completed;
        }

        var document = new MacroDocument(1, "_startup_gate_then", PlaybackSettings.Default, gate.ThenSteps, null);
        if (MacroControlFlowInspector.RequiresManagedExecution(document, macroResolver))
        {
            uint managedSequence = 100_000;
            var managedActionsSubmitted = 0;
            var runner = new ManagedMacroControlFlowRunner(
                inputSink,
                delayStrategy,
                clock,
                pixelEvaluator: null,
                macroResolver,
                pressFloor: pressFloor);
            return runner.Run(
                document,
                clock.GetTimestamp(),
                clock.Frequency,
                cancellationToken,
                noWait: false,
                ref managedSequence,
                ref managedActionsSubmitted);
        }

        var plan = CompiledPlaybackPlan.Create(
            document,
            clock.Frequency,
            pixelEvaluator: null,
            macroResolver,
            pressFloor: pressFloor);

        if (CanUseNativePrecision(precision)
            && inputSink is SendInputMacroSink
            && NativePlaybackEngine.TryRun(
                plan,
                precision,
                cancellationToken,
                out _,
                out _,
                enableCpuScan: false,
                engineMode: NativePlaybackEngineMode.Inline,
                generation: pressFloor.Generation))
        {
            return MacroControlFlowResult.Completed;
        }

        var startTick = clock.GetTimestamp();
        uint sequence = 100_000;
        foreach (var batch in plan.Batches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            delayStrategy.WaitUntil(startTick + batch.DueTick, clock.Frequency, cancellationToken, noWait: false);
            if (inputSink is SendInputMacroSink sendInput)
            {
                sendInput.SubmitPrepared(sequence, batch.PreparedBatch);
                sequence += (uint)batch.PreparedBatch.ActionCount;
            }
            else
            {
                foreach (var action in batch.PreparedBatch.Actions)
                {
                    inputSink.Submit(sequence++, action);
                }
            }
        }

        return MacroControlFlowResult.Completed;
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
        bool noWait,
        PlaybackPauseCoordinator? pauseCoordinator = null,
        bool anotherIterationFollows = false)
    {
        // Conditions must stay active until the iteration ends. Without conditions the tail is still part of the loop period
        // ("press, release, wait 100" repeats every 100 ms, not every ~10 ms), but only matters when another iteration follows.
        if (monitors.Count > 0 || anotherIterationFollows)
        {
            if (pauseCoordinator is null)
            {
                delayStrategy.WaitUntil(iterationEndTick, qpcFrequency, cancellationToken, noWait);
            }
            else
            {
                pauseCoordinator.WaitUntil(iterationEndTick, delayStrategy, qpcFrequency, cancellationToken, noWait);
            }
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
        foreach (var monitor in monitors) monitor.Deactivate();
        var failures = new List<Exception>();
        foreach (var monitor in monitors)
            try { monitor.Dispose(); } catch (Exception ex) { failures.Add(ex); }
        if (failures.Count > 0) throw new AggregateException("条件监控执行失败", failures);
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

    internal IReadOnlyList<StepTimeWindow> CreateConditionTimeWindows(MacroDocument document, long qpcFrequency)
    {
        var windows = new List<StepTimeWindow>();
        var elapsedTicks = 0L;
        var visiting = new HashSet<MacroDocument>(ReferenceEqualityComparer.Instance) { document };
        AddStepTimeWindows(document.Steps, [], windows, ref elapsedTicks, qpcFrequency, depth: 0, visiting);
        return windows;
    }

    private void AddStepTimeWindows(
        IReadOnlyList<MacroStep> steps,
        IReadOnlyList<int> parentPath,
        List<StepTimeWindow> windows,
        ref long elapsedTicks,
        long qpcFrequency,
        int depth,
        HashSet<MacroDocument> visiting)
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
            var durationTicks = EstimateStepDurationTicks(step, qpcFrequency, depth, visiting);

            windows.Add(new StepTimeWindow(windows.Count, path, startTicks, startTicks + durationTicks));

            if (step is RepeatStep repeat)
            {
                var oneIterationTicks = EstimateStepsDurationTicks(repeat.Steps, qpcFrequency, depth + 1, visiting);
                var firstIterationElapsed = elapsedTicks;
                AddStepTimeWindows(repeat.Steps, path, windows, ref firstIterationElapsed, qpcFrequency, depth + 1, visiting);
                elapsedTicks += oneIterationTicks * Math.Max(1, repeat.Count);
            }
            else
            {
                elapsedTicks += durationTicks;
            }
        }
    }

    private long EstimateStepsDurationTicks(
        IReadOnlyList<MacroStep> steps,
        long qpcFrequency,
        int depth,
        HashSet<MacroDocument> visiting)
    {
        if (depth > 16)
        {
            return 0L;
        }

        var total = 0L;
        foreach (var step in steps)
        {
            total += EstimateStepDurationTicks(step, qpcFrequency, depth, visiting);
        }

        return total;
    }

    private long EstimateStepDurationTicks(
        MacroStep step,
        long qpcFrequency,
        int depth,
        HashSet<MacroDocument> visiting)
    {
        if (depth > 16)
        {
            return 0L;
        }

        static long ToTicks(TimeSpan duration, long frequency) =>
            (long)Math.Round(duration.TotalSeconds * frequency, MidpointRounding.AwayFromZero);

        return step switch
        {
            KeyStep key => ToTicks(key.Hold, qpcFrequency),
            MouseMoveStep move => ToTicks(move.Duration, qpcFrequency),
            MouseButtonStep button => ToTicks(button.Hold, qpcFrequency),
            OcrClickStep ocrClick =>
                ToTicks(ocrClick.Hold, qpcFrequency) * Math.Clamp(ocrClick.ClickCount, 1, 3)
                + ToTicks(ocrClick.Interval, qpcFrequency) * Math.Max(0, Math.Clamp(ocrClick.ClickCount, 1, 3) - 1),
            ConsumerStep consumer => ToTicks(consumer.Hold, qpcFrequency),
            WaitStep wait => ToTicks(wait.MaxDuration ?? wait.Duration, qpcFrequency),
            RepeatStep repeat => EstimateStepsDurationTicks(repeat.Steps, qpcFrequency, depth + 1, visiting) * Math.Max(1, repeat.Count),
            MacroCallStep macro => EstimateMacroCallDurationTicks(macro, qpcFrequency, depth, visiting),
            PixelWhenStep pixel => EstimateStepsDurationTicks(pixel.ThenSteps, qpcFrequency, depth + 1, visiting),
            TextWhenStep text => EstimateStepsDurationTicks(text.ThenSteps, qpcFrequency, depth + 1, visiting),
            ParallelBranchStep branch => EstimateStepsDurationTicks(branch.Steps, qpcFrequency, depth + 1, visiting),
            ParallelStep parallel => Math.Max(
                EstimateStepsDurationTicks(parallel.Left.Steps, qpcFrequency, depth + 1, visiting),
                EstimateStepsDurationTicks(parallel.Right.Steps, qpcFrequency, depth + 1, visiting)),
            WeakNetStep weak when weak.Action == WeakNetAction.Hold => ToTicks(weak.EffectiveDuration, qpcFrequency),
            _ => 0L
        };
    }

    private long EstimateMacroCallDurationTicks(
        MacroCallStep macro,
        long qpcFrequency,
        int depth,
        HashSet<MacroDocument> visiting)
    {
        if (macroResolver is null || macroResolver(macro.Macro) is not { } document)
        {
            return 0L;
        }

        if (!visiting.Add(document))
        {
            return 0L;
        }

        try
        {
            return EstimateStepsDurationTicks(document.Steps, qpcFrequency, depth + 1, visiting);
        }
        finally
        {
            visiting.Remove(document);
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

    private void SubmitSafetyReleaseActions(
        IReadOnlyList<InputAction> releases,
        ref uint sequence,
        ref int actionsSubmitted)
    {
        foreach (var action in releases)
        {
            try
            {
                inputSink.Submit(sequence++, action);
                actionsSubmitted++;
            }
            catch
            {
                // A failed SendInput must not prevent the remaining key-up packets
                // or replace the original playback exception.
            }
        }
    }

    private void SubmitSafetyReleaseActions(IReadOnlyList<InputAction> releases)
    {
        PlaybackInputSafetyRegistry.SubmitBestEffort(inputSink, releases);
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

internal sealed class StartupGateStopIterationException : Exception;
