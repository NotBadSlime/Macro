using MacroHid.Core;

namespace MacroHid.Runtime;

public readonly record struct CoreSample(int ProcessorNumber, long MaxLateUs);

public readonly record struct CoreMeasureResult(
    bool Measured,
    int PrimaryProcessor,
    int SecondaryProcessor,
    long PrimaryMaxLateUs,
    long SecondaryMaxLateUs)
{
    public IReadOnlyList<CoreSample> Samples { get; init; } = [];

    internal static CoreMeasureResult From(MhpRunStats stats, bool statusOk)
    {
        var primary = ToProcessor(stats.SelectedWorker0);
        var secondary = ToProcessor(stats.SelectedWorker1);
        var primaryLate = stats.SelectedWorker0MaxLateUs;
        var secondaryLate = stats.SelectedWorker1MaxLateUs;
        var measured = statusOk
            && primary >= 0
            && primaryLate >= 0
            && primaryLate != long.MaxValue;
        return new CoreMeasureResult(measured, primary, secondary, primaryLate, secondaryLate);
    }

    private static int ToProcessor(uint value) => value == uint.MaxValue ? -1 : checked((int)value);
}

public static class NativePlaybackWarmup
{
    private static readonly object Gate = new();
    private static int cpuScanReady;
    public static bool CpuScanReady => Volatile.Read(ref cpuScanReady) != 0;
    public static void TryWarmUp() => TryWarmUp(false);
    public static void TryWarmUp(bool scanCpu) => Prepare(scanCpu, "", out _);
    // The generation the engine is prepared for. Prepare() has no other way to know it: it is also reached from the core
    // picker and the affinity-mask paths, which do not carry run options. Set whenever a precision warm-up is requested.
    private static int preparedGeneration;
    public static PrecisionGeneration Generation => (PrecisionGeneration)Volatile.Read(ref preparedGeneration);
    // Native profile (1 = 均衡, 2 = 极限) the idle workers are configured for. The core picker and affinity-mask
    // paths keep the last one requested, so they do not flip a 均衡 user's workers into MMCSS/affinity.
    private static int preparedNativePrecision = (int)NativePrecisionMode.ExtremeDuringPlayback;
    private enum NativePrecisionMode { ExtremeDuringPlayback = 1, UltraLowJitter = 2 }
    public static void QueueWarmUpForPrecision(PrecisionMode precision, string? affinityMask, PrecisionGeneration generation = PrecisionGeneration.Latest)
        => _ = Task.Run(() => TryWarmUpForPrecision(precision, affinityMask, generation));
    public static void TryWarmUpForPrecision(PrecisionMode precision, string? affinityMask, PrecisionGeneration generation = PrecisionGeneration.Latest)
        => PrepareForPrecision(precision, affinityMask, generation);
    public static bool PrepareForPrecision(PrecisionMode precision, string? affinityMask, PrecisionGeneration generation = PrecisionGeneration.Latest)
    {
        Volatile.Write(ref preparedGeneration, (int)PrecisionGenerations.Normalize(generation));
        QpcPlaybackDelayStrategy.Prepare(precision);
        if (!PlaybackPerformancePolicy.For(precision).PreferNative)
        {
            PlaybackRuntimeStatus.SetPreparation(new(false, true, "managed", ""));
            return false;
        }
        if (PrecisionGenerations.UsesLegacyNativeEngine(Generation)) return PrepareForPrecisionV1_6(precision, affinityMask);
        Volatile.Write(ref preparedNativePrecision, (int)(precision == PrecisionMode.UltraLowJitter
            ? NativePrecisionMode.UltraLowJitter : NativePrecisionMode.ExtremeDuringPlayback));
        return Prepare(precision == PrecisionMode.UltraLowJitter, affinityMask, out _);
    }
    private static bool Prepare(bool scan, string? maskText, out MhpRunStats stats)
    {
        lock (Gate)
        {
            stats = default;
            PlaybackRuntimeStatus.SetPreparation(new(true, false, "managed", "原生初始化 / 核心准备"));
            try
            {
                PlaybackAffinityMask.TryParse(PlaybackAffinityMask.NormalizeOrThrow(maskText), out var mask);
                var options = new MhpRunOptions { PrecisionMode = Volatile.Read(ref preparedNativePrecision), EnableCpuScan = scan ? 1 : 0,
                    QpcFrequency = System.Diagnostics.Stopwatch.Frequency, NativeEngineMode = (int)NativeEngineMode.Standby,
                    Generation = (uint)Generation,
                    Flags = Volatile.Read(ref preparedNativePrecision) == (int)NativePrecisionMode.UltraLowJitter && MaxPreemption.Enabled ? MaxPreemption.MhpFlagMaxPreemption : 0u };
                var status = NativePlaybackInterop.MhpPrepareEngine(ref options, mask, out stats);
                bool measured = CoreMeasureResult.From(stats, status == MhpStatus.Ok).Measured;
                Volatile.Write(ref cpuScanReady, measured ? 1 : 0);
                string reason = status != MhpStatus.Ok ? NativePlaybackInterop.GetLastErrorText()
                    : scan && !measured ? "测核尚未完成；正在运行的宏不受影响" : "";
                PlaybackRuntimeStatus.SetPreparation(new(false, status == MhpStatus.Ok,
                    status != MhpStatus.Ok ? "managed" : PrecisionGenerations.UsesLegacyNativeEngine(Generation) ? "native-standby" : "native-shared", reason));
                return status == MhpStatus.Ok;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or FormatException)
            {
                PlaybackRuntimeStatus.SetPreparation(new(false, true, "managed", ex.Message));
                return false;
            }
        }
    }
    // ---- MacroHID 1.6.0 warm-up (precision generation 1.6.0) ----
    // 1.6.0 warmed the standby workers unpinned (no core scan) except for 极致 with a core mask: then it restarted the
    // engine inside that process affinity and scanned, so the workers were picked from the masked cores. Without a mask,
    // 极致 kept an earlier scan or warmed unpinned. The scan flag was set only by a successful scan and kept otherwise.
    private static string v16WarmedMask = string.Empty;

    private static bool PrepareForPrecisionV1_6(PrecisionMode precision, string? affinityMask)
    {
        if (precision != PrecisionMode.UltraLowJitter) return PrepareV1_6(scan: false, "", force: false, out _);
        var normalized = NormalizeMaskOrEmpty(affinityMask);
        if (normalized.Length > 0) return PrepareV1_6(scan: true, normalized, force: false, out _);
        return CpuScanReady || PrepareV1_6(scan: false, "", force: false, out _);
    }

    private static string NormalizeMaskOrEmpty(string? maskText)
    {
        try { return PlaybackAffinityMask.NormalizeOrThrow(maskText) ?? string.Empty; }
        catch (FormatException) { return string.Empty; }
    }

    private static bool PrepareV1_6(bool scan, string normalizedMask, bool force, out MhpRunStats stats)
    {
        lock (Gate)
        {
            stats = default;
            var masked = scan && normalizedMask.Length > 0;
            if (masked && !force && string.Equals(normalizedMask, v16WarmedMask, StringComparison.OrdinalIgnoreCase))
                return true;
            PlaybackRuntimeStatus.SetPreparation(new(true, false, "managed", "原生初始化 / 核心准备"));
            try
            {
                using var affinityScope = masked ? ProcessAffinityScope.TryEnter(normalizedMask) : null;
                if (masked)
                {
                    // A warmed standby engine keeps its workers; restart it so the scan runs on the masked cores.
                    NativePlaybackInterop.MhpShutdownEngine();
                    Volatile.Write(ref cpuScanReady, 0);
                    v16WarmedMask = string.Empty;
                }

                var options = new MhpRunOptions
                {
                    PrecisionMode = 2,
                    EnableCpuScan = scan ? 1 : 0,
                    OutlierThresholdUs = 250,
                    QpcFrequency = System.Diagnostics.Stopwatch.Frequency,
                    NativeEngineMode = (int)NativeEngineMode.Standby,
                    Generation = (uint)PrecisionGeneration.V1_6
                };
                var status = NativePlaybackInterop.MhpWarmEngine(ref options, out stats);
                if (scan && status == MhpStatus.Ok)
                {
                    Volatile.Write(ref cpuScanReady, 1);
                    if (masked) v16WarmedMask = normalizedMask;
                }
                PlaybackRuntimeStatus.SetPreparation(new(false, status == MhpStatus.Ok,
                    status == MhpStatus.Ok ? "native-standby" : "managed",
                    status == MhpStatus.Ok ? "" : NativePlaybackInterop.GetLastErrorText()));
                return status == MhpStatus.Ok;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                PlaybackRuntimeStatus.SetPreparation(new(false, true, "managed", ex.Message));
                return false;
            }
        }
    }

    public static CoreMeasureResult MeasureCheckedCores(string? affinityMask) => MeasureCheckedCores(affinityMask, null);
    public static CoreMeasureResult MeasureCheckedCores(string? affinityMask, Action<int, int, int, long>? progress)
    {
        var v16 = PrecisionGenerations.UsesLegacyNativeEngine(Generation);
        MhpRunStats stats;
        var ok = v16
            ? PrepareV1_6(true, NormalizeMaskOrEmpty(affinityMask), force: true, out stats)
            : Prepare(true, affinityMask, out stats);
        var result = CoreMeasureResult.From(stats, ok);
        var samples = new List<CoreSample>();
        if (result.Measured)
        {
            PlaybackAffinityMask.TryParse(affinityMask, out var mask);
            var buffer = new MhpCoreSample[64];
            NativePlaybackInterop.CoreProgressCallback? callback = progress is null ? null
                : (processor, completed, total, late) => progress((int)processor, completed, total, late);
            NativePlaybackInterop.MhpScanCoresOnMask(mask, buffer, (uint)buffer.Length, out var written, callback);
            for (int i = 0; i < Math.Min(written, (uint)buffer.Length); i++) samples.Add(new((int)buffer[i].ProcessorNumber, buffer[i].MaxLateUs));
        }
        return result with { Samples = samples };
    }
    public static void QueueWarmUpForAffinityMask(string? mask) => QueueWarmUpForAffinityMask(mask, false);
    public static void QueueWarmUpForAffinityMask(string? mask, bool force) => _ = Task.Run(() => TryWarmUpForAffinityMask(mask, force));
    public static void TryWarmUpForAffinityMask(string? mask) => TryWarmUpForAffinityMask(mask, false);
    private static void TryWarmUpForAffinityMask(string? mask, bool force)
    {
        if (PrecisionGenerations.UsesLegacyNativeEngine(Generation)) PrepareV1_6(true, NormalizeMaskOrEmpty(mask), force, out _);
        else Prepare(true, mask, out _);
    }
    public static void Shutdown()
    {
        lock (Gate)
        {
            try { NativePlaybackInterop.MhpShutdownEngine(); }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { }
            Volatile.Write(ref cpuScanReady, 0);
            v16WarmedMask = string.Empty;
        }
    }
}
