namespace MacroHid.Runtime;

internal static class BandwidthPacer
{
    public static long Charge(ref long nextTick, long now, int length, long bytesPerSecond, long frequency)
    {
        if (bytesPerSecond < 1)
        {
            bytesPerSecond = 1;
        }

        if (length < 1)
        {
            length = 1;
        }

        if (frequency < 1)
        {
            frequency = 1;
        }

        if (nextTick < now)
        {
            nextTick = now;
        }

        var release = nextTick;
        var ticks = length * frequency / bytesPerSecond;
        if (ticks < 1)
        {
            ticks = 1;
        }

        nextTick += ticks;
        return release;
    }
}
