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
    private readonly Func<string, MacroDocument?>? macroResolver;
    private readonly PlaybackPauseCoordinator? pauseCoordinator;

    public ManagedMacroControlFlowRunner(
        IMacroInputSink inputSink,
        IPlaybackDelayStrategy delayStrategy,
        IHighResolutionClock clock,
        Func<PixelCondition, bool>? pixelEvaluator,
        Func<string, MacroDocument?>? macroResolver,
        PlaybackPauseCoordinator? pauseCoordinator = null)
    {
        this.inputSink = inputSink;
        this.delayStrategy = delayStrategy;
        this.clock = clock;
        this.pixelEvaluator = pixelEvaluator;
        this.macroResolver = macroResolver;
        this.pauseCoordinator = pauseCoordinator;
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
        var pressGaps = new PressReleaseGapTracker(qpcFrequency);
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
                    Submit(new MouseMoveInputAction(move.Mode, move.X, move.Y, move.Buttons));
                    elapsedTicks += ToTicks(move.Duration, qpcFrequency);
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
            inputSink.Submit(nextSequence++, action);
            submittedActions++;
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
                || step is PixelWhenStep pixel && ContainsControlInstruction(pixel.ThenSteps))
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
                case StopCurrentSequenceStep or StopCurrentIterationStep or StopAllSequencesStep or WindowActivateStep or OcrExtractTextStep or OcrClickStep:
                    return true;
                case TextStep text when !string.IsNullOrEmpty(text.Text):
                    return true;
                case RepeatStep repeat when InspectSteps(repeat.Steps, macroResolver, visitingDocuments, visitingNames, depth):
                    return true;
                case PixelWhenStep pixel when InspectSteps(pixel.ThenSteps, macroResolver, visitingDocuments, visitingNames, depth):
                    return true;
                case MacroCallStep macro when macroResolver is not null:
                {
                    var target = macroResolver(macro.Macro);
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
