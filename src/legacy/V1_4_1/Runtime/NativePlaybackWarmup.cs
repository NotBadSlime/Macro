using MacroHid.Core;

namespace MacroHid.Runtime;

public readonly record struct CoreMeasureResult(
    bool Measured,
    int PrimaryProcessor,
    int SecondaryProcessor,
    long PrimaryMaxLateUs,
    long SecondaryMaxLateUs)
{
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
    private static int cpuScanReady;
    private static string warmedAffinityMask = string.Empty;

    public static bool CpuScanReady => Volatile.Read(ref cpuScanReady) != 0;

    public static void TryWarmUp()
    {
        TryWarmUp(scanCpu: false);
    }

    public static void QueueWarmUpForPrecision(PrecisionMode precision, string? affinityMask)
    {
        _ = Task.Run(() => TryWarmUpForPrecision(precision, affinityMask));
    }

    public static void TryWarmUpForPrecision(PrecisionMode precision, string? affinityMask)
    {
        if (precision != PrecisionMode.UltraLowJitter)
        {
            TryWarmUp(scanCpu: false);
            return;
        }

        var normalizedMask = PlaybackAffinityMask.NormalizeOrThrow(affinityMask);
        if (!string.IsNullOrWhiteSpace(normalizedMask))
        {
            TryWarmUpForAffinityMask(normalizedMask);
            return;
        }

        if (CpuScanReady)
        {
            return;
        }

        TryWarmUp(scanCpu: false);
    }

    public static void TryWarmUp(bool scanCpu)
    {
        TryWarmUp(scanCpu, out _);
    }

    public static CoreMeasureResult MeasureCheckedCores(string? affinityMask)
    {
        var normalizedMask = PlaybackAffinityMask.NormalizeOrThrow(affinityMask);
        if (string.IsNullOrWhiteSpace(normalizedMask) || !OperatingSystem.IsWindows())
        {
            return default;
        }

        try
        {
            using var affinityScope = ProcessAffinityScope.TryEnter(normalizedMask);
            Shutdown();
            var measured = TryWarmUp(scanCpu: true, out var stats);
            if (measured)
            {
                warmedAffinityMask = normalizedMask;
            }

            return CoreMeasureResult.From(stats, measured);
        }
        catch
        {
            return default;
        }
    }

    private static bool TryWarmUp(bool scanCpu, out MhpRunStats stats)
    {
        stats = default;
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            var options = new MhpRunOptions
            {
                PrecisionMode = 2,
                EnableCpuScan = scanCpu ? 1 : 0,
                OutlierThresholdUs = 250,
                LoopStepCount = 0,
                QpcFrequency = System.Diagnostics.Stopwatch.Frequency,
                NativeEngineMode = (int)NativeEngineMode.Standby
            };

            var status = NativePlaybackInterop.MhpWarmEngine(ref options, out stats);
            if (scanCpu && status == MhpStatus.Ok)
            {
                Volatile.Write(ref cpuScanReady, 1);
            }

            return scanCpu && status == MhpStatus.Ok;
        }
        catch
        {
            return false;
        }
    }

    public static void QueueWarmUpForAffinityMask(string? affinityMask)
    {
        QueueWarmUpForAffinityMask(affinityMask, force: false);
    }

    public static void QueueWarmUpForAffinityMask(string? affinityMask, bool force)
    {
        var normalizedMask = PlaybackAffinityMask.NormalizeOrThrow(affinityMask);
        if (!force && string.Equals(normalizedMask, warmedAffinityMask, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (force)
        {
            warmedAffinityMask = string.Empty;
        }

        _ = Task.Run(() => TryWarmUpForAffinityMask(normalizedMask));
    }

    public static void TryWarmUpForAffinityMask(string? affinityMask)
    {
        var normalizedMask = PlaybackAffinityMask.NormalizeOrThrow(affinityMask);
        if (string.Equals(normalizedMask, warmedAffinityMask, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            using var affinityScope = ProcessAffinityScope.TryEnter(normalizedMask);
            Shutdown();
            TryWarmUp(scanCpu: true);
            if (CpuScanReady)
            {
                warmedAffinityMask = normalizedMask;
            }
        }
        catch
        {
            // Affinity-specific warm-up is opportunistic. Playback can still use the generic standby engine or managed fallback.
        }
    }

    public static void Shutdown()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            NativePlaybackInterop.MhpShutdownEngine();
            Volatile.Write(ref cpuScanReady, 0);
            warmedAffinityMask = string.Empty;
        }
        catch
        {
            // Shutdown is best effort; process teardown will release the DLL resources.
        }
    }
}
