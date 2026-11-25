using System.Collections.Concurrent;
using MacroHid.Core;
using MacroHid.Runtime;

internal static class ConditionOnlyPlaybackTests
{
    public static void Run()
    {
        FixedIterationsEvaluateUnboundedConditionsOnce();
        FixedIterationWaitsWithinAnExplicitWindow();
        LoopMonitorsKeepEvaluatingIndependently();
        LoopHonorsStopAll();
        GateOnlyLoopRepeatsItsActions();
    }

    public static void ReportsActualStartupGateWait()
    {
        foreach (var nested in new[] { false, true })
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var changes = new BlockingCollection<string>();
            var sink = new InertInputSink();
            var owner = new MacroDocument(1, "gated macro", PlaybackSettings.Default, [Tap(HidKey.A)],
                [Directive(1, []) with { ExecutionMode = ConditionExecutionMode.GateMainSequence },
                 Directive(2, []) with { ExecutionMode = ConditionExecutionMode.GateMainSequence }]);
            var document = nested
                ? new MacroDocument(1, "root macro", PlaybackSettings.Default, [new MacroCallStep(owner.Name)])
                : owner;
            var allowed = 0;
            var executor = new MacroPlaybackExecutor(sink,
                livePixelEvaluator: pixel => pixel.Coordinate.X <= Volatile.Read(ref allowed),
                macroResolver: name => name == owner.Name ? owner : null);
            executor.StartupGateWaitChanged += () => changes.Add(executor.WaitingStartupGate?.Condition.Id ?? "");
            var controller = new MacroPlaybackController(document, executor, Options(PlaybackMode.FixedCount));
            try
            {
                controller.TriggerPressedAsync().GetAwaiter().GetResult();
                Assert.Equal("condition-1", changes.Take(deadline.Token));
                Assert.Equal(owner, controller.WaitingStartupGate?.Document);
                Assert.Equal(0, sink.DownCount(HidKey.A));

                Volatile.Write(ref allowed, 1);
                Assert.Equal("", changes.Take(deadline.Token));
                Assert.Equal("condition-2", changes.Take(deadline.Token));
                Assert.Equal(0, sink.DownCount(HidKey.A));

                controller.StopAsync().WaitAsync(deadline.Token).GetAwaiter().GetResult();
                Assert.True(controller.WhenIdleAsync().GetAwaiter().GetResult().Cancelled);
                Assert.Equal("", changes.Take(deadline.Token));
                Assert.True(controller.WaitingStartupGate is null);
                Assert.Equal(0, sink.DownCount(HidKey.A));

                Volatile.Write(ref allowed, 2);
                controller.TriggerPressedAsync().GetAwaiter().GetResult();
                Assert.False(controller.WhenIdleAsync().WaitAsync(deadline.Token).GetAwaiter().GetResult().Cancelled);
                Assert.Equal(1, sink.DownCount(HidKey.A));
                Assert.False(changes.TryTake(out _));
            }
            finally
            {
                controller.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            }
        }
    }

    private static void FixedIterationsEvaluateUnboundedConditionsOnce()
    {
        foreach (var kind in new[] { MacroKind.Normal, MacroKind.Condition })
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            int unmatchedPolls = 0;
            var sink = new InertInputSink();
            using var executor = new MacroPlaybackExecutor(sink, livePixelEvaluator: pixel =>
            {
                if (pixel.Coordinate.X == 0) return true;
                Interlocked.Increment(ref unmatchedPolls);
                return false;
            });
            var document = new MacroDocument(1, "fixed condition playback", PlaybackSettings.Default, [],
                [Directive(0, [Tap(HidKey.A)]), Directive(1, [Tap(HidKey.B)])], Kind: kind);
            var result = executor.RunAsync(document, Options(PlaybackMode.FixedCount, 3), stop.Token).GetAwaiter().GetResult();

            Assert.False(result.Cancelled);
            Assert.Equal(3, result.IterationsCompleted);
            Assert.Equal(3, unmatchedPolls);
            Assert.Equal(3, sink.DownCount(HidKey.A));
            Assert.Equal(0, sink.DownCount(HidKey.B));
        }
    }

    private static void FixedIterationWaitsWithinAnExplicitWindow()
    {
        int polls = 0;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var sink = new InertInputSink();
        using var executor = new MacroPlaybackExecutor(sink, livePixelEvaluator: _ => Interlocked.Increment(ref polls) >= 3);
        var document = new MacroDocument(1, "condition window", PlaybackSettings.Default, [],
            [Directive(0, [Tap(HidKey.A)]) with { WindowEnd = TimeSpan.FromSeconds(2) }]);
        var result = executor.RunAsync(document, Options(PlaybackMode.FixedCount), stop.Token).GetAwaiter().GetResult();

        Assert.False(result.Cancelled);
        Assert.Equal(1, result.IterationsCompleted);
        Assert.Equal(3, polls);
        Assert.Equal(1, sink.DownCount(HidKey.A));
    }

    private static void LoopMonitorsKeepEvaluatingIndependently()
    {
        foreach (var mode in new[] { PlaybackMode.ToggleLoop, PlaybackMode.HoldLoop })
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var firstRepeated = new ManualResetEventSlim();
            using var secondMatched = new ManualResetEventSlim();
            var sink = new InertInputSink((key, count) =>
            {
                if (key == HidKey.A && count == 2) firstRepeated.Set();
                if (key == HidKey.B) secondMatched.Set();
            });
            using var executor = new MacroPlaybackExecutor(sink, livePixelEvaluator: pixel =>
                pixel.Coordinate.X == 0 || firstRepeated.IsSet);
            var document = new MacroDocument(1, "condition loop", PlaybackSettings.Default, [],
                [Directive(0, [Tap(HidKey.A)]), Directive(1, [Tap(HidKey.B)]) with
                    { WindowStart = TimeSpan.Zero, TimeBase = ConditionTimeBase.AfterPreviousCondition }], Kind: MacroKind.Condition);
            var run = executor.RunAsync(document, Options(mode), stop.Token);
            try
            {
                Assert.True(firstRepeated.Wait(TimeSpan.FromSeconds(4)));
                Assert.True(secondMatched.Wait(TimeSpan.FromSeconds(4)));
                Assert.False(run.IsCompleted);
            }
            finally
            {
                stop.Cancel();
                Assert.True(run.GetAwaiter().GetResult().Cancelled);
            }
            Assert.True(sink.DownCount(HidKey.A) >= 2);
            Assert.True(sink.DownCount(HidKey.B) >= 1);
        }
    }

    private static void LoopHonorsStopAll()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var sink = new InertInputSink();
        using var executor = new MacroPlaybackExecutor(sink, livePixelEvaluator: _ => true);
        var document = new MacroDocument(1, "condition stop", PlaybackSettings.Default, [],
            [Directive(0, [Tap(HidKey.A), new StopAllSequencesStep()])]);
        var result = executor.RunAsync(document, Options(PlaybackMode.ToggleLoop), stop.Token).GetAwaiter().GetResult();
        Assert.True(result.Cancelled);
        Assert.False(stop.IsCancellationRequested);
        Assert.Equal(1, sink.DownCount(HidKey.A));
    }

    private static void GateOnlyLoopRepeatsItsActions()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var sink = new InertInputSink((key, count) => { if (key == HidKey.A && count == 2) stop.Cancel(); });
        using var executor = new MacroPlaybackExecutor(sink, livePixelEvaluator: _ => true);
        var document = new MacroDocument(1, "gate loop", PlaybackSettings.Default, [],
            [Directive(0, [Tap(HidKey.A)]) with { ExecutionMode = ConditionExecutionMode.GateMainSequence }]);
        var result = executor.RunAsync(document, Options(PlaybackMode.ToggleLoop), stop.Token).GetAwaiter().GetResult();
        Assert.True(result.Cancelled);
        Assert.Equal(2, sink.DownCount(HidKey.A));
    }

    private static PlaybackExecutionOptions Options(PlaybackMode mode, int count = 1)
        => new(mode, count, PixelEvaluationMode.Live, false, PrecisionMode.Balanced);

    private static ConditionalDirective Directive(int x, IReadOnlyList<MacroStep> actions)
        => new($"condition-{x}", $"condition-{x}", -1, -1,
            new PixelMatcher(ScreenRegion.FromSinglePixel(x, 0), new RgbColor(1, 2, 3), 0), actions,
            PollInterval: TimeSpan.FromMilliseconds(10));

    private static KeyStep Tap(HidKey key) => new(KeyActionKind.Tap, key, HidModifier.None, TimeSpan.Zero);

    private sealed class InertInputSink(Action<HidKey, int>? onDown = null) : IMacroInputSink
    {
        private readonly ConcurrentDictionary<HidKey, int> counts = new();
        public bool IsAvailable => true;
        public int DownCount(HidKey key) => counts.GetValueOrDefault(key);
        public void Submit(uint sequence, InputAction action)
        {
            if (action is not KeyInputAction { Kind: KeyActionKind.Down } key) return;
            var count = counts.AddOrUpdate(key.Key, 1, (_, previous) => previous + 1);
            onDown?.Invoke(key.Key, count);
        }
        public InputSubmissionStats? GetStats() => null;
    }
}
