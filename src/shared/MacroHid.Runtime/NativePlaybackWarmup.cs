using MacroHid.Core;

namespace MacroHid.Runtime;

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

        TryWarmUp(scanCpu: true);
    }

    public static void TryWarmUp(bool scanCpu)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
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

            var status = NativePlaybackInterop.MhpWarmEngine(ref options, out _);
            if (scanCpu && status == MhpStatus.Ok)
            {
                Volatile.Write(ref cpuScanReady, 1);
            }
        }
        catch
        {
            // Warm-up is opportunistic. Playback will fall back to managed ultra if native is unavailable.
        }

    }

    public static void QueueWarmUpForAffinityMask(string? affinityMask)
    {
        var normalizedMask = PlaybackAffinityMask.NormalizeOrThrow(affinityMask);
        if (string.Equals(normalizedMask, warmedAffinityMask, StringComparison.OrdinalIgnoreCase))
        {
            return;
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
