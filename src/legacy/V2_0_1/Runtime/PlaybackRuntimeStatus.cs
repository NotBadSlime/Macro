namespace MacroHid.Runtime;

public sealed record PlaybackPreparationStatus(bool Preparing, bool Ready, string Backend, string Reason);

public static class PlaybackRuntimeStatus
{
    private static PlaybackPreparationStatus preparation = new(false, false, "managed", "尚未准备");
    private static string actualBackend = "尚未运行";
    private static string fallbackReason = "";
    private static int activeRuns;
    // Set (and flowed to pool workers by the execution context) while a path warm-up runs, so it never shows up in UI status.
    private static readonly AsyncLocal<bool> suppressed = new();
    internal static IDisposable Suppress()
    {
        suppressed.Value = true;
        return new SuppressScope();
    }
    private sealed class SuppressScope : IDisposable { public void Dispose() => suppressed.Value = false; }
    public static int ActiveRuns => Volatile.Read(ref activeRuns);
    internal static void RunStarted() { if (!suppressed.Value) Interlocked.Increment(ref activeRuns); }
    internal static void RunFinished() { if (!suppressed.Value) Interlocked.Decrement(ref activeRuns); }
    public static PlaybackPreparationStatus Preparation => Volatile.Read(ref preparation);
    public static string ActualBackend => Volatile.Read(ref actualBackend);
    public static string FallbackReason => Volatile.Read(ref fallbackReason);
    internal static void SetPreparation(PlaybackPreparationStatus value) => Volatile.Write(ref preparation, value);
    internal static void ReportBackend(string backend, string reason = "")
    {
        if (suppressed.Value) return;
        Volatile.Write(ref actualBackend, backend);
        Volatile.Write(ref fallbackReason, reason);
    }
}
