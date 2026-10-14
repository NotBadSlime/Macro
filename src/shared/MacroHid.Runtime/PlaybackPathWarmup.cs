using System.Runtime.InteropServices;
using MacroHid.Core;

namespace MacroHid.Runtime;

/// <summary>
/// Exercises the hotkey-to-first-input path once at startup so the first real press does not pay for JIT and
/// P/Invoke stub generation (measured 33-36ms on the calling thread in a cold process; steady state is ~10us).
/// Uses an inert sink and never calls <see cref="MacroPlaybackExecutor.Prepare"/>, which would re-prepare the
/// native engine and could override the user's core selection. Status reporting is suppressed for the duration.
/// </summary>
public static class PlaybackPathWarmup
{
    private static int queued;

    public static void QueueRun()
    {
        if (Interlocked.Exchange(ref queued, 1) != 0) return;
        try
        {
            new Thread(() => Run()) { IsBackground = true, Name = "MacroHID-PathWarmup", Priority = ThreadPriority.BelowNormal }.Start();
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

    private sealed class InertSink : IMacroInputSink
    {
        public bool IsAvailable => true;
        public void Submit(uint sequence, InputAction action) { }
        public InputSubmissionStats? GetStats() => null;
    }
}
