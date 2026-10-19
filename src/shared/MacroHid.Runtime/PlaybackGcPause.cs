using System.Runtime;
using MacroHid.Core;

namespace MacroHid.Runtime;

/// <summary>User-adjustable rules of no-GC playback (高级设置). <see cref="Normalize"/> clamps every field.</summary>
public sealed record PlaybackGcOptions(
    // Macros whose single iteration is written as at most this many seconds play without GC. 0 = off.
    int MaxIterationSeconds = PlaybackGcOptions.DefaultMaxIterationSeconds,
    // Allocation budget reserved by the region. Committed while held; allocating past it ends the region (normal GC).
    int BudgetMegabytes = PlaybackGcOptions.DefaultBudgetMegabytes,
    // A loop renews the region only when at least this much time is left before the next iteration.
    double RenewSlackMilliseconds = PlaybackGcOptions.DefaultRenewSlackMilliseconds,
    // Renew when this share of the budget is used (1-100 %).
    int RenewAtBudgetPercent = PlaybackGcOptions.DefaultRenewAtBudgetPercent,
    // Re-enter at the next iteration end after the region was lost to a budget overrun.
    bool RenewAfterLoss = true,
    // Also renew while condition monitors run (their evaluation then may pause for the collection).
    bool RenewWhileConditionsRun = false)
{
    public const int DefaultMaxIterationSeconds = 120;
    public const int UpperMaxIterationSeconds = 3600;
    public const int DefaultBudgetMegabytes = 32;
    public const int MinBudgetMegabytes = 4;
    public const int MaxBudgetMegabytes = 1024;
    public const double DefaultRenewSlackMilliseconds = 3;
    public const double MaxRenewSlackMilliseconds = 1000;
    public const int DefaultRenewAtBudgetPercent = 50;

    public static PlaybackGcOptions Default { get; } = new();

    public PlaybackGcOptions Normalize() => this with
    {
        MaxIterationSeconds = Math.Clamp(MaxIterationSeconds, 0, UpperMaxIterationSeconds),
        BudgetMegabytes = Math.Clamp(BudgetMegabytes, MinBudgetMegabytes, MaxBudgetMegabytes),
        RenewSlackMilliseconds = double.IsFinite(RenewSlackMilliseconds)
            ? Math.Round(Math.Clamp(RenewSlackMilliseconds, 0, MaxRenewSlackMilliseconds), 3) : DefaultRenewSlackMilliseconds,
        RenewAtBudgetPercent = Math.Clamp(RenewAtBudgetPercent, 1, 100)
    };
}

/// <summary>
/// No-GC playback (2.1). While a 极致/极限 (and Max) macro plays, the process holds a .NET no-GC region, so no collection
/// suspends the managed threads on the playback path: hotkey hand-off, iteration hand-off, the managed batch loop and condition
/// monitors. The native workers that send the batches are not managed threads and were never paused by a GC.
///
/// Entering a region can itself run a collection (measured up to ~30 ms with a cold 64 MB budget), so it is not entered in front
/// of the first input: native runs enter it right after their plan was accepted (the native workers are already sending), the
/// managed batch loop after its first batch. Which macros qualify and when a loop may collect come from
/// <see cref="PlaybackGcOptions"/> (高级设置): in the idle time after an iteration's last batch the region is ended and re-entered
/// (the collection runs there) when enough time is left, condition monitors allow it, and the budget share is used or the region
/// was lost. Overrunning the budget ends the region by itself (the runtime then collects normally).
/// MACROHID_NO_GC_PLAYBACK=0 turns it off for A/B runs.
/// </summary>
public static class PlaybackGcPause
{
    private static PlaybackGcOptions options = PlaybackGcOptions.Default;

    public static PlaybackGcOptions Options
    {
        get => Volatile.Read(ref options);
        set => Volatile.Write(ref options, (value ?? PlaybackGcOptions.Default).Normalize());
    }

    private static readonly object Gate = new();
    private static readonly bool Disabled = Environment.GetEnvironmentVariable("MACROHID_NO_GC_PLAYBACK") == "0";
    private static int wanted;
    private static bool active;
    private static long enteredAtAllocated, enteredBudget;
    private static int entries, losses;

    /// <summary>Region entries and regions lost to a budget overrun since start-up (diagnostics/tests).</summary>
    public static (int Entries, int Losses) Counters { get { lock (Gate) return (entries, losses); } }

    public static bool Holding { get { lock (Gate) return active && GCSettings.LatencyMode == GCLatencyMode.NoGCRegion; } }

    /// <summary>
    /// Starts wanting a region for this run (nothing is collected yet). Null when the run does not qualify: Balanced, the 1.6.0
    /// generation (it keeps its own per-run region), an iteration longer than the configured seconds, off, or disabled.
    /// </summary>
    public static IDisposable? Begin(PrecisionMode precision, PrecisionGeneration generation, long iterationTicks, long qpcFrequency)
    {
        if (Disabled || precision is not (PrecisionMode.ExtremeDuringPlayback or PrecisionMode.UltraLowJitter)) return null;
        if (PrecisionGenerations.UsesLegacyNativeEngine(PrecisionGenerations.Normalize(generation))) return null;
        var limit = Options.MaxIterationSeconds;
        if (limit <= 0 || qpcFrequency <= 0 || iterationTicks > qpcFrequency * limit) return null;
        lock (Gate) wanted++;
        return new Scope();
    }

    /// <summary>The first input is on its way (native plan accepted, or first managed batch sent): enter the region now.</summary>
    public static void AfterFirstSubmit()
    {
        if (Volatile.Read(ref wanted) == 0) return;
        lock (Gate) { if (wanted > 0 && !active) EnterLocked(); }
    }

    /// <summary>
    /// Idle time after an iteration's last batch, before the next iteration. Renews the region (the collection runs here) under the
    /// configured rules. Returns whether it renewed.
    /// </summary>
    public static bool RenewAtIterationEnd(long slackTicks, long qpcFrequency, bool conditionsRunning)
    {
        if (Volatile.Read(ref wanted) == 0) return false;
        var o = Options;
        if (conditionsRunning && !o.RenewWhileConditionsRun) return false;
        if (slackTicks < (long)(qpcFrequency * o.RenewSlackMilliseconds / 1000)) return false;
        lock (Gate)
        {
            if (wanted == 0) return false;
            var lost = !active || GCSettings.LatencyMode != GCLatencyMode.NoGCRegion;
            if (lost && !o.RenewAfterLoss) return false;
            if (!lost && GC.GetTotalAllocatedBytes(false) - enteredAtAllocated < enteredBudget * o.RenewAtBudgetPercent / 100) return false;
            EndLocked();
            EnterLocked();
            return true;
        }
    }

    private static void EnterLocked()
    {
        // Another owner (the 1.6.0 generation's per-run region) already holds one: leave it alone.
        if (GCSettings.LatencyMode == GCLatencyMode.NoGCRegion) return;
        var budget = (long)Options.BudgetMegabytes << 20;
        try
        {
            // disallowFullBlockingGC: if the budget cannot be found without a full blocking collection, do not enter.
            active = GC.TryStartNoGCRegion(budget, disallowFullBlockingGC: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentOutOfRangeException) { active = false; }
        if (!active) return;
        entries++;
        enteredBudget = budget;
        enteredAtAllocated = GC.GetTotalAllocatedBytes(false);
    }

    private static void EndLocked()
    {
        if (!active) return;
        active = false;
        if (GCSettings.LatencyMode != GCLatencyMode.NoGCRegion) { losses++; return; }
        // Throws when a collection already happened inside the region (budget overrun).
        try { GC.EndNoGCRegion(); } catch (InvalidOperationException) { losses++; }
    }

    private sealed class Scope : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            lock (Gate) { if (--wanted == 0) EndLocked(); }
        }
    }
}
