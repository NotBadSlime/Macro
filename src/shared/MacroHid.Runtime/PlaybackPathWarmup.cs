using System.Runtime.InteropServices;
using MacroHid.Core;

namespace MacroHid.Runtime;

/// <summary>
/// Exercises the hotkey-to-first-input path once at startup so the first real press does not pay for JIT and
/// P/Invoke stub generation (measured 33-36ms on the calling thread in a cold process; steady state is ~10us).
/// <para>Phase 1 uses an inert sink and never calls <see cref="MacroPlaybackExecutor.Prepare"/>.</para>
/// <para>Phase 2 (when a native profile is selected) runs the real native branch — Prepare, TryRunPrepared,
/// SubmitAndWait — with a wait-only macro: the native plan has no batches, so nothing is injected. It prepares with
/// the user's own profile and core mask, which the startup warm-up already did, so the engine is not re-targeted.
/// Without it the first real hotkey paid ~2ms of JIT between PlanReady and NativeQueued.</para>
/// Status reporting is suppressed for the duration.
/// </summary>
public static class PlaybackPathWarmup
{
    private static int queued;

    public static void QueueRun() => QueueRun(null, "", PrecisionGeneration.Latest);

    public static void QueueRun(PrecisionMode? nativePrecision, string? affinityMask, PrecisionGeneration generation)
    {
        if (Interlocked.Exchange(ref queued, 1) != 0) return;
        try
        {
            new Thread(() =>
            {
                Run();
                if (nativePrecision is { } precision) RunNative(precision, affinityMask, generation);
            }) { IsBackground = true, Name = "MacroHID-PathWarmup", Priority = ThreadPriority.BelowNormal }.Start();
        }
        catch { }
    }

    /// <returns>The number of rounds that completed.</returns>
    public static int Run(int rounds = 2)
    {
        var completed = 0;
        try
        {
            using var quiet = PlaybackRuntimeStatus.Suppress();
            // Generates the IL stubs for every native export up front. Missing DLL or entry point is not fatal.
            try { Marshal.PrelinkAll(typeof(NativePlaybackInterop)); } catch { }

            var document = new MacroDocument(0, "(path warm-up)",
            [
                new KeyStep(KeyActionKind.Down, HidKey.A, HidModifier.None, TimeSpan.Zero),
                new WaitStep(TimeSpan.FromMilliseconds(1)),
                new KeyStep(KeyActionKind.Up, HidKey.A, HidModifier.None, TimeSpan.Zero)
            ]);
            var options = new PlaybackExecutionOptions(PlaybackMode.FixedCount, 1, PixelEvaluationMode.Skip, false, PrecisionMode.ExtremeDuringPlayback);
            using var executor = new MacroPlaybackExecutor(new InertSink());
            using var controller = new MacroPlaybackController(document, executor, options);
            for (var i = 0; i < rounds; i++)
            {
                controller.TriggerPressedAsync();
                controller.WhenIdleAsync().GetAwaiter().GetResult();
                completed++;
            }
        }
        catch { }
        return completed;
    }

    /// <returns>The number of rounds that ran on the native backend.</returns>
    public static int RunNative(PrecisionMode precision, string? affinityMask, PrecisionGeneration generation, int rounds = 2)
    {
        if (!PlaybackPerformancePolicy.For(precision).PreferNative) return 0;
        var native = 0;
        try
        {
            using var quiet = PlaybackRuntimeStatus.Suppress();
            // No input steps: the compiled plan has no batches, so the native run submits nothing.
            var document = new MacroDocument(0, "(path warm-up)", [new WaitStep(TimeSpan.FromMilliseconds(1))]);
            var options = new PlaybackExecutionOptions(PlaybackMode.FixedCount, 1, PixelEvaluationMode.Skip, false, precision, affinityMask ?? "")
            {
                PressFloor = InputPressFloor.Factory with { Generation = generation }
            };
            using var executor = new MacroPlaybackExecutor(new SendInputMacroSink());
            executor.Prepare(document, options);
            using var controller = new MacroPlaybackController(document, executor, options);
            for (var i = 0; i < rounds; i++)
            {
                controller.TriggerPressedAsync();
                var result = controller.WhenIdleAsync().GetAwaiter().GetResult();
                if (result.InputStats?.BackendName.StartsWith("native", StringComparison.Ordinal) == true) native++;
            }
        }
        catch { }
        return native;
    }

    private sealed class InertSink : IMacroInputSink
    {
        public bool IsAvailable => true;
        public void Submit(uint sequence, InputAction action) { }
        public InputSubmissionStats? GetStats() => null;
    }
}
