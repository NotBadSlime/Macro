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
