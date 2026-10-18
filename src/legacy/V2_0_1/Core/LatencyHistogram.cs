namespace MacroHid.Core;

public sealed class LatencyHistogram
{
    private readonly List<long> samples = [];

    public int Count => samples.Count;

    public void RecordMicroseconds(long microseconds)
    {
        if (microseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(microseconds), "Latency cannot be negative.");
        }

        samples.Add(microseconds);
    }

    public long PercentileMicroseconds(double percentile)
    {
        if (samples.Count == 0)
        {
            return 0;
        }

        if (percentile < 0 || percentile > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(percentile), "Percentile must be between 0 and 1.");
        }

        var sorted = samples.Order().ToArray();
        var index = (int)Math.Floor(percentile * (sorted.Length - 1));
        return sorted[index];
    }

    public string Summary()
    {
        return $"count={Count} p50={PercentileMicroseconds(0.50)}us p95={PercentileMicroseconds(0.95)}us p99={PercentileMicroseconds(0.99)}us";
    }
}
