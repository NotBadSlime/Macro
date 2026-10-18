namespace MacroHid.Runtime;

/// <summary>
/// Fixed-capacity ring of hook-callback self times (QPC ticks). Single writer (the hook thread);
/// <see cref="Record"/> never allocates and is a no-op until <see cref="Enabled"/> is set.
/// Snapshots may be taken from another thread and can observe a torn window, which is acceptable for diagnostics.
/// </summary>
public sealed class HookSelfTimeRecorder
{
    private readonly long[] ticks;
    private long written;

    public HookSelfTimeRecorder(int capacity = 4096)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ticks = new long[capacity];
    }

    public volatile bool Enabled;

    public long TotalRecorded => Volatile.Read(ref written);

    public void Record(long startQpc, long endQpc)
    {
        if (!Enabled)
        {
            return;
        }

        var index = written;
        ticks[(int)(index % ticks.Length)] = Math.Max(0, endQpc - startQpc);
        Volatile.Write(ref written, index + 1);
    }

    public HookSelfTimeStats Snapshot(long qpcFrequency)
    {
        var count = (int)Math.Min(Volatile.Read(ref written), ticks.Length);
        if (count == 0 || qpcFrequency <= 0)
        {
            return new HookSelfTimeStats(0, 0, 0, 0, 0);
        }

        var sorted = new long[count];
        Array.Copy(ticks, sorted, count);
        Array.Sort(sorted);
        double ToUs(long t) => t * 1_000_000.0 / qpcFrequency;
        double Rank(double p) => ToUs(sorted[Math.Max(1, (int)Math.Ceiling(count * p)) - 1]);
        return new HookSelfTimeStats(count, Rank(0.5), Rank(0.99), Rank(0.999), ToUs(sorted[^1]));
    }

    public void Reset() => Volatile.Write(ref written, 0);
}

public readonly record struct HookSelfTimeStats(int Count, double P50Us, double P99Us, double P999Us, double MaxUs)
{
    public override string ToString() =>
        $"count={Count} p50={P50Us:F1}us p99={P99Us:F1}us p99.9={P999Us:F1}us max={MaxUs:F1}us";
}
