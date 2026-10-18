using MacroHid.Core;

namespace MacroHid.Runtime;

public sealed class PlaybackTimingRecorder
{
    private readonly LatencyHistogram absoluteJitter = new();
    private long minJitter = long.MaxValue;
    private long maxJitter = long.MinValue;
    private int count;

    public void RecordJitter(long actualTick, long dueTick, long qpcFrequency)
    {
        var jitterUs = ToMicroseconds(actualTick - dueTick, qpcFrequency);
        minJitter = Math.Min(minJitter, jitterUs);
        maxJitter = Math.Max(maxJitter, jitterUs);
        absoluteJitter.RecordMicroseconds(Math.Abs(jitterUs));
        count++;
    }

    public PlaybackTimingStats ToStats()
    {
        return count == 0
            ? new PlaybackTimingStats(0, 0, 0, "count=0 p50=0us p95=0us p99=0us")
            : new PlaybackTimingStats(count, minJitter, maxJitter, absoluteJitter.Summary());
    }

    private static long ToMicroseconds(long ticks, long qpcFrequency)
    {
        return qpcFrequency <= 0 ? 0 : (long)Math.Round(ticks * 1_000_000.0 / qpcFrequency, MidpointRounding.AwayFromZero);
    }
}
