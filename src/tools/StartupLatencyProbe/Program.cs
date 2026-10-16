using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using MacroHid.Core;
using MacroHid.Runtime;

// Headless hotkey-to-first-SendInput probe. Mirrors what MacroStudio does before a hotkey can fire (startup
// warm-up, path warm-up, precision warm lease, executor.Prepare) and then calls TriggerPressedAsync from an
// AboveNormal thread, the way the keyboard hook thread does. Each run injects F24 down/up into the foreground
// window (F24 has no default binding). Stage times come from the MacroHid-Playback EventSource.
//
//   StartupLatencyProbe --precision balanced --runs 500 --interval-ms 20
//   precision: low | balanced | extreme  (UI names: 低占用 / 均衡 / 极限)

string Option(string key, string fallback)
{
    int at = Array.IndexOf(args, key);
    return at >= 0 && at + 1 < args.Length ? args[at + 1] : fallback;
}

var precision = Option("--precision", "balanced") switch
{
    "low" => PrecisionMode.Balanced,
    "extreme" => PrecisionMode.UltraLowJitter,
    _ => PrecisionMode.ExtremeDuringPlayback
};
int runs = int.Parse(Option("--runs", "300"), CultureInfo.InvariantCulture);
int intervalMs = int.Parse(Option("--interval-ms", "20"), CultureInfo.InvariantCulture);
var policy = PlaybackPerformancePolicy.For(precision);

using var listener = new StageListener();
var setup = Stopwatch.StartNew();
NativePlaybackWarmup.TryWarmUpForPrecision(precision, "", PrecisionGeneration.Latest);
PlaybackPathWarmup.Run();
bool nativePathWarmup = !args.Contains("--no-native-path-warmup");
// Looked up by reflection so the probe also builds against revisions before the native path warm-up existed (A/B runs).
var runNative = typeof(PlaybackPathWarmup).GetMethod("RunNative");
int nativeWarmRounds = nativePathWarmup && runNative is not null
    ? (int)runNative.Invoke(null, [precision, "", PrecisionGeneration.Latest, 2])!
    : 0;
using var lease = policy.PreferNative ? PrecisionPlaybackContext.AcquireWarmLease() : null;

var document = McrxParser.Parse("{\"steps\":[{\"type\":\"key.down\",\"key\":\"F24\"},{\"type\":\"key.up\",\"key\":\"F24\"}]}");
var options = new PlaybackExecutionOptions(PlaybackMode.FixedCount, 1, PixelEvaluationMode.Skip, false, precision);
using var executor = new MacroPlaybackExecutor(new SendInputMacroSink());
executor.Prepare(document, options);
await using var controller = new MacroPlaybackController(document, executor, options);
listener.Clear();
Console.WriteLine($"precision={precision} runs={runs} interval={intervalMs}ms setup={setup.ElapsedMilliseconds}ms nativePathWarmupRounds={nativeWarmRounds}");

var backends = new ConcurrentBag<string>();
var hookThread = new Thread(() =>
{
    for (int i = 0; i < runs; i++)
    {
        Thread.Sleep(intervalMs);
        long qpc = Stopwatch.GetTimestamp();
        controller.TriggerPressedAsync(qpc);
        var result = controller.WhenIdleAsync().GetAwaiter().GetResult();
        backends.Add(result.InputStats?.BackendName ?? "(none)");
    }
}) { Name = "Probe-Hotkey", Priority = ThreadPriority.AboveNormal, IsBackground = true };
hookThread.Start();
hookThread.Join();

Console.WriteLine("backend=" + string.Join(",", backends.GroupBy(b => b).Select(g => $"{g.Key}x{g.Count()}")));
var samples = listener.Runs();
if (samples.Count == 0) { Console.WriteLine("no trace samples"); return 1; }
var stages = new[]
{
    PlaybackTraceStage.Dispatch, PlaybackTraceStage.WorkerStart, PlaybackTraceStage.PrecisionEntered,
    PlaybackTraceStage.ScopesReady, PlaybackTraceStage.SafetyReady, PlaybackTraceStage.PlanReady,
    PlaybackTraceStage.NativeQueued, PlaybackTraceStage.NativeFirstSubmit, PlaybackTraceStage.SubmitStart
};
double Us(long ticks) => ticks * 1_000_000.0 / Stopwatch.Frequency;
var first = samples[0];
Console.WriteLine("stage(from Hotkey)        first      p50      p90      p99    p99.9      max   n");
foreach (var stage in stages)
{
    var values = samples.Where(s => s.ContainsKey(PlaybackTraceStage.Hotkey) && s.ContainsKey(stage))
        .Select(s => Us(s[stage] - s[PlaybackTraceStage.Hotkey])).OrderBy(v => v).ToArray();
    if (values.Length == 0) continue;
    double P(double q) => values[Math.Min(values.Length - 1, (int)Math.Ceiling(q * values.Length) - 1)];
    string firstText = first.TryGetValue(stage, out var f) && first.TryGetValue(PlaybackTraceStage.Hotkey, out var h)
        ? Us(f - h).ToString("F1", CultureInfo.InvariantCulture) : "-";
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"{stage,-24}{firstText,9}{P(0.5),9:F1}{P(0.9),9:F1}{P(0.99),9:F1}{P(0.999),9:F1}{values[^1],9:F1}{values.Length,4}"));
}
var finalStage = samples.Any(s => s.ContainsKey(PlaybackTraceStage.NativeFirstSubmit))
    ? PlaybackTraceStage.NativeFirstSubmit : PlaybackTraceStage.SubmitStart;
var finals = samples.Where(s => s.ContainsKey(PlaybackTraceStage.Hotkey) && s.ContainsKey(finalStage))
    .Select(s => Us(s[finalStage] - s[PlaybackTraceStage.Hotkey])).ToArray();
var steady = finals.Skip(1).OrderBy(v => v).ToArray();
double Q(double q) => steady.Length == 0 ? 0 : steady[Math.Min(steady.Length - 1, (int)Math.Ceiling(q * steady.Length) - 1)];
Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
    $"SUMMARY stage={finalStage} first={finals[0]:F1} p50={Q(0.5):F1} p99={Q(0.99):F1} p999={Q(0.999):F1} max={(steady.Length == 0 ? 0 : steady[^1]):F1}"));
return 0;

sealed class StageListener : EventListener
{
    private readonly ConcurrentDictionary<long, ConcurrentDictionary<PlaybackTraceStage, long>> runs = new();
    private readonly ConcurrentQueue<long> order = new();

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == "MacroHid-Playback") EnableEvents(source, EventLevel.Informational);
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (e.EventId != 1 || e.Payload is null) return;
        long run = (long)e.Payload[0]!;
        var stage = (PlaybackTraceStage)(int)e.Payload[1]!;
        long qpc = (long)e.Payload[2]!;
        if (run == 0) return;
        var stages = runs.GetOrAdd(run, id => { order.Enqueue(id); return new(); });
        stages.TryAdd(stage, qpc);
    }

    public void Clear() { runs.Clear(); order.Clear(); }

    public List<IReadOnlyDictionary<PlaybackTraceStage, long>> Runs() =>
        order.Select(id => (IReadOnlyDictionary<PlaybackTraceStage, long>)runs[id]).ToList();
}
