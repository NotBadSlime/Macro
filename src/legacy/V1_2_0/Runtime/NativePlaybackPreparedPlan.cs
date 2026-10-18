using System.Diagnostics;

namespace MacroHid.Runtime;

internal sealed class NativePlaybackPreparedPlan : IDisposable
{
    private IntPtr handle;

    private NativePlaybackPreparedPlan(
        IntPtr handle,
        NativePlaybackTimeline timeline,
        long qpcFrequency,
        long planCreateMicroseconds)
    {
        this.handle = handle;
        Timeline = timeline;
        QpcFrequency = qpcFrequency;
        PlanCreateMicroseconds = planCreateMicroseconds;
    }

    public NativePlaybackTimeline Timeline { get; }

    public long QpcFrequency { get; }

    public long PlanCreateMicroseconds { get; }

    internal IntPtr Handle
    {
        get
        {
            ObjectDisposedException.ThrowIf(handle == IntPtr.Zero, this);
            return handle;
        }
    }

    public void Dispose()
    {
        var plan = Interlocked.Exchange(ref handle, IntPtr.Zero);
        if (plan != IntPtr.Zero)
        {
            NativePlaybackInterop.MhpDestroyPlan(plan);
        }
    }

    internal static bool TryCreate(
        NativePlaybackTimeline timeline,
        long qpcFrequency,
        out NativePlaybackPreparedPlan? preparedPlan,
        out long planCreateMicroseconds,
        out MhpStatus status)
    {
        preparedPlan = null;
        var start = Stopwatch.GetTimestamp();
        status = NativePlaybackInterop.MhpCreatePlan(
            timeline.Inputs,
            checked((uint)timeline.Inputs.Length),
            timeline.Batches,
            checked((uint)timeline.Batches.Length),
            out var nativePlan);
        planCreateMicroseconds = ToMicroseconds(Stopwatch.GetTimestamp() - start, Stopwatch.Frequency);

        if (status != MhpStatus.Ok || nativePlan == IntPtr.Zero)
        {
            return false;
        }

        preparedPlan = new NativePlaybackPreparedPlan(nativePlan, timeline, qpcFrequency, planCreateMicroseconds);
        return true;
    }

    private static long ToMicroseconds(long ticks, long frequency)
    {
        return frequency <= 0
            ? 0
            : (long)Math.Round(ticks * 1_000_000.0 / frequency, MidpointRounding.AwayFromZero);
    }
}
