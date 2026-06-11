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
            if (remainingUs > 4_000)
            {
                Thread.Sleep(Math.Max(1, (int)(remainingUs / 1_000) - 2));
            }
            else if (remainingUs > 500)
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

    public MacroPlaybackExecutor(
        IMacroInputSink inputSink,
        IPlaybackDelayStrategy? delayStrategy = null,
        Func<PixelCondition, bool>? livePixelEvaluator = null,
        Func<string, MacroDocument?>? macroResolver = null)
    {
        this.inputSink = inputSink;
        this.delayStrategy = delayStrategy ?? new QpcPlaybackDelayStrategy();
        this.livePixelEvaluator = livePixelEvaluator ?? ScreenPixelSampler.Matches;
        this.macroResolver = macroResolver;
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

        if (!inputSink.IsAvailable)
        {
            return new PlaybackRunResult(PlaybackRunStatus.InputUnavailable, 0, 0, Cancelled: false, InputStats: null);
        }

        RuntimeNativeMethods.QueryPerformanceFrequency(out var qpcFrequency);
        var iterationsTarget = options.Mode == PlaybackMode.FixedCount ? options.Count : int.MaxValue;
        var iterationsCompleted = 0;
        var actionsSubmitted = 0;
        var sequence = 1u;

        try
        {
            using var timerResolution = TimerResolutionScope.TryBeginOneMillisecond();
            while (iterationsCompleted < iterationsTarget)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var actions = InputActionCompiler.Compile(
                    document,
                    startTick: 0,
                    qpcFrequency,
                    GetPixelEvaluator(options.PixelMode),
                    macroResolver);
                RuntimeNativeMethods.QueryPerformanceCounter(out var iterationStartTick);

                foreach (var action in actions)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    delayStrategy.WaitUntil(iterationStartTick + action.DueTick, qpcFrequency, cancellationToken, options.NoWait);
                    cancellationToken.ThrowIfCancellationRequested();
                    inputSink.Submit(sequence++, action.Action);
                    actionsSubmitted++;
                }

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
            SubmitSafeReleaseActions(ref sequence, ref actionsSubmitted);
            return new PlaybackRunResult(
                PlaybackRunStatus.Completed,
                iterationsCompleted,
                actionsSubmitted,
                Cancelled: true,
                inputSink.GetStats());
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
        private readonly uint period;
        private readonly bool active;

        private TimerResolutionScope(uint period, bool active)
        {
            this.period = period;
            this.active = active;
        }

        public static TimerResolutionScope TryBeginOneMillisecond()
        {
            if (!OperatingSystem.IsWindows())
            {
                return new TimerResolutionScope(0, active: false);
            }

            return new TimerResolutionScope(1, RuntimeNativeMethods.timeBeginPeriod(1) == 0);
        }

        public void Dispose()
        {
            if (active)
            {
                RuntimeNativeMethods.timeEndPeriod(period);
            }
        }
    }
}
