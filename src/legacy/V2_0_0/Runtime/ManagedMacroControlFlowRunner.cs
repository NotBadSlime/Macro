using MacroHid.Core;

namespace MacroHid.Runtime;

internal enum MacroControlFlowResult
{
    Completed,
    StopCurrent,
    StopIteration,
    StopAll
}

internal sealed class ManagedMacroControlFlowRunner
{
    private readonly IMacroInputSink inputSink;
    private readonly IPlaybackDelayStrategy delayStrategy;
    private readonly IHighResolutionClock clock;
    private readonly Func<PixelCondition, bool>? pixelEvaluator;
    private readonly Func<TextWhenStep, CancellationToken, bool>? textEvaluator;
    private readonly Func<string, MacroDocument?>? macroResolver;
    private readonly PlaybackPauseCoordinator? pauseCoordinator;
    private readonly bool pollConditions;
    private readonly object inputGate;
    private readonly int parallelDepth;
    private readonly InputPressFloor pressFloor;
    private static readonly AsyncLocal<int> ConditionalCallDepth = new();
    private readonly PrecisionMode precision;

    public ManagedMacroControlFlowRunner(
        IMacroInputSink inputSink,
        IPlaybackDelayStrategy delayStrategy,
        IHighResolutionClock clock,
        Func<PixelCondition, bool>? pixelEvaluator,
        Func<string, MacroDocument?>? macroResolver,
        PlaybackPauseCoordinator? pauseCoordinator = null,
        Func<TextWhenStep, CancellationToken, bool>? textEvaluator = null,
        bool pollConditions = false,
        object? inputGate = null,
        int parallelDepth = 0,
        InputPressFloor? pressFloor = null,
        PrecisionMode precision = PrecisionMode.Balanced)
    {
        this.inputSink = inputSink;
        this.delayStrategy = delayStrategy;
        this.clock = clock;
        this.pixelEvaluator = pixelEvaluator;
        this.textEvaluator = textEvaluator;
        this.macroResolver = macroResolver;
        this.pauseCoordinator = pauseCoordinator;
        this.pollConditions = pollConditions;
        this.inputGate = inputGate ?? new object();
        this.parallelDepth = parallelDepth;
        this.pressFloor = pressFloor ?? InputPressFloor.Factory;
        this.precision = precision;
    }

    public MacroControlFlowResult Run(
        MacroDocument document,
        long startTick,
        long qpcFrequency,
        CancellationToken cancellationToken,
        bool noWait,
        ref uint sequence,
        ref int actionsSubmitted)
    {
        var frames = new List<ExecutionFrame> { ExecutionFrame.ForMacro(document) };
        var elapsedTicks = 0L;
        var nextSequence = sequence;
        var submittedActions = actionsSubmitted;
        var pressGaps = new PressReleaseGapTracker(qpcFrequency, pressFloor);
        PaddleOcrBridge? ocrBridge = null;

        try
        {
            while (frames.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var frame = frames[^1];
                if (frame.Index >= frame.Steps.Count)
                {
                    if (frame.RepetitionsRemaining > 1)
                    {
                        frame.RepetitionsRemaining--;
                        frame.Index = 0;
                        continue;
                    }

                    frames.RemoveAt(frames.Count - 1);
                    continue;
                }

                var step = frame.Steps[frame.Index++];
                switch (step)
                {
                case StopCurrentSequenceStep:
                    if (StopCurrentMacro(frames))
                    {
                        return MacroControlFlowResult.StopCurrent;
                    }
                    break;

                case StopCurrentIterationStep:
                    return MacroControlFlowResult.StopIteration;

                case StopAllSequencesStep:
                    return MacroControlFlowResult.StopAll;

                case WaitStep wait:
                    elapsedTicks += ToTicks(wait.Sample(Random.Shared), qpcFrequency);
                    break;

                case RepeatStep repeat when repeat.Count > 0 && repeat.Steps.Count > 0:
                    frames.Add(ExecutionFrame.ForBlock(repeat.Steps, repeat.Count));
                    break;

                case MacroCallStep macro:
                    if (macroResolver is null)
                    {
                        throw new InvalidOperationException($"Macro call '{macro.Macro}' cannot be resolved without a macro resolver.");
                    }

                    var target = macroResolver(macro.Macro)
                        ?? throw new InvalidOperationException($"Macro call target '{macro.Macro}' was not found.");

                    if (target.EffectiveConditions.Count > 0 && !ConditionalMacroCallScope.Contains(target))
                    {
                        WaitToCurrentTick();
                        var nested = RunConditionalMacro(target, qpcFrequency, cancellationToken, noWait, ref nextSequence, ref submittedActions);
                        elapsedTicks = Math.Max(elapsedTicks, clock.GetTimestamp() - startTick);
                        if (nested is MacroControlFlowResult.StopAll or MacroControlFlowResult.StopIteration) return nested;
                        break;
                    }

                    // Tail calls, including direct self-calls, reuse the current frame so an intentional
                    // recursive loop remains cancellable without growing the managed call stack.
                    if (frame.IsMacroBoundary && frame.Index >= frame.Steps.Count && frame.RepetitionsRemaining == 1)
                    {
                        frame.ResetForMacro(target);
                    }
                    else
                    {
                        frames.Add(ExecutionFrame.ForMacro(target));
                        if (frames.Count(static candidate => candidate.IsMacroBoundary) > 1024)
                        {
                            throw new InvalidOperationException("Macro call nesting exceeded the safety limit. Use a tail self-call or a loop for unbounded repetition.");
                        }
                    }
                    break;

                case PixelWhenStep pixel:
                    if (pollConditions && !noWait)
                    {
                        if (WaitUntilPixelMatches(pixel, cancellationToken, qpcFrequency) && pixel.ThenSteps.Count > 0)
                        {
                            frames.Add(ExecutionFrame.ForBlock(pixel.ThenSteps, 1));
                        }
                    }
                    else
                    {
                        if (pixel.WindowStart is { } windowStart)
                        {
                            elapsedTicks = Math.Max(elapsedTicks, ToTicks(windowStart, qpcFrequency));
                        }

                        if (pixelEvaluator?.Invoke(pixel.Condition) == true)
                        {
                            if (pixel.ThenSteps.Count > 0)
                            {
                                frames.Add(ExecutionFrame.ForBlock(pixel.ThenSteps, 1));
                            }
                        }
                        else if (pixel.WindowEnd is { } windowEnd)
                        {
                            elapsedTicks = Math.Max(elapsedTicks, ToTicks(windowEnd, qpcFrequency));
                        }
                    }
                    break;

                case TextWhenStep textWhen:
                    if (TextMatches(textWhen, cancellationToken, qpcFrequency, noWait) && textWhen.ThenSteps.Count > 0)
                    {
                        frames.Add(ExecutionFrame.ForBlock(textWhen.ThenSteps, 1));
                    }
                    break;

                case WeakNetStep weakNet:
                    WaitToCurrentTick();
                    ExecuteWeakNet(weakNet, cancellationToken, qpcFrequency, noWait);
                    elapsedTicks = Math.Max(elapsedTicks, Math.Max(0, clock.GetTimestamp() - startTick));
                    break;

                case NetLimitStep netLimit:
                    WaitToCurrentTick();
                    ExecuteNetLimit(netLimit);
                    elapsedTicks = Math.Max(elapsedTicks, Math.Max(0, clock.GetTimestamp() - startTick));
                    break;

                case ClockShiftStep clockShift:
                    WaitToCurrentTick();
                    ExecuteClockShift(clockShift);
                    elapsedTicks = Math.Max(elapsedTicks, Math.Max(0, clock.GetTimestamp() - startTick));
                    break;

                case FrameLockStep frameLock:
                    WaitToCurrentTick();
                    ExecuteFrameLock(frameLock);
                    elapsedTicks = Math.Max(elapsedTicks, Math.Max(0, clock.GetTimestamp() - startTick));
                    break;

                case YsrFpsToggleStep ysr:
                    WaitToCurrentTick();
                    if (!YsrFpsSession.TryApply(ysr, out var ysrError)) throw new InvalidOperationException(ysrError);
                    elapsedTicks = Math.Max(elapsedTicks, Math.Max(0, clock.GetTimestamp() - startTick));
                    break;

                case ParallelStep parallel:
                    WaitToCurrentTick();
                    var parallelOutcome = ExecuteParallel(parallel, qpcFrequency, cancellationToken, noWait);
                    submittedActions += parallelOutcome.Actions;
                    elapsedTicks = Math.Max(elapsedTicks, Math.Max(0, clock.GetTimestamp() - startTick));
                    if (parallelOutcome.Result is MacroControlFlowResult.StopAll or MacroControlFlowResult.StopIteration)
                    {
                        return parallelOutcome.Result;
                    }
                    break;

                case ParallelBranchStep:
                    break;

                case KeyStep key:
                    if (key.Kind == KeyActionKind.Tap)
                    {
                        Submit(new KeyInputAction(KeyActionKind.Down, key.Key, key.Modifiers));
                        elapsedTicks += ToTicks(key.Hold, qpcFrequency);
                        Submit(new KeyInputAction(KeyActionKind.Up, key.Key, key.Modifiers));
                    }
                    else
                    {
                        Submit(new KeyInputAction(key.Kind, key.Key, key.Modifiers));
                        elapsedTicks += ToTicks(key.Hold, qpcFrequency);
                    }
                    break;

                case CommentStep:
                    break;

                case TextStep text:
                    Submit(new TextInputAction(text.Text));
                    break;

                case MouseButtonStep button:
                    if (button.HasCoordinate)
                    {
                        Submit(new MouseMoveInputAction(button.CoordinateMode ?? MouseMoveMode.Absolute, button.X!.Value, button.Y!.Value));
                    }

                    if (button.Kind == ButtonActionKind.Click)
                    {
                        Submit(new MouseButtonInputAction(button.Button, ButtonActionKind.Down));
                        elapsedTicks += ToTicks(button.Hold, qpcFrequency);
                        Submit(new MouseButtonInputAction(button.Button, ButtonActionKind.Up));
                    }
                    else
                    {
                        Submit(new MouseButtonInputAction(button.Button, button.Kind));
                        elapsedTicks += ToTicks(button.Hold, qpcFrequency);
                    }
                    break;

                case MouseMoveStep move:
                    var moveOrigin = elapsedTicks;
                    foreach (var slice in MouseMoveSmoothing.Split(move.Mode, move.X, move.Y, move.Duration))
                    {
                        elapsedTicks = moveOrigin + ToTicks(slice.Offset, qpcFrequency);
                        Submit(new MouseMoveInputAction(slice.Mode, slice.X, slice.Y, move.Buttons));
                    }

                    var moveEnd = moveOrigin + ToTicks(move.Duration, qpcFrequency);
                    if (elapsedTicks < moveEnd)
                    {
                        elapsedTicks = moveEnd;
                    }

                    break;

                case MouseWheelStep wheel:
                    Submit(new MouseWheelInputAction(wheel.Vertical, wheel.Horizontal, wheel.Buttons));
                    break;

                case WindowActivateStep windowActivate:
                    WaitToCurrentTick();
                    var activation = WindowActivationService.Activate(windowActivate, cancellationToken);
                    elapsedTicks = Math.Max(elapsedTicks, Math.Max(0, clock.GetTimestamp() - startTick));
                    if (!activation.Success && windowActivate.FailIfNotFound)
                    {
                        throw new InvalidOperationException(
                            $"Could not activate window '{windowActivate.ProcessName}': {activation.Error}");
                    }
                    break;

                case OcrExtractTextStep ocrExtractText:
                    WaitToCurrentTick();
                    ocrBridge ??= new PaddleOcrBridge();
                    var recognition = ocrBridge.RecognizeWithDiagnosticsAsync(
                            ocrExtractText.Region,
                            ocrExtractText.Language,
                            cancellationToken)
                        .GetAwaiter()
                        .GetResult();
                    elapsedTicks = Math.Max(elapsedTicks, Math.Max(0, clock.GetTimestamp() - startTick));
                    if (!recognition.Success)
                    {
                        if (ocrExtractText.FailIfNotFound)
                        {
                            throw new InvalidOperationException(
                                $"OCR text extraction failed: {recognition.Error ?? "no text was recognized"}");
                        }

                        break;
                    }

                    if (!PaddleOcrBridge.TryExtractText(
                            recognition.Text,
                            ocrExtractText.Pattern,
                            ocrExtractText.UseRegex,
                            ocrExtractText.MatchIndex,
                            ocrExtractText.CaptureGroup,
                            ocrExtractText.FilterTerms,
                            ocrExtractText.KeepDigitsOnly,
                            ocrExtractText.NormalizeWhitespace,
                            out var extractedText,
                            out var extractionError))
                    {
                        if (ocrExtractText.FailIfNotFound)
                        {
                            throw new InvalidOperationException(
                                $"OCR text extraction failed: {extractionError ?? "the requested text was not found"}");
                        }

                        break;
                    }

                    if (!WindowsClipboardService.TrySetText(extractedText, cancellationToken, out var clipboardError)
                        && ocrExtractText.FailIfNotFound)
                    {
                        throw new InvalidOperationException(
                            $"OCR text extraction could not update the clipboard: {clipboardError}");
                    }
                    break;

                case OcrClickStep ocrClick:
                    WaitToCurrentTick();
                    ocrBridge ??= new PaddleOcrBridge();
                    var location = ocrBridge.FindTextAsync(
                            ocrClick.Region,
                            ocrClick.ExpectedText,
                            ocrClick.Contains,
                            ocrClick.Language,
                            ocrClick.UseRegex,
                            ocrClick.MatchIndex,
                            ocrClick.OffsetX,
                            ocrClick.OffsetY,
                            cancellationToken)
                        .GetAwaiter()
                        .GetResult();
                    elapsedTicks = Math.Max(elapsedTicks, Math.Max(0, clock.GetTimestamp() - startTick));
                    if (location is null)
                    {
                        break;
                    }

                    Submit(new MouseMoveInputAction(MouseMoveMode.Absolute, location.X, location.Y));
                    var clickCount = Math.Clamp(ocrClick.ClickCount, 1, 3);
                    for (var clickIndex = 0; clickIndex < clickCount; clickIndex++)
                    {
                        Submit(new MouseButtonInputAction(ocrClick.Button, ButtonActionKind.Down));
                        elapsedTicks += ToTicks(ocrClick.Hold, qpcFrequency);
                        Submit(new MouseButtonInputAction(ocrClick.Button, ButtonActionKind.Up));
                        if (clickIndex + 1 < clickCount)
                        {
                            elapsedTicks += ToTicks(ocrClick.Interval, qpcFrequency);
                        }
                    }
                    break;

                case ConsumerStep consumer:
                    if (consumer.Kind == ButtonActionKind.Click)
                    {
                        Submit(new ConsumerInputAction(consumer.Control, ButtonActionKind.Down));
                        elapsedTicks += ToTicks(consumer.Hold, qpcFrequency);
                        Submit(new ConsumerInputAction(consumer.Control, ButtonActionKind.Up));
                    }
                    else
                    {
                        Submit(new ConsumerInputAction(consumer.Control, consumer.Kind));
                        elapsedTicks += ToTicks(consumer.Hold, qpcFrequency);
                    }
                    break;
                }
            }

            // A trailing delay is part of the sequence, including effect-only macros.
            WaitToCurrentTick();
            return MacroControlFlowResult.Completed;
        }
        finally
        {
            ocrBridge?.Dispose();
            sequence = nextSequence;
            actionsSubmitted = submittedActions;
        }

        void Submit(InputAction action)
        {
            var dueTick = pressGaps.AdjustDueTick(action, startTick + elapsedTicks);
            elapsedTicks = Math.Max(elapsedTicks, dueTick - startTick);
            if (pauseCoordinator is null)
            {
                delayStrategy.WaitUntil(dueTick, qpcFrequency, cancellationToken, noWait);
            }
            else
            {
                pauseCoordinator.WaitUntil(dueTick, delayStrategy, qpcFrequency, cancellationToken, noWait);
            }
            cancellationToken.ThrowIfCancellationRequested();
            lock (inputGate)
            {
                inputSink.Submit(nextSequence++, action);
                submittedActions++;
            }
        }

        void WaitToCurrentTick()
        {
            var dueTick = startTick + elapsedTicks;
            if (pauseCoordinator is null)
            {
                delayStrategy.WaitUntil(dueTick, qpcFrequency, cancellationToken, noWait);
            }
            else
            {
                pauseCoordinator.WaitUntil(dueTick, delayStrategy, qpcFrequency, cancellationToken, noWait);
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private bool WaitUntilPixelMatches(PixelWhenStep pixel, CancellationToken cancellationToken, long qpcFrequency)
    {
        var intervalTicks = ToTicks(pixel.EffectivePollInterval, qpcFrequency);
        while (!cancellationToken.IsCancellationRequested)
        {
            if (pixelEvaluator?.Invoke(pixel.Condition) == true)
            {
                return true;
            }

            var next = clock.GetTimestamp() + Math.Max(1, intervalTicks);
            delayStrategy.WaitUntil(next, qpcFrequency, cancellationToken, noWait: false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return false;
    }

    private bool TextMatches(TextWhenStep step, CancellationToken cancellationToken, long qpcFrequency, bool noWait)
    {
        if (!(pollConditions && !noWait))
        {
            return textEvaluator?.Invoke(step, cancellationToken) == true;
        }

        var intervalTicks = ToTicks(step.EffectivePollInterval, qpcFrequency);
        while (!cancellationToken.IsCancellationRequested)
        {
            if (textEvaluator?.Invoke(step, cancellationToken) == true)
            {
                return true;
            }

            var next = clock.GetTimestamp() + Math.Max(1, intervalTicks);
            delayStrategy.WaitUntil(next, qpcFrequency, cancellationToken, noWait: false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return false;
    }

    private void ExecuteWeakNet(WeakNetStep step, CancellationToken cancellationToken, long qpcFrequency, bool noWait)
    {
        if (step.Action == WeakNetAction.Stop)
        {
            WeakNetSession.Stop();
            return;
        }

        if (!WeakNetSession.TryStart(step.ProcessName, step.EffectiveTimeShift, out var error))
        {
            throw new InvalidOperationException(error ?? "无法开启断网。");
        }

        if (step.Action != WeakNetAction.Hold)
        {
            return;
        }

        try
        {
            var due = clock.GetTimestamp() + ToTicks(step.EffectiveDuration, qpcFrequency);
            delayStrategy.WaitUntil(due, qpcFrequency, cancellationToken, noWait);
        }
        finally
        {
            WeakNetSession.Stop();
        }
    }

    private static void ExecuteNetLimit(NetLimitStep step)
    {
        if (step.Action == NetLimitAction.Stop)
        {
            NetLimitSession.Stop(step.Engine);
            return;
        }

        if (!NetLimitSession.TryStart(step.ProcessName, step.EffectiveBytesPerSecond, step.Engine, out var error))
        {
            throw new InvalidOperationException(error ?? "无法开启弱网限速。");
        }
    }

    private static void ExecuteClockShift(ClockShiftStep step)
    {
        if (!PlaybackEffectScope.TryAcquire("network", out var ownershipError))
            throw new InvalidOperationException(ownershipError);
        if (step.Action == ClockShiftAction.Restore)
        {
            SystemTimeShift.Restore();
            return;
        }

        try
        {
            SystemTimeShift.ShiftForwardOnce(step.EffectiveTimeShift);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(ex.Message);
        }
    }

    private static void ExecuteFrameLock(FrameLockStep step)
    {
        var ok = step.Action == FrameLockAction.Stop
            ? FrameLockSession.TryStop(step.ProcessName, out var error)
            : FrameLockSession.TryStart(step.ProcessName, step.EffectiveFps, out error);
        if (!ok)
        {
            throw new InvalidOperationException(error ?? "无法调整锁帧。");
        }
    }

    private MacroControlFlowResult RunConditionalMacro(MacroDocument target, long frequency,
        CancellationToken token, bool noWait, ref uint sequence, ref int submitted)
    {
        if (ConditionalCallDepth.Value >= 64) throw new InvalidOperationException("条件宏嵌套超过 64 层安全上限。");
        using var callScope = new ConditionalMacroCallScope(target);
        ConditionalCallDepth.Value++;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var evaluator = new CompositeConditionEvaluator(pixelEvaluator: pixelEvaluator,
            textEvaluator: textEvaluator is null ? null : matcher => textEvaluator(
                new TextWhenStep(matcher.Region, matcher.ExpectedText, [], matcher.Contains, matcher.Language, matcher.UseRegex), cancellation.Token));
        var monitors = new List<ConditionMonitor>();
        var outcome = 0;
        var started = clock.GetTimestamp();
        var timeline = new ConditionTimeline(target.EffectiveConditions.Count, started, started);
        var localPause = new PlaybackPauseCoordinator(clock);
        void Stop(int value)
        {
            int old;
            do { old = Volatile.Read(ref outcome); if (old >= value) break; }
            while (Interlocked.CompareExchange(ref outcome, value, old) != old);
            cancellation.Cancel();
        }
        ConditionMonitor Create(ConditionalDirective condition, int index, IReadOnlyList<ConditionTimeInterval> intervals)
            => new(condition, evaluator, inputSink, macroResolver, started, frequency,
                precision, () => Stop(3), () => Stop(2), localPause, timeline, index, intervals, pressFloor,
                stopCurrentRequested: () => Stop(1));
        try
        {
            // Gates belong to this call, not to the parent macro's time origin.
            for (var i = 0; i < target.EffectiveConditions.Count; i++)
            {
                var condition = target.EffectiveConditions[i];
                if (condition.ExecutionMode != ConditionExecutionMode.GateMainSequence) continue;
                var gate = Create(condition, i, ConditionActivationSchedule.ResolveStartupGate());
                monitors.Add(gate);
                gate.Activate();
                gate.WaitForTriggeredCompletion(cancellation.Token);
            }
            var mainStart = clock.GetTimestamp();
            timeline.SetMainIterationTick(mainStart);
            var windows = new MacroPlaybackExecutor(inputSink, macroResolver: macroResolver)
                .CreateConditionTimeWindows(target, frequency);
            for (var i = 0; i < target.EffectiveConditions.Count; i++)
            {
                var condition = target.EffectiveConditions[i];
                if (condition.ExecutionMode == ConditionExecutionMode.GateMainSequence) continue;
                var intervals = ConditionActivationSchedule.Resolve(condition, windows, frequency);
                if (intervals.Count == 0 && !condition.HasActivationConstraint)
                    intervals = ConditionActivationSchedule.ResolveStartupGate();
                if (intervals.Count == 0) continue;
                var monitor = Create(condition, i, intervals);
                monitors.Add(monitor);
                monitor.Activate();
            }
            var runner = new ManagedMacroControlFlowRunner(inputSink, delayStrategy, clock, pixelEvaluator,
                macroResolver, localPause, textEvaluator, true, inputGate, parallelDepth, pressFloor, precision);
            var flow = runner.Run(target, mainStart, frequency, cancellation.Token, noWait, ref sequence, ref submitted);
            if (!target.IsConditionMacro && target.Steps.Count > 0)
                foreach (var monitor in monitors) monitor.CompleteAfterCurrentEvaluation();
            foreach (var monitor in monitors) monitor.WaitForTriggeredCompletion(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            return flow;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && Volatile.Read(ref outcome) != 0)
        {
            return outcome switch { 3 => MacroControlFlowResult.StopAll, 2 => MacroControlFlowResult.StopIteration, _ => MacroControlFlowResult.StopCurrent };
        }
        finally
        {
            foreach (var monitor in monitors) monitor.Deactivate();
            var errors = new List<Exception>();
            foreach (var monitor in monitors) try { monitor.Dispose(); } catch (Exception ex) { errors.Add(ex); }
            ConditionalCallDepth.Value--;
            if (errors.Count > 0) throw new AggregateException("子宏条件执行失败", errors);
        }
    }

    private (MacroControlFlowResult Result, int Actions) ExecuteParallel(
        ParallelStep parallel,
        long qpcFrequency,
        CancellationToken cancellationToken,
        bool noWait)
    {
        if (parallelDepth >= 8)
        {
            throw new InvalidOperationException("Parallel nesting exceeded the safety limit.");
        }

        using var branchCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var workers = ParallelWorkerLease.Acquire(2);
        long commonStart = 0;
        using var startBarrier = new Barrier(3, _ => commonStart = clock.GetTimestamp());
        if (!noWait && parallel.Timeout is { } timeout && timeout > TimeSpan.Zero)
        {
            branchCancellation.CancelAfter(timeout);
        }

        var branches = new[] { parallel.Left.Steps, parallel.Right.Steps };
        var results = new[] { MacroControlFlowResult.Completed, MacroControlFlowResult.Completed };
        var counts = new int[2];
        var errors = new Exception?[2];
        var tasks = new Task[2];
        for (var index = 0; index < branches.Length; index++)
        {
            var branchIndex = index;
            var branchSteps = branches[index];
            tasks[index] = PlaybackWorkers.Parallel.Run(() =>
            {
                try
                {
                    using var precisionScope = PrecisionPlaybackContext.Enter(precision);
                    var branchRunner = new ManagedMacroControlFlowRunner(
                        inputSink,
                        delayStrategy,
                        clock,
                        pixelEvaluator,
                        macroResolver,
                        pauseCoordinator,
                        textEvaluator,
                        pollConditions: true,
                        inputGate,
                        parallelDepth + 1,
                        pressFloor, precision);
                    uint sequence = 1;
                    var submitted = 0;
                    var document = new MacroDocument(1, "_parallel_branch", PlaybackSettings.Default, branchSteps);
                    startBarrier.SignalAndWait(branchCancellation.Token);
                    var flow = branchRunner.Run(
                        document,
                        commonStart,
                        qpcFrequency,
                        branchCancellation.Token,
                        noWait,
                        ref sequence,
                        ref submitted);
                    counts[branchIndex] = submitted;
                    results[branchIndex] = flow;
                    if (flow is MacroControlFlowResult.StopAll or MacroControlFlowResult.StopIteration)
                    {
                        branchCancellation.Cancel();
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    errors[branchIndex] = ex;
                    branchCancellation.Cancel();
                }
                return true;
            });
        }

        try { startBarrier.SignalAndWait(branchCancellation.Token); }
        catch (OperationCanceledException) { branchCancellation.Cancel(); }
        try
        {
            Task.WaitAll(tasks);
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(error => error is OperationCanceledException or TaskCanceledException))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }

        var failure = errors.FirstOrDefault(error => error is not null);
        if (failure is not null)
        {
            if (failure is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (failure is not OperationCanceledException)
            {
                throw failure;
            }
        }

        if (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        if (results.Any(result => result == MacroControlFlowResult.StopAll))
        {
            return (MacroControlFlowResult.StopAll, counts.Sum());
        }

        if (results.Any(result => result == MacroControlFlowResult.StopIteration))
        {
            return (MacroControlFlowResult.StopIteration, counts.Sum());
        }

        return (MacroControlFlowResult.Completed, counts.Sum());
    }

    private static bool StopCurrentMacro(List<ExecutionFrame> frames)
    {
        while (frames.Count > 0)
        {
            var frame = frames[^1];
            frames.RemoveAt(frames.Count - 1);
            if (frame.IsMacroBoundary)
            {
                return frames.Count == 0;
            }
        }

        return true;
    }

    private static long ToTicks(TimeSpan duration, long qpcFrequency)
    {
        return Math.Max(0, (long)Math.Round(duration.TotalSeconds * qpcFrequency, MidpointRounding.AwayFromZero));
    }

    private sealed class ExecutionFrame
    {
        private ExecutionFrame(IReadOnlyList<MacroStep> steps, int repetitions, bool isMacroBoundary)
        {
            Steps = steps;
            RepetitionsRemaining = repetitions;
            IsMacroBoundary = isMacroBoundary;
        }

        public IReadOnlyList<MacroStep> Steps { get; private set; }
        public int Index { get; set; }
        public int RepetitionsRemaining { get; set; }
        public bool IsMacroBoundary { get; }

        public static ExecutionFrame ForMacro(MacroDocument document) => new(document.Steps, 1, true);
        public static ExecutionFrame ForBlock(IReadOnlyList<MacroStep> steps, int repetitions) => new(steps, repetitions, false);

        public void ResetForMacro(MacroDocument document)
        {
            Steps = document.Steps;
            Index = 0;
            RepetitionsRemaining = 1;
        }
    }
}

internal sealed class PlaybackPauseCoordinator : IDisposable
{
    private readonly object gate = new();
    private readonly ManualResetEventSlim resumed = new(initialState: true);
    private readonly IHighResolutionClock clock;
    private readonly NativePlaybackRunControl? nativeControl;
    private int pauseCount;
    private long pauseStartedTick;
    private long completedPauseTicks;

    public PlaybackPauseCoordinator(IHighResolutionClock clock, NativePlaybackRunControl? nativeControl = null)
    {
        this.clock = clock;
        this.nativeControl = nativeControl;
    }

    public IDisposable Pause()
    {
        lock (gate)
        {
            if (pauseCount++ == 0)
            {
                pauseStartedTick = clock.GetTimestamp();
                resumed.Reset();
                nativeControl?.Pause();
            }
        }

        return new PauseLease(this);
    }

    public void WaitUntil(
        long unpausedDueTick,
        IPlaybackDelayStrategy delayStrategy,
        long qpcFrequency,
        CancellationToken cancellationToken,
        bool noWait)
    {
        if (noWait)
        {
            return;
        }

        while (true)
        {
            resumed.Wait(cancellationToken);
            var pauseTicks = GetCompletedPauseTicks();
            delayStrategy.WaitUntil(unpausedDueTick + pauseTicks, qpcFrequency, cancellationToken, noWait: false);
            cancellationToken.ThrowIfCancellationRequested();
            if (resumed.IsSet && pauseTicks == GetCompletedPauseTicks())
            {
                return;
            }
        }
    }

    public void WaitUntilResumed(CancellationToken cancellationToken)
    {
        resumed.Wait(cancellationToken);
    }

    public long GetTimelineElapsedTicks(long timelineStartTick, long nowTick)
    {
        lock (gate)
        {
            var pausedTicks = completedPauseTicks;
            if (pauseCount > 0 && pauseStartedTick > 0)
            {
                pausedTicks += Math.Max(0, nowTick - pauseStartedTick);
            }

            return Math.Max(0, nowTick - timelineStartTick - pausedTicks);
        }
    }

    private long GetCompletedPauseTicks()
    {
        lock (gate)
        {
            return completedPauseTicks;
        }
    }

    private void Resume()
    {
        lock (gate)
        {
            if (pauseCount <= 0 || --pauseCount > 0)
            {
                return;
            }

            completedPauseTicks += Math.Max(0, clock.GetTimestamp() - pauseStartedTick);
            pauseStartedTick = 0;
            nativeControl?.Resume();
            resumed.Set();
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (pauseCount > 0)
            {
                pauseCount = 0;
                nativeControl?.Resume();
            }
        }
        resumed.Set();
        resumed.Dispose();
    }

    private sealed class PauseLease : IDisposable
    {
        private PlaybackPauseCoordinator? owner;

        public PauseLease(PlaybackPauseCoordinator owner)
        {
            this.owner = owner;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref owner, null)?.Resume();
        }
    }
}

internal static class MacroControlFlowInspector
{
    public static bool RequiresManagedExecution(
        MacroDocument document,
        Func<string, MacroDocument?>? macroResolver)
    {
        var visitingDocuments = new HashSet<MacroDocument>(ReferenceEqualityComparer.Instance) { document };
        var visitingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { document.Name };
        return InspectSteps(document.Steps, macroResolver, visitingDocuments, visitingNames, depth: 0);
    }

    public static bool ContainsControlInstruction(IReadOnlyList<MacroStep> steps)
    {
        foreach (var step in steps)
        {
            if (step is StopCurrentSequenceStep or StopCurrentIterationStep or StopAllSequencesStep)
            {
                return true;
            }

            if (step is RepeatStep repeat && ContainsControlInstruction(repeat.Steps)
                || step is PixelWhenStep pixel && ContainsControlInstruction(pixel.ThenSteps)
                || step is TextWhenStep text && ContainsControlInstruction(text.ThenSteps)
                || step is ParallelStep parallel && (ContainsControlInstruction(parallel.Left.Steps) || ContainsControlInstruction(parallel.Right.Steps))
                || step is ParallelBranchStep branch && ContainsControlInstruction(branch.Steps))
            {
                return true;
            }
        }

        return false;
    }

    private static bool InspectSteps(
        IReadOnlyList<MacroStep> steps,
        Func<string, MacroDocument?>? macroResolver,
        HashSet<MacroDocument> visitingDocuments,
        HashSet<string> visitingNames,
        int depth)
    {
        if (depth > 16)
        {
            return true;
        }

        foreach (var step in steps)
        {
            switch (step)
            {
                case StopCurrentSequenceStep or StopCurrentIterationStep or StopAllSequencesStep or WindowActivateStep or OcrExtractTextStep or OcrClickStep or WeakNetStep or NetLimitStep or ClockShiftStep or ParallelStep or TextWhenStep or FrameLockStep or YsrFpsToggleStep:
                    return true;
                case TextStep text when !string.IsNullOrEmpty(text.Text):
                    return true;
                case RepeatStep repeat when InspectSteps(repeat.Steps, macroResolver, visitingDocuments, visitingNames, depth):
                    return true;
                case PixelWhenStep pixel when InspectSteps(pixel.ThenSteps, macroResolver, visitingDocuments, visitingNames, depth):
                    return true;
                case ParallelBranchStep branch when InspectSteps(branch.Steps, macroResolver, visitingDocuments, visitingNames, depth):
                    return true;
                case MacroCallStep macro when macroResolver is not null:
                {
                    var target = macroResolver(macro.Macro);
                    if (target?.EffectiveConditions.Count > 0) return true;
                    if (target is null)
                    {
                        break;
                    }

                    if (visitingDocuments.Contains(target)
                        || visitingNames.Contains(target.Name)
                        || visitingNames.Contains(macro.Macro))
                    {
                        return true;
                    }

                    visitingDocuments.Add(target);
                    visitingNames.Add(target.Name);
                    visitingNames.Add(macro.Macro);
                    var requiresManaged = InspectSteps(target.Steps, macroResolver, visitingDocuments, visitingNames, depth + 1);
                    visitingDocuments.Remove(target);
                    visitingNames.Remove(target.Name);
                    visitingNames.Remove(macro.Macro);
                    if (requiresManaged)
                    {
                        return true;
                    }
                    break;
                }
            }
        }

        return false;
    }
}
