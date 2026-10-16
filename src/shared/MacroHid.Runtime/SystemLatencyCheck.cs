namespace MacroHid.Runtime;

/// <summary>One logical processor's stall probe result.</summary>
/// <param name="StallsOver50Us">Times the spinning probe thread was kept off the core for more than 50 us.</param>
/// <param name="MaxStallUs">Longest such period: what an unlucky deadline on this core would be late by.</param>
public sealed record CoreStallResult(
    int Processor, int PhysicalCore, int EfficiencyClass,
    int StallsOver50Us, int StallsOver200Us, long MaxStallUs, bool Noisy);

public sealed record SystemLatencyReport(
    IReadOnlyList<CoreStallResult> Cores, int WindowMs, DateTimeOffset MeasuredAt)
{
    public IReadOnlyList<CoreStallResult> NoisyCores => Cores.Where(core => core.Noisy).ToList();
    public IReadOnlyList<CoreStallResult> QuietCores => Cores.Where(core => !core.Noisy).ToList();

    /// <summary>Mask of quiet processors, or 0 when fewer than two are quiet (then nothing is restricted).</summary>
    public ulong QuietMask
    {
        get
        {
            var quiet = QuietCores;
            if (quiet.Count < 2 || quiet.Any(core => core.Processor >= 64)) return 0;
            return quiet.Aggregate(0UL, (mask, core) => mask | (1UL << core.Processor));
        }
    }

    public long WorstQuietStallUs => QuietCores.Count == 0 ? 0 : QuietCores.Max(core => core.MaxStallUs);
}

/// <summary>
/// "System latency check": spins on each logical processor in turn and records the periods the thread could not run there
/// (interrupts, DPCs, firmware SMIs, preemption). Those periods are what produce the rare maximum outliers; they cluster on a
/// few interrupt-heavy cores (CPU 0 by default). The result feeds automatic core avoidance for every profile.
/// Changes nothing in the system: no registry, no driver, no interrupt routing.
/// </summary>
public static class SystemLatencyCheck
{
    private static readonly object Gate = new();
    private static SystemLatencyReport? last;
    // Recent per-processor "noisy" votes; a core is avoided when it was noisy in 2 of the last 3 checks, so a single
    // one-off stall (a 2 ms event on an otherwise quiet core) does not move playback around.
    private static readonly Queue<HashSet<int>> recentNoisy = new();

    public static SystemLatencyReport? Last { get { lock (Gate) return last; } }

    /// <summary>Processors avoided now: noisy in at least 2 of the last 3 checks (or in the only check so far).</summary>
    public static IReadOnlySet<int> StableNoisy
    {
        get
        {
            lock (Gate)
            {
                if (recentNoisy.Count == 0) return new HashSet<int>();
                var need = recentNoisy.Count == 1 ? 1 : 2;
                return recentNoisy.SelectMany(set => set).GroupBy(cpu => cpu)
                    .Where(group => group.Count() >= need).Select(group => group.Key).ToHashSet();
            }
        }
    }

    /// <summary>Mask of processors not in <see cref="StableNoisy"/>, or 0 when fewer than two remain.</summary>
    public static ulong StableQuietMask
    {
        get
        {
            var report = Last;
            if (report is null) return 0;
            var noisy = StableNoisy;
            var quiet = report.Cores.Where(core => !noisy.Contains(core.Processor) && core.Processor < 64).ToList();
            return quiet.Count < 2 ? 0 : quiet.Aggregate(0UL, (mask, core) => mask | (1UL << core.Processor));
        }
    }

    /// <param name="windowMs">Spin time per core (20..2000). 100 ms per core takes ~3 s on 32 logical processors.</param>
    /// <param name="progress">(processor, completed, total, maxStallUs or -1 when starting that core).</param>
    /// <param name="applyQuietCores">Use the quiet cores as the soft affinity of the playback workers.</param>
    public static SystemLatencyReport? Run(int windowMs, Action<int, int, int, long>? progress = null, bool applyQuietCores = true)
    {
        lock (Gate)
        {
            try
            {
                var buffer = new MhpCoreStall[Math.Max(1, Environment.ProcessorCount)];
                NativePlaybackInterop.CoreProgressCallback? callback = progress is null ? null
                    : (processor, completed, total, value) => progress((int)processor, completed, total, value);
                var status = NativePlaybackInterop.MhpScanCoreStalls(0, (uint)Math.Clamp(windowMs, 20, 2000), buffer,
                    (uint)buffer.Length, out var written, callback);
                GC.KeepAlive(callback);
                if (status != MhpStatus.Ok) return null;
                var cores = buffer.Take((int)Math.Min(written, (uint)buffer.Length)).Select(core => new CoreStallResult(
                    (int)core.ProcessorNumber, (int)core.PhysicalCoreId, (int)core.EfficiencyClass,
                    (int)core.StallsOver50Us, (int)core.StallsOver200Us, core.MaxStallUs, core.Noisy != 0)).ToList();
                last = new SystemLatencyReport(cores, windowMs, DateTimeOffset.Now);
                recentNoisy.Enqueue(last.NoisyCores.Select(core => core.Processor).ToHashSet());
                while (recentNoisy.Count > 3) recentNoisy.Dequeue();
                if (applyQuietCores) ApplyQuietCores(StableQuietMask);
                AppLog.Info("LatencyCheck", $"window={windowMs}ms cores={cores.Count} noisy=[{string.Join(",", last.NoisyCores.Select(c => c.Processor))}] " +
                    $"avoided=[{string.Join(",", StableNoisy.Order())}] worstQuietStall={last.WorstQuietStallUs}us");
                return last;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                return null;
            }
        }
    }

    public static void ApplyQuietCores(ulong mask)
    {
        try { NativePlaybackInterop.MhpSetQuietCores(mask); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { }
    }

    public static ulong CurrentQuietMask
    {
        get
        {
            try { return NativePlaybackInterop.MhpGetQuietCores(); }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { return 0; }
        }
    }

    /// <summary>Background check at start-up: three short passes a few seconds apart so avoidance uses the stable vote.</summary>
    public static void QueueStartupCheck(int windowMs = 60)
    {
        new Thread(() =>
        {
            Thread.Sleep(3000); // after warm-up and window creation
            for (var pass = 0; pass < 3; pass++)
            {
                Run(windowMs);
                Thread.Sleep(2000);
            }
        }) { IsBackground = true, Name = "MacroHID-LatencyCheck", Priority = ThreadPriority.BelowNormal }.Start();
    }
}
