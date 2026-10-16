namespace MacroHid.Runtime;

/// <summary>
/// The hotkey moment of the run being started, carried to the playback worker by the execution context (the worker
/// runs the captured context). Set by <see cref="MacroPlaybackController"/>; 0 for runs without a hotkey.
/// </summary>
public static class PlaybackTrigger
{
    internal static readonly AsyncLocal<long> CallbackQpc = new();
    private static long anchoredStarts;
    private static long lateStarts;

    /// <summary>Median SendInput cost (us) the native scheduler has learned for key batches and mouse batches; 0 = not yet.</summary>
    public static (long KeyboardUs, long MouseUs) LearnedSendCost
    {
        get
        {
            try { NativePlaybackInterop.MhpGetSendCost(out var keyboard, out var mouse); return (keyboard, mouse); }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { return (0, 0); }
        }
    }

    /// <summary>Runs that started exactly at hotkey + lead, and runs whose preparation overran the lead.</summary>
    public static (long Anchored, long Late) StartCounts => (Interlocked.Read(ref anchoredStarts), Interlocked.Read(ref lateStarts));

    internal static void RecordStart(bool anchored)
    {
        if (anchored) Interlocked.Increment(ref anchoredStarts);
        else Interlocked.Increment(ref lateStarts);
    }
}
