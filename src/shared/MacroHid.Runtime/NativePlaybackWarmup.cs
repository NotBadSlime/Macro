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
                var options = new MhpRunOptions { PrecisionMode = 2, EnableCpuScan = scan ? 1 : 0,
                    QpcFrequency = System.Diagnostics.Stopwatch.Frequency, NativeEngineMode = (int)NativeEngineMode.Standby,
                    Generation = (uint)Generation };
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
    public static CoreMeasureResult MeasureCheckedCores(string? affinityMask) => MeasureCheckedCores(affinityMask, null);
    public static CoreMeasureResult MeasureCheckedCores(string? affinityMask, Action<int, int, int, long>? progress)
    {
        var ok = Prepare(true, affinityMask, out var stats);
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
    public static void QueueWarmUpForAffinityMask(string? mask, bool force) => _ = Task.Run(() => TryWarmUpForAffinityMask(mask));
    public static void TryWarmUpForAffinityMask(string? mask) => Prepare(true, mask, out _);
    public static void Shutdown()
    {
        lock (Gate)
        {
            try { NativePlaybackInterop.MhpShutdownEngine(); }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { }
            Volatile.Write(ref cpuScanReady, 0);
        }
    }
}
