using System.Diagnostics;

namespace MacroHid.Runtime;

// A short coalescing window, not a cache of old screen state across polls.
internal sealed class RecentEvaluationCache<TKey, TValue> where TKey : notnull
{
    private readonly object gate = new();
    private readonly Dictionary<TKey, Entry> entries = new();
    private readonly long freshnessTicks;
    internal RecentEvaluationCache(TimeSpan? freshness = null)
        => freshnessTicks = Math.Max(1, (long)((freshness ?? TimeSpan.FromMilliseconds(1)).TotalSeconds * Stopwatch.Frequency));
    internal TValue Get(TKey key, Func<TValue> produce, CancellationToken token = default)
    {
        Entry entry;
        lock (gate)
        {
            if (!entries.TryGetValue(key, out entry!))
            {
                if (entries.Count >= 16) entries.Clear();
                entries.Add(key, entry = new());
            }
        }
        entry.Work.Wait(token);
        try
        {
            token.ThrowIfCancellationRequested();
            var now = Stopwatch.GetTimestamp();
            if (entry.HasValue && now - entry.Completed < freshnessTicks) return entry.Value!;
            var value = produce();
            token.ThrowIfCancellationRequested();
            entry.Value = value;
            entry.Completed = Stopwatch.GetTimestamp();
            entry.HasValue = true;
            return value;
        }
        finally { entry.Work.Release(); }
    }
    private sealed class Entry
    {
        internal readonly SemaphoreSlim Work = new(1, 1);
        internal bool HasValue;
        internal long Completed;
        internal TValue? Value;
    }
}
