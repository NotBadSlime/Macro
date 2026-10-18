namespace MacroHid.Runtime;

// Reservations are immediate: nested parallel work never waits behind its parent.
internal sealed class PlaybackWorkerPool(int maximumIdle)
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
                        Monitor.Wait(gate, remaining);
                    }
                }
            }
        }
        finally { QpcPlaybackDelayStrategy.ReleaseThreadTimer(); }
    }
    private sealed class Worker { internal Action? Work; }
}

internal static class PlaybackWorkers
{
    internal static readonly PlaybackWorkerPool Playback = new(8);
    internal static readonly PlaybackWorkerPool Parallel = new(32);
}
