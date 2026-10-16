using MacroHid.Core;

namespace MacroHid.Runtime;

public interface IConditionEvaluator
{
    bool Evaluate(IConditionMatcher matcher, CancellationToken cancellationToken = default);
}

public sealed class ConditionMonitor : IDisposable
{
    private readonly ConditionalDirective directive;
    private readonly IConditionEvaluator evaluator;
    private readonly IMacroInputSink inputSink;
    private readonly Func<string, MacroDocument?>? macroResolver;
    private readonly long macroStartTick;
    private readonly long qpcFrequency;
    private readonly PrecisionMode precision;
    private readonly Action? stopAllRequested;
    private readonly Action? stopIterationRequested;
    private readonly Action? stopCurrentRequested;
    private readonly PlaybackPauseCoordinator? pauseCoordinator;
    private readonly ConditionTimeline? timeline;
    private readonly int conditionIndex;
    private readonly IReadOnlyList<ConditionTimeInterval> activationIntervals;
    private readonly IHighResolutionClock clock = new QpcHighResolutionClock();
    private readonly IPlaybackDelayStrategy delayStrategy;
    private readonly IPlaybackDelayStrategy pollingDelayStrategy;
    private readonly InputPressFloor pressFloor;

    private CancellationTokenSource cts = new();
    private Thread? monitorThread;
    private volatile bool triggered;
    private volatile bool active;
    private Exception? failure;
    private int disposed;
    private CompiledPlaybackPlan? preparedThenPlan;
    public Exception? LastError => failure;

    public ConditionMonitor(
        ConditionalDirective directive,
        IConditionEvaluator evaluator,
        IMacroInputSink inputSink,
        Func<string, MacroDocument?>? macroResolver = null,
        long macroStartTick = 0,
        long qpcFrequency = 0)
        : this(
            directive,
            evaluator,
            inputSink,
            macroResolver,
            macroStartTick,
            qpcFrequency,
            PrecisionMode.ExtremeDuringPlayback,
            null,
            null,
            null)
    {
    }

    internal ConditionMonitor(
        ConditionalDirective directive,
        IConditionEvaluator evaluator,
        IMacroInputSink inputSink,
        Func<string, MacroDocument?>? macroResolver,
        long macroStartTick,
        long qpcFrequency,
        PrecisionMode precision = PrecisionMode.ExtremeDuringPlayback,
        Action? stopAllRequested = null,
        Action? stopIterationRequested = null,
        PlaybackPauseCoordinator? pauseCoordinator = null,
        ConditionTimeline? timeline = null,
        int conditionIndex = -1,
        IReadOnlyList<ConditionTimeInterval>? activationIntervals = null,
        InputPressFloor? pressFloor = null,
        Action? stopCurrentRequested = null)
    {
        this.directive = directive;
        this.evaluator = evaluator;
        this.inputSink = inputSink;
        this.macroResolver = macroResolver;
        this.macroStartTick = macroStartTick;
        this.qpcFrequency = qpcFrequency;
        this.precision = precision;
        this.stopAllRequested = stopAllRequested;
        this.stopIterationRequested = stopIterationRequested;
        this.stopCurrentRequested = stopCurrentRequested;
        this.pauseCoordinator = pauseCoordinator;
        this.timeline = timeline;
        this.conditionIndex = conditionIndex;
        this.pressFloor = pressFloor ?? InputPressFloor.Factory;
        this.activationIntervals = activationIntervals
            ?? BuildLegacyIntervals(directive);
        delayStrategy = new QpcPlaybackDelayStrategy(clock, precision);
        pollingDelayStrategy = new QpcPlaybackDelayStrategy(clock, PrecisionMode.Balanced);
    }

    private static IReadOnlyList<ConditionTimeInterval> BuildLegacyIntervals(ConditionalDirective directive)
    {
        if (!directive.HasTimeRange && !directive.HasStepRange)
        {
            return [];
        }

        return
        [
            new ConditionTimeInterval(
                directive.WindowStart ?? TimeSpan.Zero,
                directive.WindowEnd,
                directive.TimeBase)
        ];
    }

    public bool HasTriggered => triggered;
    public string DirectiveId => directive.Id;
    public int StartStepIndex => directive.StartStepIndex;
    public int EndStepIndex => directive.EndStepIndex;

    public void Activate()
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        if (active) return;
        if (monitorThread?.IsAlive == true) throw new InvalidOperationException("条件线程尚未结束，不能重复启动。");
        failure = null;
        if (directive.ThenSteps.Count > 0 && ConditionActionPlanCache.CanCache(directive.ThenSteps))
        {
            var document = new MacroDocument(1, "_condition_then", PlaybackSettings.Default, directive.ThenSteps);
            if (!MacroControlFlowInspector.RequiresManagedExecution(document, macroResolver))
                preparedThenPlan = ConditionActionPlanCache.Get(directive.ThenSteps, () =>
                    CompiledPlaybackPlan.Create(document, EffectiveFrequency(), macroResolver: macroResolver, pressFloor: pressFloor));
        }
        active = true;
        if (cts.IsCancellationRequested)
        {
            cts.Dispose();
            cts = new CancellationTokenSource();
        }

        monitorThread = new Thread(() => PollLoop(cts.Token))
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = $"MacroHID-Condition-{directive.Id}"
        };
        monitorThread.Start();
    }

    public void Deactivate()
    {
        active = false;
        cts.Cancel();
    }

    public void CompleteAfterCurrentEvaluation()
    {
        active = false;
    }

    public void WaitForTriggeredCompletion(CancellationToken cancellationToken)
    {
        if (monitorThread is null)
        {
            return;
        }

        while (monitorThread.IsAlive)
        {
            cancellationToken.ThrowIfCancellationRequested();
            monitorThread.Join(TimeSpan.FromMilliseconds(10));
        }
    }

    private void PollLoop(CancellationToken cancellationToken)
    {
        try { PollCore(cancellationToken); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            failure = ex;
            active = false;
            try { stopAllRequested?.Invoke(); } catch { }
        }
    }

    private void PollCore(CancellationToken cancellationToken)
    {
        // Expensive recognition must not compete with input timing at time-critical priority.
        using var precisionContext = PrecisionPlaybackContext.Enter(PrecisionMode.Balanced);
        var frequency = EffectiveFrequency();
        var pollTicks = Math.Max(1, (long)Math.Round(directive.EffectivePollInterval.TotalSeconds * frequency, MidpointRounding.AwayFromZero));
        var nextPollTick = clock.GetTimestamp();

        try
        {
            while (!cancellationToken.IsCancellationRequested && active && !triggered)
            {
                try
                {
                    if (!WaitUntilInsideTimeWindow(cancellationToken))
                    {
                        return;
                    }

                    if (evaluator.Evaluate(directive.Condition, cancellationToken))
                    {
                        triggered = true;
                        using var pauseLease = directive.ExecutionMode == ConditionExecutionMode.PauseMainTimeline
                            ? pauseCoordinator?.Pause()
                            : null;
                        ExecuteThenSteps(cancellationToken);
                        return;
                    }

                    nextPollTick += pollTicks;
                    // Skip missed polls instead of repeatedly running expensive OCR to catch up.
                    var now = clock.GetTimestamp();
                    if (nextPollTick <= now) nextPollTick = now + pollTicks;
                    pollingDelayStrategy.WaitUntil(nextPollTick, frequency, cancellationToken, noWait: false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
        finally
        {
            timeline?.MarkFinished(conditionIndex, clock.GetTimestamp());
        }
    }

    private bool WaitUntilInsideTimeWindow(CancellationToken cancellationToken)
    {
        if (activationIntervals.Count == 0)
        {
            return false;
        }

        if (qpcFrequency <= 0 && EffectiveFrequency() <= 0)
        {
            return true;
        }

        var frequency = EffectiveFrequency();
        while (!cancellationToken.IsCancellationRequested && active)
        {
            pauseCoordinator?.WaitUntilResumed(cancellationToken);
            RuntimeNativeMethods.QueryPerformanceCounter(out var now);

            if (IsInsideActivationWindow(now, frequency, cancellationToken))
            {
                return true;
            }

            if (IsPastAllActivationWindows(now, frequency, cancellationToken))
            {
                return false;
            }

            var nextStart = NextActivationStartAfter(now, frequency, cancellationToken);
            if (nextStart is not { } next)
            {
                return false;
            }

            var baseTick = ResolveAnchorBaseTick(next.Anchor, cancellationToken);
            if (baseTick <= 0)
            {
                pollingDelayStrategy.WaitUntil(
                    now + Math.Max(1, frequency / 200),
                    frequency,
                    cancellationToken,
                    noWait: false);
                continue;
            }

            var dueTick = baseTick + (long)Math.Round(next.Start.TotalSeconds * frequency, MidpointRounding.AwayFromZero);
            if (pauseCoordinator is null)
            {
                pollingDelayStrategy.WaitUntil(dueTick, frequency, cancellationToken, noWait: false);
            }
            else
            {
                pauseCoordinator.WaitUntil(dueTick, delayStrategy, frequency, cancellationToken, noWait: false);
            }
        }

        return false;
    }

    private bool IsInsideActivationWindow(long nowTick, long frequency, CancellationToken cancellationToken)
    {
        foreach (var interval in activationIntervals)
        {
            var baseTick = ResolveAnchorBaseTick(interval.Anchor, cancellationToken);
            if (baseTick <= 0)
            {
                continue;
            }

            var elapsedMs = ElapsedMs(nowTick, baseTick, frequency);
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

    private bool IsPastAllActivationWindows(long nowTick, long frequency, CancellationToken cancellationToken)
    {
        if (activationIntervals.Count == 0)
        {
            return true;
        }

        foreach (var interval in activationIntervals)
        {
            if (interval.End is null)
            {
                return false;
            }

            var baseTick = ResolveAnchorBaseTick(interval.Anchor, cancellationToken);
            if (baseTick <= 0)
            {
                return false;
            }

            var elapsedMs = ElapsedMs(nowTick, baseTick, frequency);
            if (elapsedMs <= interval.End.Value.TotalMilliseconds)
            {
                return false;
            }
        }

        return true;
    }

    private (TimeSpan Start, ConditionTimeBase Anchor)? NextActivationStartAfter(
        long nowTick,
        long frequency,
        CancellationToken cancellationToken)
    {
        (TimeSpan Start, ConditionTimeBase Anchor)? next = null;
        foreach (var interval in activationIntervals)
        {
            var baseTick = ResolveAnchorBaseTick(interval.Anchor, cancellationToken);
            if (baseTick <= 0)
            {
                continue;
            }

            var elapsedMs = ElapsedMs(nowTick, baseTick, frequency);
            if (elapsedMs < interval.Start.TotalMilliseconds)
            {
                if (next is not { } current || interval.Start < current.Start)
                {
                    next = (interval.Start, interval.Anchor);
                }
            }
        }

        return next;
    }

    private double ElapsedMs(long nowTick, long baseTick, long frequency)
    {
        var elapsedTicks = pauseCoordinator?.GetTimelineElapsedTicks(baseTick, nowTick)
            ?? nowTick - baseTick;
        return elapsedTicks * 1000.0 / frequency;
    }

    private long ResolveAnchorBaseTick(ConditionTimeBase anchor, CancellationToken cancellationToken)
    {
        if (timeline is null || conditionIndex < 0)
        {
            return macroStartTick;
        }

        while (!cancellationToken.IsCancellationRequested && active)
        {
            var baseTick = timeline.ResolveBaseTick(conditionIndex, anchor);
            if (baseTick > 0
                || anchor != ConditionTimeBase.AfterPreviousCondition
                || conditionIndex <= 0)
            {
                return baseTick > 0 ? baseTick : macroStartTick;
            }

            pollingDelayStrategy.WaitUntil(
                clock.GetTimestamp() + Math.Max(1, EffectiveFrequency() / 200),
                EffectiveFrequency(),
                cancellationToken,
                noWait: false);
        }

        return 0;
    }

    private long ResolveWindowBaseTick(CancellationToken cancellationToken)
        => ResolveAnchorBaseTick(directive.TimeBase, cancellationToken);

    private void ExecuteThenSteps(CancellationToken cancellationToken)
    {
        if (directive.ThenSteps.Count == 0) return;
        using var actionPrecision = PrecisionPlaybackContext.Enter(precision);

        var qpcFrequency = EffectiveFrequency();
        var document = new MacroDocument(1, "_condition_then", PlaybackSettings.Default, directive.ThenSteps, null);
        if (MacroControlFlowInspector.RequiresManagedExecution(document, macroResolver))
        {
            uint managedSequence = 100_000;
            var managedActionsSubmitted = 0;
            var runner = new ManagedMacroControlFlowRunner(
                inputSink,
                delayStrategy,
                clock,
                pixelEvaluator: pixel => evaluator.Evaluate(new PixelMatcher(
                    ScreenRegion.FromSinglePixel(pixel.Coordinate.X, pixel.Coordinate.Y), pixel.Expected, pixel.Tolerance), cancellationToken),
                macroResolver,
                textEvaluator: (step, token) => evaluator.Evaluate(
                    new TextMatcher(step.Region, step.ExpectedText, step.Contains, step.Language, step.UseRegex),
                    token),
                pollConditions: true, pressFloor: pressFloor, precision: precision);
            var flow = runner.Run(
                document,
                clock.GetTimestamp(),
                qpcFrequency,
                cancellationToken,
                noWait: false,
                ref managedSequence,
                ref managedActionsSubmitted);
            if (flow == MacroControlFlowResult.StopAll)
            {
                stopAllRequested?.Invoke();
            }
            else if (flow == MacroControlFlowResult.StopIteration)
            {
                stopIterationRequested?.Invoke();
            }
            else if (flow == MacroControlFlowResult.StopCurrent) stopCurrentRequested?.Invoke();
            return;
        }

        var plan = preparedThenPlan ?? CompiledPlaybackPlan.Create(
            document,
            qpcFrequency,
            pixelEvaluator: null,
            macroResolver,
            pressFloor: pressFloor);

        if (precision is PrecisionMode.ExtremeDuringPlayback or PrecisionMode.UltraLowJitter && inputSink is SendInputMacroSink)
        {
            var prepared = ConditionActionPlanCache.GetNative(plan);
            bool ranNative = prepared is not null
                ? NativePlaybackEngine.TryRunPrepared(prepared, precision, cancellationToken, out _, out _, enableCpuScan: NativePlaybackWarmup.CpuScanReady, generation: pressFloor.Generation)
                : NativePlaybackEngine.TryRun(plan, precision, cancellationToken, out _, out _, enableCpuScan: NativePlaybackWarmup.CpuScanReady, engineMode: NativePlaybackEngineMode.Inline, generation: pressFloor.Generation);
            if (ranNative) return;
        }

        var startTick = clock.GetTimestamp();
        uint sequence = 100_000;

        foreach (var batch in plan.Batches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            delayStrategy.WaitUntil(startTick + batch.DueTick, qpcFrequency, cancellationToken, noWait: false);
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
        delayStrategy.WaitUntil(startTick + plan.DurationTicks, qpcFrequency, cancellationToken, noWait: false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Deactivate();
        // Evaluator/token resources remain alive until their last user has exited.
        if (monitorThread is not null && monitorThread != Thread.CurrentThread) monitorThread.Join();
        cts.Dispose();
        if (failure is not null) throw new InvalidOperationException($"条件 {directive.Id} 执行失败：{failure.Message}", failure);
    }

    private long EffectiveFrequency() => qpcFrequency > 0 ? qpcFrequency : clock.Frequency;
}

public sealed class CompositeConditionEvaluator : IConditionEvaluator, IDisposable
{
    private readonly Func<PixelCondition, bool> pixelEvaluator;
    private readonly Func<TemplateMatcher, bool>? templateEvaluator;
    private readonly Func<PixelHashMatcher, bool>? pixelHashEvaluator;
    private readonly Func<TextMatcher, bool>? customTextEvaluator;
    private readonly Lazy<PaddleOcrBridge> ocrBridge = new(() => new PaddleOcrBridge());
    private readonly PixelFrameCache frames = new();
    private readonly RecentEvaluationCache<(ScreenRegion Region, string Language), OcrRecognitionResult> recognition = new();

    public CompositeConditionEvaluator(
        Func<PixelCondition, bool>? pixelEvaluator = null,
        Func<TemplateMatcher, bool>? templateEvaluator = null,
        Func<PixelHashMatcher, bool>? pixelHashEvaluator = null,
        Func<TextMatcher, bool>? textEvaluator = null)
    {
        this.pixelEvaluator = pixelEvaluator ?? ScreenPixelSampler.Matches;
        this.templateEvaluator = templateEvaluator;
        this.pixelHashEvaluator = pixelHashEvaluator;
        customTextEvaluator = textEvaluator;
    }

    public bool Evaluate(IConditionMatcher matcher, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return matcher switch
        {
            PixelMatcher pixel => EvaluatePixel(pixel),
            TemplateMatcher template => templateEvaluator?.Invoke(template) ?? EvaluateTemplate(template, cancellationToken),
            PixelHashMatcher hash => pixelHashEvaluator?.Invoke(hash) ?? EvaluatePixelHash(hash, cancellationToken),
            TextMatcher text => customTextEvaluator?.Invoke(text) ?? EvaluateText(text, cancellationToken),
            _ => false
        };
    }

    private bool EvaluatePixel(PixelMatcher pixel)
    {
        var condition = new PixelCondition(
            new PixelCoordinate(CoordinateScope.Screen, pixel.Region.TopLeft.X, pixel.Region.TopLeft.Y),
            pixel.Expected,
            pixel.Tolerance);
        return pixelEvaluator(condition);
    }


    private bool EvaluateTemplate(TemplateMatcher template, CancellationToken token)
    {
        if (template.TemplateImageData.Length == 0)
        {
            return false;
        }

        using var lease = frames.Get(template.Region, token);
        return TemplateMatchEngine.Match(template.Region, template.TemplateImageData, template.Threshold, lease.Frame, token).x >= 0;
    }

    private bool EvaluatePixelHash(PixelHashMatcher hash, CancellationToken token)
    {
        if (hash.ReferenceHash.Length == 0)
        {
            return false;
        }

        using var lease = frames.Get(hash.Region, token);
        var current = PixelHashEngine.ComputeHash(hash.Region, lease.Frame, token);
        return current.Length != 0 && PixelHashEngine.CompareSimilarity(current, hash.ReferenceHash) >= hash.SimilarityThreshold;
    }

    private bool EvaluateText(TextMatcher text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text.ExpectedText) || !ocrBridge.Value.IsAvailable)
        {
            return false;
        }

        var result = recognition.Get((text.Region, text.Language), () =>
            ocrBridge.Value.RecognizeWithDiagnosticsAsync(text.Region, text.Language, cancellationToken).GetAwaiter().GetResult(), cancellationToken);
        return result.Success && PaddleOcrBridge.TextMatches(result.Text, text.ExpectedText, text.Contains, text.UseRegex);
    }

    public void Dispose()
    {
        frames.Dispose();
        if (ocrBridge.IsValueCreated)
        {
            ocrBridge.Value.Dispose();
        }
    }
}
