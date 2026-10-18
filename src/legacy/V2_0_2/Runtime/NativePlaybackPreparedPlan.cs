using System.Diagnostics;

namespace MacroHid.Runtime;

internal sealed class NativePlaybackPreparedPlan : IDisposable
{
    private IntPtr handle;
    private readonly object bufferGate = new();
    private readonly Stack<NativeOutlierEvent[]> buffers = new();

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
        int capacity = Math.Min(timeline.Batches.Length, 65_536);
        if (capacity > 0)
            for (int i = 0; i < 2; i++) buffers.Push(System.Buffers.ArrayPool<NativeOutlierEvent>.Shared.Rent(capacity));
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
        lock (bufferGate)
            while (buffers.TryPop(out var buffer)) System.Buffers.ArrayPool<NativeOutlierEvent>.Shared.Return(buffer);
    }

    internal NativeOutlierEvent[] RentOutliers(int capacity)
    {
        lock (bufferGate)
            if (buffers.TryPop(out var buffer)) return buffer;
        return System.Buffers.ArrayPool<NativeOutlierEvent>.Shared.Rent(capacity);
    }
    internal void ReturnOutliers(NativeOutlierEvent[] buffer)
    {
        lock (bufferGate)
        {
            if (handle != IntPtr.Zero && buffers.Count < 2) { buffers.Push(buffer); return; }
        }
        System.Buffers.ArrayPool<NativeOutlierEvent>.Shared.Return(buffer);
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
