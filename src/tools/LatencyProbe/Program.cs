using System.Diagnostics;
using System.Runtime;
using MacroHid.Core;
using MacroHid.Runtime;

var iterations = ParseInt(args, "--iterations", 2_000);
var intervalUs = ParseInt(args, "--interval-us", 1_000);
var loopSteps = ParseInt(args, "--loop-steps", 0);
var loopIntervalUs = ParseInt(args, "--loop-interval-us", intervalUs);
var warmup = ParseInt(args, "--warmup", 100);
var legacy = HasFlag(args, "--legacy");
var cpuScan = HasFlag(args, "--cpu-scan");
var warmNative = HasFlag(args, "--warm-native");
var precreateNativePlan = HasFlag(args, "--precreate-native-plan");
var profile = ParsePrecisionMode(args);
var backend = ParseBackend(args);
var nativeEngine = ParseNativeEngineMode(args);
var traceOutliersPath = ParseString(args, "--trace-outliers", string.Empty);
var affinityMask = ParseNullableUInt64(args, "--affinity-mask");
var precisionTargetUs = PrecisionModeProfiles.ForMode(profile).TargetJitterMicroseconds;
var outlierThresholdUs = ParseInt(args, "--target-us", ParseInt(args, "--outlier-threshold-us", precisionTargetUs));

Console.WriteLine("MacroHID LatencyProbe");
Console.WriteLine($"iterations={iterations} interval={intervalUs}us warmup={warmup} profile={ToProfileText(profile)} targetPrecision={precisionTargetUs}us backend={ToBackendText(backend)} nativeEngine={ToNativeEngineText(nativeEngine)} mode={(!legacy ? "precision" : "legacy")} loopSteps={loopSteps} loopInterval={loopIntervalUs}us cpuScan={cpuScan} warmNative={warmNative} precreateNativePlan={precreateNativePlan} affinityMask={(affinityMask.HasValue ? "0x" + affinityMask.Value.ToString("X") : "default")} outlierThreshold={outlierThresholdUs}us");
Console.WriteLine("backend choices: auto|managed|native");
Console.WriteLine("native engine choices: auto|standby|inline");
Console.WriteLine($"timerFrequency={Stopwatch.Frequency} ticks/sec highResolution={Stopwatch.IsHighResolution}");

ApplyAffinityMaskIfRequested(affinityMask);

if (warmNative)
{
    var warmupStart = Stopwatch.GetTimestamp();
    NativePlaybackWarmup.TryWarmUp(scanCpu: cpuScan);
    var warmupUs = ToMicroseconds(Stopwatch.GetTimestamp() - warmupStart, Stopwatch.Frequency);
    Console.WriteLine($"nativeWarmup={warmupUs}us cpuScanReady={NativePlaybackWarmup.CpuScanReady}");
}

using var precisionContext = legacy
    ? null
    : PrecisionPlaybackContext.Enter(profile);

if (!legacy && OperatingSystem.IsWindows())
{
    RuntimeNativeMethods.NtQueryTimerResolution(out var minRes, out var maxRes, out var curRes);
    Console.WriteLine($"timerResolution min={minRes * 100}ns max={maxRes * 100}ns current={curRes * 100}ns");
    Console.WriteLine($"gcLatencyMode={GCSettings.LatencyMode}");
}

var clock = new QpcHighResolutionClock();
var delayStrategy = new QpcPlaybackDelayStrategy(clock, profile);
Console.WriteLine($"clockFrequency={clock.Frequency}");
if (cpuScan)
{
    PrintCpuScan(clock, delayStrategy, legacy);
}

Console.WriteLine();
if (ShouldUseNativeProbe(backend, profile, legacy)
    && RunNativeScheduleProbe(clock, profile, iterations, intervalUs, cpuScan, outlierThresholdUs, traceOutliersPath, nativeEngine, precreateNativePlan))
{
}
else
{
    RunScheduleProbe(clock, delayStrategy, iterations, intervalUs, warmup, legacy, outlierThresholdUs);
}

if (loopSteps > 0)
{
    Console.WriteLine();
    if (ShouldUseNativeProbe(backend, profile, legacy)
        && RunNativeLoopProbe(clock, profile, iterations, loopSteps, loopIntervalUs, cpuScan, outlierThresholdUs, traceOutliersPath, nativeEngine, precreateNativePlan))
    {
    }
    else
    {
        RunLoopProbe(clock, delayStrategy, iterations, loopSteps, loopIntervalUs, warmup, legacy, outlierThresholdUs, traceOutliersPath);
    }
}

NativePlaybackWarmup.Shutdown();
Console.WriteLine("Note: this probe measures user-mode scheduler jitter and prepared SendInput overhead without sending real key events.");
Environment.ExitCode = 0;

static void RunScheduleProbe(
    IHighResolutionClock clock,
    IPlaybackDelayStrategy delayStrategy,
    int iterations,
    int intervalUs,
    int warmup,
    bool legacy,
    int outlierThresholdUs)
{
    var scheduleJitter = new ProbeSeries();
    var submitDuration = new ProbeSeries();
    var encodeCost = new ProbeSeries();
    var conditionTrigger = new ProbeSeries();
    var intervalTicks = Math.Max(1, intervalUs * clock.Frequency / 1_000_000);
    var nextTick = clock.GetTimestamp() + intervalTicks;
    var noInputSink = new SendInputMacroSink();
    var emptyPrepared = PreparedInputBatch.FromActions(Array.Empty<InputAction>());

    InputAction[] probeActions =
    [
        new KeyInputAction(KeyActionKind.Down, HidKey.A, HidModifier.None),
        new KeyInputAction(KeyActionKind.Up, HidKey.A, HidModifier.None)
    ];

    using var cancellation = new CancellationTokenSource();

    for (var i = -warmup; i < iterations; i++)
    {
        WaitUntil(nextTick, clock, delayStrategy, cancellation.Token, legacy);

        var actualTick = clock.GetTimestamp();
        var scheduleDeltaUs = Math.Abs(ToMicroseconds(actualTick - nextTick, clock.Frequency));

        var encodeStart = clock.GetTimestamp();
        _ = PreparedInputBatch.FromActions(probeActions);
        var encodeEnd = clock.GetTimestamp();

        var submitStart = clock.GetTimestamp();
        if (OperatingSystem.IsWindows())
        {
            noInputSink.SubmitPrepared(0, emptyPrepared);
        }

        var submitEnd = clock.GetTimestamp();

        var conditionStart = clock.GetTimestamp();
        var conditionDue = conditionStart + Math.Max(1, intervalTicks / 4);
        WaitUntil(conditionDue, clock, delayStrategy, cancellation.Token, legacy);
        var conditionEnd = clock.GetTimestamp();

        if (i >= 0)
        {
            scheduleJitter.Record(scheduleDeltaUs);
            encodeCost.Record(Math.Abs(ToMicroseconds(encodeEnd - encodeStart, clock.Frequency)));
            submitDuration.Record(Math.Abs(ToMicroseconds(submitEnd - submitStart, clock.Frequency)));
            conditionTrigger.Record(Math.Abs(ToMicroseconds(conditionEnd - conditionDue, clock.Frequency)));
        }

        nextTick += intervalTicks;
    }

    Console.WriteLine($"scheduleJitter {scheduleJitter.Summary()} outliersOverTarget={scheduleJitter.CountOver(outlierThresholdUs)}");
    Console.WriteLine($"submitDuration {submitDuration.Summary()}");
    Console.WriteLine($"encodeCost {encodeCost.Summary()}");
    Console.WriteLine($"conditionTrigger {conditionTrigger.Summary()}");
}

static void RunLoopProbe(
    IHighResolutionClock clock,
    IPlaybackDelayStrategy delayStrategy,
    int iterations,
    int loopSteps,
    int loopIntervalUs,
    int warmup,
    bool legacy,
    int outlierThresholdUs,
    string traceOutliersPath)
{
    var stepJitter = new ProbeSeries();
    var loopEndDrift = new ProbeSeries();
    var loopDurationError = new ProbeSeries();
    var intervalTicks = Math.Max(1, loopIntervalUs * clock.Frequency / 1_000_000);
    var loopDurationTicks = Math.Max(1, intervalTicks * loopSteps);
    var plannedLoopStartTick = clock.GetTimestamp() + intervalTicks;

    using var cancellation = new CancellationTokenSource();

    for (var i = -warmup; i < iterations; i++)
    {
        WaitUntil(plannedLoopStartTick, clock, delayStrategy, cancellation.Token, legacy);
        var actualLoopStartTick = clock.GetTimestamp();

        for (var step = 0; step < loopSteps; step++)
        {
            var dueTick = plannedLoopStartTick + ((step + 1) * intervalTicks);
            WaitUntil(dueTick, clock, delayStrategy, cancellation.Token, legacy);
            if (i >= 0)
            {
                stepJitter.Record(Math.Abs(ToMicroseconds(clock.GetTimestamp() - dueTick, clock.Frequency)));
            }
        }

        var plannedLoopEndTick = plannedLoopStartTick + loopDurationTicks;
        var actualLoopEndTick = clock.GetTimestamp();
        if (i >= 0)
        {
            var loopEndDriftUs = Math.Abs(ToMicroseconds(actualLoopEndTick - plannedLoopEndTick, clock.Frequency));
            loopEndDrift.Record(loopEndDriftUs);
            loopDurationError.Record(Math.Abs(ToMicroseconds((actualLoopEndTick - actualLoopStartTick) - loopDurationTicks, clock.Frequency)));
            if (loopEndDriftUs > outlierThresholdUs)
            {
                TraceOutlierCsv(traceOutliersPath, "managed-loop", i, plannedLoopEndTick, actualLoopEndTick, -1, "managed", loopEndDriftUs);
            }
        }

        plannedLoopStartTick += loopDurationTicks;
    }

    Console.WriteLine($"loopStepJitter {stepJitter.Summary()}");
    Console.WriteLine($"loopEndDrift {loopEndDrift.Summary()} target={outlierThresholdUs}us outliersOverTarget={loopEndDrift.CountOver(outlierThresholdUs)}");
    Console.WriteLine($"loopDurationError {loopDurationError.Summary()} target={outlierThresholdUs}us outliersOverTarget={loopDurationError.CountOver(outlierThresholdUs)}");
}

static bool RunNativeScheduleProbe(
    IHighResolutionClock clock,
    PrecisionMode profile,
    int iterations,
    int intervalUs,
    bool cpuScan,
    int outlierThresholdUs,
    string traceOutliersPath,
    NativePlaybackEngineMode nativeEngine,
    bool precreateNativePlan)
{
    var intervalTicks = Math.Max(1, intervalUs * clock.Frequency / 1_000_000);
    var batches = new MhpBatch[iterations];
    for (var i = 0; i < batches.Length; i++)
    {
        batches[i] = new MhpBatch((i + 1) * intervalTicks, 0, 0, 0);
    }

    var timeline = new NativePlaybackTimeline([], batches, Math.Max(1, batches.Length * intervalTicks));
    using var cancellation = new CancellationTokenSource();
    if (!TryRunNativeProbeTimeline(
            timeline,
            clock.Frequency,
            profile,
            cancellation.Token,
            out var diagnostics,
            out var fallbackReason,
            outlierThresholdUs,
            cpuScan,
            nativeEngine,
            precreateNativePlan))
    {
        Console.WriteLine($"nativeBackend fallbackReason={fallbackReason}");
        return false;
    }

    PrintNativeDiagnostics("scheduleJitter", diagnostics, outlierThresholdUs);
    TraceNativeEventsIfNeeded(traceOutliersPath, "native-schedule-batch", diagnostics);
    TraceNativeAggregateIfNeeded(traceOutliersPath, "native-schedule", diagnostics);
    return true;
}

static bool RunNativeLoopProbe(
    IHighResolutionClock clock,
    PrecisionMode profile,
    int iterations,
    int loopSteps,
    int loopIntervalUs,
    bool cpuScan,
    int outlierThresholdUs,
    string traceOutliersPath,
    NativePlaybackEngineMode nativeEngine,
    bool precreateNativePlan)
{
    var intervalTicks = Math.Max(1, loopIntervalUs * clock.Frequency / 1_000_000);
    var loopDurationTicks = Math.Max(1, intervalTicks * loopSteps);
    var batches = new MhpBatch[iterations * loopSteps];
    var index = 0;
    for (var iteration = 0; iteration < iterations; iteration++)
    {
        var loopStart = iteration * loopDurationTicks;
        for (var step = 0; step < loopSteps; step++)
        {
            batches[index++] = new MhpBatch(loopStart + ((step + 1) * intervalTicks), 0, 0, 0);
        }
    }

    var timeline = new NativePlaybackTimeline([], batches, Math.Max(1, iterations * loopDurationTicks), loopSteps);
    using var cancellation = new CancellationTokenSource();
    if (!TryRunNativeProbeTimeline(
            timeline,
            clock.Frequency,
            profile,
            cancellation.Token,
            out var diagnostics,
            out var fallbackReason,
            outlierThresholdUs,
            cpuScan,
            nativeEngine,
            precreateNativePlan))
    {
        Console.WriteLine($"nativeBackend fallbackReason={fallbackReason}");
        return false;
    }

    PrintNativeDiagnostics("loopProbe", diagnostics, outlierThresholdUs);
    Console.WriteLine($"loopDurationError nativeTimelineDuration={ToMicroseconds(timeline.DurationTicks, clock.Frequency)}us batches={batches.Length}");
    TraceNativeEventsIfNeeded(traceOutliersPath, "native-loop-batch", diagnostics);
    TraceNativeAggregateIfNeeded(traceOutliersPath, "native-loop", diagnostics);
    return true;
}

static bool TryRunNativeProbeTimeline(
    NativePlaybackTimeline timeline,
    long qpcFrequency,
    PrecisionMode profile,
    CancellationToken cancellationToken,
    out NativePlaybackRunDiagnostics diagnostics,
    out string fallbackReason,
    int outlierThresholdUs,
    bool cpuScan,
    NativePlaybackEngineMode nativeEngine,
    bool precreateNativePlan)
{
    if (!precreateNativePlan)
    {
        return NativePlaybackEngine.TryRunTimeline(
            timeline,
            qpcFrequency,
            profile,
            cancellationToken,
            out diagnostics,
            out fallbackReason,
            outlierThresholdUs,
            cpuScan,
            nativeEngine);
    }

    if (!NativePlaybackEngine.TryCreatePreparedPlan(
            timeline,
            qpcFrequency,
            out var preparedPlan,
            out diagnostics,
            out fallbackReason))
    {
        return false;
    }

    using var prepared = preparedPlan!;
    return NativePlaybackEngine.TryRunPrepared(
        prepared,
        profile,
        cancellationToken,
        out diagnostics,
        out fallbackReason,
        outlierThresholdUs,
        cpuScan,
        nativeEngine);
}

static void PrintNativeDiagnostics(string label, NativePlaybackRunDiagnostics diagnostics, int outlierThresholdUs)
{
    Console.WriteLine($"{label} nativeBatchLate backend={diagnostics.BackendName} p99.9={diagnostics.P999LateMicroseconds}us max={diagnostics.MaxLateMicroseconds}us target={outlierThresholdUs}us outliersOverTarget={diagnostics.OutliersOverThreshold}");
    if (diagnostics.MaxLoopEndLateMicroseconds > 0
        || diagnostics.P999LoopEndLateMicroseconds > 0
        || diagnostics.LoopEndOutliersOverThreshold > 0)
    {
        Console.WriteLine($"{label} nativeLoopEndLate backend={diagnostics.BackendName} p99.9={diagnostics.P999LoopEndLateMicroseconds}us max={diagnostics.MaxLoopEndLateMicroseconds}us target={outlierThresholdUs}us outliersOverTarget={diagnostics.LoopEndOutliersOverThreshold}");
    }

    Console.WriteLine($"nativePlanCreate={diagnostics.NativePlanCreateMicroseconds}us nativeStartup={diagnostics.NativeStartupMicroseconds}us nativeRunOverhead={diagnostics.NativeRunOverheadMicroseconds}us nativeSubmitDuration={diagnostics.NativeSubmitDurationMicroseconds}us selectedCpu={diagnostics.SelectedCpuSet} cpuMigrationCount={diagnostics.CpuMigrationCount} cpuSetApplied={diagnostics.CpuSetAppliedCount} standbyUsed={diagnostics.StandbyUsed} workerWins={diagnostics.PrimaryWorkerWins}/{diagnostics.HelperWorkerWins} engineWake={diagnostics.EngineWakeCostMicroseconds}us maxLateBatch={diagnostics.MaxLateBatchIndex} maxLateCpu={diagnostics.MaxLateCpu} maxLateWorker={diagnostics.MaxLateWorker} priorityState={diagnostics.PriorityState} waitPathBreakdown={diagnostics.WaitPathBreakdown}");
}

static void TraceNativeAggregateIfNeeded(string traceOutliersPath, string source, NativePlaybackRunDiagnostics diagnostics)
{
    if (diagnostics.OutliersOverThreshold > 0 && diagnostics.OutlierEvents.Count == 0)
    {
        TraceOutlierCsv(traceOutliersPath, source, diagnostics.MaxLateBatchIndex, 0, 0, diagnostics.MaxLateCpu, $"{diagnostics.WaitPathBreakdown};winner={diagnostics.MaxLateWorker}", diagnostics.MaxLateMicroseconds);
    }
}

static void TraceNativeEventsIfNeeded(string traceOutliersPath, string source, NativePlaybackRunDiagnostics diagnostics)
{
    foreach (var item in diagnostics.OutlierEvents)
    {
        TraceOutlierCsv(
            traceOutliersPath,
            source,
            item.BatchIndex,
            item.DueTick,
            item.ActualTick,
            item.Cpu,
            $"{diagnostics.WaitPathBreakdown};winner={item.Worker};loopEnd={item.LoopEnd}",
            item.LateMicroseconds);
    }
}

static void TraceOutlierCsv(
    string traceOutliersPath,
    string source,
    int batchIndex,
    long dueTick,
    long actualTick,
    int cpuId,
    string waitPath,
    long lateUs)
{
    if (string.IsNullOrWhiteSpace(traceOutliersPath))
    {
        return;
    }

    var directory = Path.GetDirectoryName(Path.GetFullPath(traceOutliersPath));
    if (!string.IsNullOrWhiteSpace(directory))
    {
        Directory.CreateDirectory(directory);
    }

    var writeHeader = !File.Exists(traceOutliersPath);
    using var writer = new StreamWriter(traceOutliersPath, append: true);
    if (writeHeader)
    {
        writer.WriteLine("source,batchIndex,dueTick,actualTick,cpuId,waitPath,lateUs");
    }

    writer.WriteLine($"{source},{batchIndex},{dueTick},{actualTick},{cpuId},\"{waitPath.Replace("\"", "\"\"")}\",{lateUs}");
}

static void PrintCpuScan(IHighResolutionClock clock, IPlaybackDelayStrategy delayStrategy, bool legacy)
{
    using var cancellation = new CancellationTokenSource();
    var probe = new ProbeSeries();
    var intervalTicks = Math.Max(1, 1_000 * clock.Frequency / 1_000_000);
    var dueTick = clock.GetTimestamp() + intervalTicks;

    for (var i = 0; i < 128; i++)
    {
        WaitUntil(dueTick, clock, delayStrategy, cancellation.Token, legacy);
        probe.Record(Math.Abs(ToMicroseconds(clock.GetTimestamp() - dueTick, clock.Frequency)));
        dueTick += intervalTicks;
    }

    Console.WriteLine($"cpuScan processors={Environment.ProcessorCount} oneMillisecondProbe={probe.Summary()}");
}

static void WaitUntil(
    long targetTick,
    IHighResolutionClock clock,
    IPlaybackDelayStrategy delayStrategy,
    CancellationToken cancellationToken,
    bool legacy)
{
    if (legacy)
    {
        LegacyWaitUntil(targetTick, clock);
    }
    else
    {
        delayStrategy.WaitUntil(targetTick, clock.Frequency, cancellationToken, noWait: false);
    }
}

static void LegacyWaitUntil(long targetTick, IHighResolutionClock clock)
{
    while (true)
    {
        var remaining = targetTick - clock.GetTimestamp();
        if (remaining <= 0)
        {
            return;
        }

        var remainingUs = remaining * 1_000_000 / clock.Frequency;
        if (remainingUs > 2_000)
        {
            Thread.Sleep(1);
        }
        else if (remainingUs > 200)
        {
            Thread.Yield();
        }
        else
        {
            Thread.SpinWait(32);
        }
    }
}

static long ToMicroseconds(long ticks, long frequency)
{
    return frequency <= 0 ? 0 : (long)Math.Round(ticks * 1_000_000.0 / frequency, MidpointRounding.AwayFromZero);
}

static PrecisionMode ParsePrecisionMode(string[] args)
{
    var value = ParseString(args, "--profile", "extreme");
    return value.ToLowerInvariant() switch
    {
        "basic" or "base" or "balanced" or "balance" => PrecisionMode.Balanced,
        "extreme" or "ultra" or "ultralowjitter" or "ultra-low-jitter" => PrecisionMode.UltraLowJitter,
        "highperformance" or "high-performance" or "high" or "extremeduringplayback" => PrecisionMode.ExtremeDuringPlayback,
        _ => PrecisionMode.ExtremeDuringPlayback
    };
}

static ProbeBackend ParseBackend(string[] args)
{
    var value = ParseString(args, "--backend", "auto");
    return value.ToLowerInvariant() switch
    {
        "native" => ProbeBackend.Native,
        "managed" => ProbeBackend.Managed,
        _ => ProbeBackend.Auto
    };
}

static NativePlaybackEngineMode ParseNativeEngineMode(string[] args)
{
    var value = ParseString(args, "--native-engine", "auto");
    return value.ToLowerInvariant() switch
    {
        "standby" => NativePlaybackEngineMode.Standby,
        "inline" => NativePlaybackEngineMode.Inline,
        _ => NativePlaybackEngineMode.Auto
    };
}

static bool ShouldUseNativeProbe(ProbeBackend backend, PrecisionMode profile, bool legacy)
{
    if (legacy || !OperatingSystem.IsWindows())
    {
        return false;
    }

    return backend == ProbeBackend.Native
        || (backend == ProbeBackend.Auto && profile is PrecisionMode.ExtremeDuringPlayback or PrecisionMode.UltraLowJitter);
}

static string ToProfileText(PrecisionMode mode) => mode switch
{
    PrecisionMode.Balanced => "basic",
    PrecisionMode.UltraLowJitter => "extreme",
    _ => "highPerformance"
};

static string ToBackendText(ProbeBackend backend) => backend switch
{
    ProbeBackend.Managed => "managed",
    ProbeBackend.Native => "native",
    _ => "auto"
};

static string ToNativeEngineText(NativePlaybackEngineMode mode) => mode switch
{
    NativePlaybackEngineMode.Standby => "standby",
    NativePlaybackEngineMode.Inline => "inline",
    _ => "auto"
};

static bool HasFlag(string[] args, string name)
{
    return args.Any(arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));
}

static string ParseString(string[] args, string name, string defaultValue)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
        {
            return args[i + 1];
        }
    }

    return defaultValue;
}

static ulong? ParseNullableUInt64(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (!string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        var raw = args[i + 1];
        if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return Convert.ToUInt64(raw[2..], 16);
        }

        return Convert.ToUInt64(raw, 10);
    }

    return null;
}

static void ApplyAffinityMaskIfRequested(ulong? affinityMask)
{
    if (!affinityMask.HasValue)
    {
        return;
    }

    if (!OperatingSystem.IsWindows())
    {
        Console.WriteLine("affinityMask ignored: not running on Windows");
        return;
    }

    if (affinityMask.Value == 0 || affinityMask.Value > long.MaxValue)
    {
        Console.WriteLine($"affinityMask ignored: invalid value 0x{affinityMask.Value:X}");
        return;
    }

    using var process = Process.GetCurrentProcess();
    process.ProcessorAffinity = new IntPtr(unchecked((long)affinityMask.Value));
    Console.WriteLine($"affinityMask applied=0x{affinityMask.Value:X}");
}

static int ParseInt(string[] args, string name, int defaultValue)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(args[i + 1], out var value)
            && value >= 0)
        {
            return value;
        }
    }

    return defaultValue;
}

internal sealed class ProbeSeries
{
    private readonly List<long> values = [];

    public void Record(long microseconds)
    {
        values.Add(Math.Max(0, microseconds));
    }

    public int CountOver(long thresholdMicroseconds)
    {
        return values.Count(value => value > thresholdMicroseconds);
    }

    public string Summary()
    {
        if (values.Count == 0)
        {
            return "count=0 p50=0us p95=0us p99=0us p99.9=0us max=0us";
        }

        var sorted = values.Order().ToArray();
        return $"count={sorted.Length} p50={Percentile(sorted, 0.50)}us p95={Percentile(sorted, 0.95)}us p99={Percentile(sorted, 0.99)}us p99.9={Percentile(sorted, 0.999)}us max={sorted[^1]}us";
    }

    private static long Percentile(long[] sorted, double percentile)
    {
        var index = (int)Math.Floor(Math.Clamp(percentile, 0, 1) * (sorted.Length - 1));
        return sorted[index];
    }
}

internal enum ProbeBackend
{
    Auto,
    Managed,
    Native
}
