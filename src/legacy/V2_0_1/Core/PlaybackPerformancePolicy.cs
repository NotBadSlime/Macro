namespace MacroHid.Core;

/// <summary>Runtime policy; persisted PrecisionMode values remain unchanged.</summary>
public sealed record PlaybackPerformancePolicy(
    int FinalSpinMicroseconds, int TargetStartupMicroseconds, int TargetJitterMicroseconds,
    bool PreferNative, bool AllowHelper, int MaximumTimingWorkers = 2)
{
    private static readonly PlaybackPerformancePolicy Low = new(100, 1000, 500, false, false);
    private static readonly PlaybackPerformancePolicy Balanced = new(300, 500, 250, true, false);
    private static readonly PlaybackPerformancePolicy Extreme = new(1000, 250, 100, true, true);
    public ThreadPriority ExecutionThreadPriority => PreferNative ? ThreadPriority.Highest : ThreadPriority.AboveNormal;
    public static PlaybackPerformancePolicy For(PrecisionMode mode) => mode switch
    {
        PrecisionMode.Balanced => Low,
        PrecisionMode.UltraLowJitter => Extreme,
        _ => Balanced
    };
}
