using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MacroHid.Core;
using MacroHid.Runtime;

internal static class PerformanceProfileTests
{
    private static void Check(bool value, string text) { if (!value) throw new Exception(text); }
    internal static void CompactPixels()
    {
        var raw = new byte[640 * 360 * 4];
        new Random(47).NextBytes(raw);
        RgbColor[] ConvertObjects()
        {
            var result = new RgbColor[raw.Length / 4];
            for (int i = 0; i < result.Length; i++) result[i] = new(raw[i * 4 + 2], raw[i * 4 + 1], raw[i * 4]);
            return result;
        }
        using var compact = new PixelFrame(raw, 640 * 360);
        var objects = ConvertObjects();
        for (int i = 0; i < objects.Length; i++)
            Check(compact[i] == new PixelValue(objects[i].R, objects[i].G, objects[i].B), "BGRA channel order");
        using var owner = PixelFrame.FromRgb(objects);
        var lease = owner.Lease();
        var retained = lease.Frame[1023];
        var borrowed = lease.Frame.Lease();
        lease.Dispose(); lease.Dispose();
        Check(borrowed.Frame[1023] == retained, "independent lease corrupted");
        borrowed.Dispose();
        var region = ScreenRegion.FromRect(0, 0, 640, 360);
        Check(PixelHashEngine.ComputeHash(region, compact, default).SequenceEqual(PixelHashEngine.ComputeHash(region, _ => objects)), "hash compatibility");
        // Warm up, then compare conversion only; no capture time is included.
        for (int i = 0; i < 3; i++) { ConvertObjects(); using var f = new PixelFrame(raw, 640 * 360); }
        static (long Bytes, double Median) Measure(Action action)
        {
            var ticks = new long[31];
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < ticks.Length; i++) { long start = Stopwatch.GetTimestamp(); action(); ticks[i] = Stopwatch.GetTimestamp() - start; }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Array.Sort(ticks);
            return (allocated / ticks.Length, ticks[15] * 1e6 / Stopwatch.Frequency);
        }
        var old = Measure(() => GC.KeepAlive(ConvertObjects()));
        var next = Measure(() => { using var f = new PixelFrame(raw, 640 * 360); GC.KeepAlive(f); });
        Console.WriteLine($"METRIC BGRA conversion old={old.Bytes} B/{old.Median:F3} us; compact={next.Bytes} B/{next.Median:F3} us");
        Check(next.Bytes <= old.Bytes * .15, "compact allocation target");
        if (Environment.GetEnvironmentVariable("MACROHID_PERF_GATES") == "1")
            Check(next.Median <= old.Median * .5, "compact conversion time target");
    }
    internal static void MatchingBoundaries()
    {
        var rng = new Random(917);
        var previous = Environment.GetEnvironmentVariable("MACROHID_LEGACY_IMAGES");
        try
        {
            for (int sample = 0; sample < 30; sample++)
            {
                byte[] raw = new byte[7 * 5 * 4]; rng.NextBytes(raw);
                var source = BitmapSource.Create(7, 5, 96, 96, PixelFormats.Bgra32, null, raw, 28);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source));
                using var stream = new MemoryStream(); encoder.Save(stream); var png = stream.ToArray();
                var search = Enumerable.Range(0, 19 * 13).Select(_ => new RgbColor((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256))).ToArray();
                if (sample == 0) Array.Fill(search, new RgbColor(127, 127, 127));
                var region = ScreenRegion.FromRect(0, 0, 19, 13);
                Environment.SetEnvironmentVariable("MACROHID_LEGACY_IMAGES", "1");
                var reference = TemplateMatchEngine.Match(region, png, 0, _ => search);
                foreach (double threshold in new[] { .85, reference.score, Math.BitIncrement(reference.score), Math.BitDecrement(reference.score) })
                {
                    var expected = TemplateMatchEngine.Match(region, png, threshold, _ => search);
                    Environment.SetEnvironmentVariable("MACROHID_LEGACY_IMAGES", null);
                    var actual = TemplateMatchEngine.Match(region, png, threshold, _ => search);
                    Check(actual.x == expected.x && actual.y == expected.y && Math.Abs(actual.score - expected.score) < 1e-9, "NCC boundary or best position changed");
                    Environment.SetEnvironmentVariable("MACROHID_LEGACY_IMAGES", "1");
                }
            }
        }
        finally { Environment.SetEnvironmentVariable("MACROHID_LEGACY_IMAGES", previous); }
    }
    internal static void TraceEvents()
    {
        using var listener = new TraceListener();
        long now = Stopwatch.GetTimestamp();
        PlaybackTrace.Mark(PlaybackTraceStage.Hotkey, now, 123);
        Check(listener.Values.TryDequeue(out var values) && (long)values[0]! == 123
            && (int)values[1]! == (int)PlaybackTraceStage.Hotkey && (long)values[2]! == now
            && (long)values[3]! == Stopwatch.Frequency, "trace lost raw QPC or run identity");
    }
    internal static void ForegroundNameCache()
    {
        var foreground = (Window: (IntPtr)10, ProcessId: 100u);
        var lookups = new List<uint>();
        string? answer = "game";
        var cache = new MacroStudio.Services.ForegroundProcessNameCache(() => foreground, id => { lookups.Add(id); return answer; });
        for (int i = 0; i < 1000; i++) Check(cache.Get() == "game", "cached name");
        Check(lookups.Count == 1, "unchanged foreground must resolve once, got " + lookups.Count);
        foreground = ((IntPtr)11, 100u); answer = "other";
        Check(cache.Get() == "other" && lookups.Count == 2, "new window handle must re-resolve");
        foreground = ((IntPtr)11, 101u); answer = "third";
        Check(cache.Get() == "third" && lookups.Count == 3, "new process id must re-resolve");
        answer = null; foreground = ((IntPtr)12, 102u);
        Check(cache.Get() == null && cache.Get() == null && lookups.Count == 5, "failed lookups must not be cached");
        foreground = (IntPtr.Zero, 0);
        Check(cache.Get() == null && lookups.Count == 5, "no foreground window must not query a process");
        var throwing = new MacroStudio.Services.ForegroundProcessNameCache(() => throw new InvalidOperationException(), _ => "x");
        Check(throwing.Get() == null, "query failure returns null");
    }
    internal static void PrecisionWarmLease()
    {
        Check(PrecisionPlaybackContext.ActiveUsers == 0, "no users at start");
        using (var lease = PrecisionPlaybackContext.AcquireWarmLease())
        {
            Check(PrecisionPlaybackContext.ActiveUsers == 1, "lease counts as a user");
            var run = PrecisionPlaybackContext.Enter(PrecisionMode.UltraLowJitter);
            Check(PrecisionPlaybackContext.ActiveUsers == 2, "run adds a user");
            run.Dispose();
            Check(PrecisionPlaybackContext.ActiveUsers == 1, "a finished run must leave the lease holding the shared scopes");
        }
        Check(PrecisionPlaybackContext.ActiveUsers == 0, "lease release");
        var second = PrecisionPlaybackContext.AcquireWarmLease();
        var overlapping = PrecisionPlaybackContext.Enter(PrecisionMode.ExtremeDuringPlayback);
        second.Dispose(); second.Dispose();
        Check(PrecisionPlaybackContext.ActiveUsers == 1, "double dispose must release once and the run keeps the scopes");
        overlapping.Dispose();
        Check(PrecisionPlaybackContext.ActiveUsers == 0, "all released");
        // Per-thread and per-process state (priority, affinity, GC mode) is a run concern, not a lease concern.
        var before = System.Threading.Thread.CurrentThread.Priority;
        using (PrecisionPlaybackContext.AcquireWarmLease())
            Check(System.Threading.Thread.CurrentThread.Priority == before, "lease must not change thread priority");
        using (PrecisionPlaybackContext.AcquireWarmLease())
        {
            var enter = new double[200];
            for (int i = 0; i < enter.Length; i++)
            {
                long start = Stopwatch.GetTimestamp();
                var context = PrecisionPlaybackContext.Enter(PrecisionMode.UltraLowJitter);
                enter[i] = (Stopwatch.GetTimestamp() - start) * 1e6 / Stopwatch.Frequency;
                context.Dispose();
            }
            Array.Sort(enter);
            Console.WriteLine($"METRIC Enter with warm lease p50={enter[100]:F1} us p99={enter[198]:F1} us");
        }
    }
    internal static void ControllerExposesPrecision()
    {
        var document = McrxParser.Parse("{\"steps\":[{\"type\":\"wait\",\"ms\":1}]}");
        var options = new PlaybackExecutionOptions(PlaybackMode.FixedCount, 1, PixelEvaluationMode.Skip, false, PrecisionMode.UltraLowJitter);
        using var executor = new MacroPlaybackExecutor(new NullSink());
        using var controller = new MacroPlaybackController(document, executor, options);
        Check(controller.Precision == PrecisionMode.UltraLowJitter, "initial precision");
        using var next = new MacroPlaybackExecutor(new NullSink());
        controller.UpdateDefinition(document, next, options with { Precision = PrecisionMode.Balanced });
        Check(controller.Precision == PrecisionMode.Balanced, "precision follows UpdateDefinition");
    }
    private sealed class NullSink : IMacroInputSink
    {
        public bool IsAvailable => true;
        public void Submit(uint sequence, InputAction action) { }
        public InputSubmissionStats? GetStats() => null;
    }
    internal static void PreparedSafetyPlanIsReused()
    {
        var document = new MacroDocument(1, "safety-cache", [
            new KeyStep(KeyActionKind.Down, HidKey.A, HidModifier.None, TimeSpan.Zero),
            new WaitStep(TimeSpan.FromMilliseconds(1)),
            new KeyStep(KeyActionKind.Up, HidKey.A, HidModifier.None, TimeSpan.Zero)]);
        var options = new PlaybackExecutionOptions(PlaybackMode.FixedCount, 1, PixelEvaluationMode.Skip, false, PrecisionMode.Balanced);

        using var unprepared = new MacroPlaybackExecutor(new RecordingSink());
        long before = PlaybackInputSafetyPlan.CreatedCount;
        for (int i = 0; i < 3; i++) unprepared.RunAsync(document, options, CancellationToken.None).GetAwaiter().GetResult();
        Check(PlaybackInputSafetyPlan.CreatedCount - before == 3, "an unprepared executor derives the safety plan per run (counter sanity)");

        using var prepared = new MacroPlaybackExecutor(new RecordingSink());
        prepared.Prepare(document, options);
        before = PlaybackInputSafetyPlan.CreatedCount;
        for (int i = 0; i < 3; i++) prepared.RunAsync(document, options, CancellationToken.None).GetAwaiter().GetResult();
        Check(PlaybackInputSafetyPlan.CreatedCount == before, "a prepared executor must reuse the safety plan: " + (PlaybackInputSafetyPlan.CreatedCount - before));

        // Reuse must not weaken the guarantee: cancelling while a key is held still releases it.
        var held = new MacroDocument(2, "held", [
            new KeyStep(KeyActionKind.Down, HidKey.A, HidModifier.None, TimeSpan.Zero),
            new WaitStep(TimeSpan.FromSeconds(30)),
            new KeyStep(KeyActionKind.Up, HidKey.A, HidModifier.None, TimeSpan.Zero)]);
        var sink = new RecordingSink();
        using var cancelling = new MacroPlaybackExecutor(sink);
        cancelling.Prepare(held, options);
        using var cancellation = new CancellationTokenSource();
        var run = cancelling.RunAsync(held, options, cancellation.Token);
        Check(sink.FirstAction.Wait(5000), "key down was never submitted");
        cancellation.Cancel();
        try { run.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        var actions = sink.Snapshot();
        Check(actions.Count >= 2 && actions[0] is KeyInputAction { Kind: KeyActionKind.Down, Key: HidKey.A }
            && actions[^1] is KeyInputAction { Kind: KeyActionKind.Up, Key: HidKey.A }, "held key must be released after cancel");
    }
    private sealed class RecordingSink : IMacroInputSink
    {
        private readonly List<InputAction> actions = [];
        public readonly ManualResetEventSlim FirstAction = new();
        public bool IsAvailable => true;
        public void Submit(uint sequence, InputAction action) { lock (actions) actions.Add(action); FirstAction.Set(); }
        public InputSubmissionStats? GetStats() => null;
        public List<InputAction> Snapshot() { lock (actions) return [.. actions]; }
    }
    internal static void PathWarmupLeavesRuntimeStatusAlone()
    {
        var document = new MacroDocument(3, "after-warmup", [new WaitStep(TimeSpan.FromMilliseconds(1))]);
        var options = new PlaybackExecutionOptions(PlaybackMode.FixedCount, 1, PixelEvaluationMode.Skip, false, PrecisionMode.Balanced);
        using (var seed = new MacroPlaybackExecutor(new RecordingSink()))
            seed.RunAsync(document, options, CancellationToken.None).GetAwaiter().GetResult();
        var backend = PlaybackRuntimeStatus.ActualBackend;
        var reason = PlaybackRuntimeStatus.FallbackReason;
        Check(backend == "managed", "seed run reports its backend: " + backend);
        PlaybackRuntimeStatus.ReportBackend("native-shared", "sentinel");

        Check(PlaybackPathWarmup.Run(rounds: 2) == 2, "both warm-up rounds must complete");
        Check(PlaybackRuntimeStatus.ActiveRuns == 0, "warm-up must not leave an active run");
        Check(PlaybackRuntimeStatus.ActualBackend == "native-shared" && PlaybackRuntimeStatus.FallbackReason == "sentinel",
            "warm-up must not overwrite the reported backend: " + PlaybackRuntimeStatus.ActualBackend);

        // The suppression is scoped to the warm-up, not sticky: a real run afterwards reports normally.
        using (var real = new MacroPlaybackExecutor(new RecordingSink()))
            real.RunAsync(document, options, CancellationToken.None).GetAwaiter().GetResult();
        Check(PlaybackRuntimeStatus.ActualBackend == "managed", "real runs after warm-up must report: " + PlaybackRuntimeStatus.ActualBackend);
        PlaybackRuntimeStatus.ReportBackend(backend, reason);
        // Queueing is idempotent and never throws.
        PlaybackPathWarmup.QueueRun(); PlaybackPathWarmup.QueueRun();
    }
    internal static void HookSelfTime()
    {
        var recorder = new HookSelfTimeRecorder(100);
        recorder.Record(0, 5);
        Check(recorder.TotalRecorded == 0, "disabled recorder must not record");
        recorder.Enabled = true;
        // 1 tick == 1us at a 1 MHz frequency. Record 1..150: the ring keeps the newest 100 (51..150).
        for (int i = 1; i <= 150; i++) recorder.Record(0, i);
        var stats = recorder.Snapshot(1_000_000);
        Check(stats.Count == 100 && stats.MaxUs == 150 && stats.P50Us == 100 && stats.P99Us == 149 && stats.P999Us == 150, "ring or nearest-rank percentiles: " + stats);
        recorder.Reset();
        Check(recorder.Snapshot(1_000_000).Count == 0, "reset");
        recorder.Record(10, 4);
        Check(recorder.Snapshot(1_000_000).MaxUs == 0, "negative span must clamp to zero");
        recorder.Record(0, 1); recorder.Record(0, 1);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++) recorder.Record(0, i);
        Check(GC.GetAllocatedBytesForCurrentThread() - before == 0, "Record must not allocate");
    }
    private sealed class TraceListener : System.Diagnostics.Tracing.EventListener
    {
        internal readonly System.Collections.Concurrent.ConcurrentQueue<object?[]> Values = new();
        protected override void OnEventSourceCreated(System.Diagnostics.Tracing.EventSource source)
        {
            if (source.Name == "MacroHid-Playback") EnableEvents(source, System.Diagnostics.Tracing.EventLevel.Informational);
        }
        protected override void OnEventWritten(System.Diagnostics.Tracing.EventWrittenEventArgs e)
        {
            if (e.EventId == 1) Values.Enqueue(e.Payload!.ToArray());
        }
    }
}
