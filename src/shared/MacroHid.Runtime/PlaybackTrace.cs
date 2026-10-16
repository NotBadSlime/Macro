using System.Diagnostics;
using System.Diagnostics.Tracing;

namespace MacroHid.Runtime;

public enum PlaybackTraceStage
{
    Hotkey = 1, Dispatch, WorkerStart, PlanReady, Deadline, SubmitStart, SubmitEnd,
    CancelRequested, CleanupComplete, NativeQueued, NativeFirstSubmit, NativeLastSubmit,
    NativeComplete, Receiver,
    // Appended after Receiver so existing numeric values do not move. Between WorkerStart and PlanReady.
    PrecisionEntered, ScopesReady, SafetyReady,
    // Appended: when the first batch's SendInput call returned.
    NativeFirstReturn
}

// Enable the MacroHid-Playback EventSource only during measurement. QPC values are
// retained verbatim; the receiver must emit its own event on the same machine.
[EventSource(Name = "MacroHid-Playback")]
public sealed class PlaybackTrace : EventSource
{
    public static readonly PlaybackTrace Log = new();
    internal static readonly AsyncLocal<long> Run = new();
    [Event(1, Level = EventLevel.Informational)]
    public void Timestamp(long runId, int stage, long qpc, long frequency)
    {
        if (IsEnabled()) WriteEvent(1, runId, stage, qpc, frequency);
    }
    [NonEvent]
    public static void Mark(PlaybackTraceStage stage, long qpc = 0, long runId = 0)
    {
        if (Log.IsEnabled()) Log.Timestamp(runId == 0 ? Run.Value : runId, (int)stage,
            qpc == 0 ? Stopwatch.GetTimestamp() : qpc, Stopwatch.Frequency);
    }
}
