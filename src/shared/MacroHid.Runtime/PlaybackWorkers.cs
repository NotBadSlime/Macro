namespace MacroHid.Runtime;

// Reservations are immediate: nested parallel work never waits behind its parent.
internal sealed class PlaybackWorkerPool(int maximumIdle, bool spinsWhenHot = false)
{
    private readonly object gate = new();
    private readonly List<Worker> idle = [];
    internal Task<T> Run<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = ExecutionContext.Capture();
        Action work = () =>
        {
            try
            {
                T result = default!;
                if (context is null) result = action();
                else ExecutionContext.Run(context, _ => result = action(), null);
                completion.TrySetResult(result);
            }
            catch (OperationCanceledException ex) { completion.TrySetCanceled(ex.CancellationToken); }
            catch (Exception ex) { completion.TrySetException(ex); }
        };
        lock (gate)
        {
            if (idle.Count > 0)
            {
                var worker = idle[^1];
                idle.RemoveAt(idle.Count - 1);
                worker.Work = work;
                Monitor.PulseAll(gate);
            }
            else
            {
                var worker = new Worker { Work = work };
                var thread = new Thread(() => Loop(worker)) { IsBackground = true, Name = "MacroHID-Worker" };
                // Work items carry their own context, not the first submitter's context.
                if (ExecutionContext.IsFlowSuppressed()) thread.Start();
                else { using (ExecutionContext.SuppressFlow()) thread.Start(); }
            }
        }
        return completion.Task;
    }
    private void Loop(Worker worker)
    {
        try
        {
            while (true)
            {
                Action? work;
                lock (gate) { work = worker.Work; worker.Work = null; }
                work!();
                lock (gate)
                {
                    if (idle.Count >= maximumIdle) return;
                    idle.Add(worker);
                    var until = Environment.TickCount64 + 60_000;
                    while (worker.Work is null)
                    {
                        int remaining = (int)Math.Max(0, until - Environment.TickCount64);
                        if (remaining == 0) { idle.Remove(worker); return; }
                        // Max hot standby: the worker that takes the next run (the last idle one) spins for it outside the
                        // lock instead of being woken by Monitor.Pulse (tens of microseconds on the hotkey path).
                        if (spinsWhenHot && MaxPreemption.HotStandby && idle.Count > 0 && ReferenceEquals(idle[^1], worker))
                        {
                            Monitor.Exit(gate);
                            try { SpinForWork(worker); }
                            finally { Monitor.Enter(gate); }
                            until = Environment.TickCount64 + 60_000;
                            continue;
                        }
                        Monitor.Wait(gate, Math.Min(remaining, 100));
                    }
                }
            }
        }
        finally { QpcPlaybackDelayStrategy.ReleaseThreadTimer(); }
    }
    private sealed class Worker { internal volatile Action? Work; }

    // Up to 50 ms, then back to the lock (hot standby may have ended; the caller re-checks). Thread.Yield every ~2 ms lets
    // other threads of this core run, so a spinning idle worker cannot starve them.
    private static void SpinForWork(Worker worker)
    {
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        var lastYield = start;
        var yieldTicks = System.Diagnostics.Stopwatch.Frequency / 500;
        var limit = System.Diagnostics.Stopwatch.Frequency / 20;
        while (worker.Work is null && MaxPreemption.HotStandby)
        {
            Thread.SpinWait(20);
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (now - start > limit) return;
            if (now - lastYield > yieldTicks) { Thread.Yield(); lastYield = now; }
        }
    }
}

internal static class PlaybackWorkers
{
    // Only the playback pool spins for its next run in Max hot standby; parallel branches keep blocking.
    internal static readonly PlaybackWorkerPool Playback = new(8, spinsWhenHot: true);
    internal static readonly PlaybackWorkerPool Parallel = new(32);
}
