namespace MacroHid.Runtime;

public sealed record PlaybackPreparationStatus(bool Preparing, bool Ready, string Backend, string Reason);

public static class PlaybackRuntimeStatus
{
    private static PlaybackPreparationStatus preparation = new(false, false, "managed", "尚未准备");
    private static string actualBackend = "尚未运行";
    private static string fallbackReason = "";
    private static int activeRuns;
    public static int ActiveRuns => Volatile.Read(ref activeRuns);
    internal static void RunStarted() => Interlocked.Increment(ref activeRuns);
    internal static void RunFinished() => Interlocked.Decrement(ref activeRuns);
    public static PlaybackPreparationStatus Preparation => Volatile.Read(ref preparation);
    public static string ActualBackend => Volatile.Read(ref actualBackend);
    public static string FallbackReason => Volatile.Read(ref fallbackReason);
    internal static void SetPreparation(PlaybackPreparationStatus value) => Volatile.Write(ref preparation, value);
    internal static void ReportBackend(string backend, string reason = "")
    {
        Volatile.Write(ref actualBackend, backend);
        Volatile.Write(ref fallbackReason, reason);
    }
}
