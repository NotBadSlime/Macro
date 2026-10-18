namespace MacroHid.Runtime;

public interface IHighResolutionClock
{
    long Frequency { get; }

    long GetTimestamp();
}

public sealed class QpcHighResolutionClock : IHighResolutionClock
{
    public QpcHighResolutionClock()
    {
        RuntimeNativeMethods.QueryPerformanceFrequency(out var frequency);
        Frequency = frequency;
    }

    public long Frequency { get; }

    public long GetTimestamp()
    {
        RuntimeNativeMethods.QueryPerformanceCounter(out var timestamp);
        return timestamp;
    }
}
