using System.Diagnostics;

namespace MacroHid.Control;

public sealed record ControlExecution(string State, string? Error = null);
public sealed class ControlJobs
{
    private sealed class Job(ControlMacro macro, double delay)
    {
        public readonly string Id = Guid.NewGuid().ToString("N");
        public readonly ControlMacro Macro = macro;
        public readonly double Delay = delay;
        public readonly Stopwatch Clock = Stopwatch.StartNew();
        public readonly DateTimeOffset Due = DateTimeOffset.UtcNow.AddMilliseconds(delay);
        public readonly CancellationTokenSource Cancellation = new();
        public string State = "waiting";
        public string? Error;
    }
    private readonly object gate = new();
    private readonly Dictionary<string, Job> jobs = [];
    public ControlJob Schedule(ControlMacro macro, double delay, Func<CancellationToken, Action, Task<ControlExecution>> execute)
    {
        if (!double.IsFinite(delay) || delay < 0 || delay > TimeSpan.FromDays(7).TotalMilliseconds) throw new ArgumentException("Invalid delay.");
        Job job;
        lock (gate)
        {
            foreach (var old in jobs.Values.Where(j => ControlProtocol.IsTerminal(j.State)).SkipLast(32).ToArray())
            { jobs.Remove(old.Id); old.Cancellation.Dispose(); }
            if (jobs.Values.Count(j => !ControlProtocol.IsTerminal(j.State)) >= 64) throw new ArgumentException("At most 64 pending/running external jobs are allowed.");
            job = new Job(macro, delay);
            jobs.Add(job.Id, job);
        }
        _ = ExecuteAsync(job, execute);
        return Snapshot(job);
    }
    private async Task ExecuteAsync(Job job, Func<CancellationToken, Action, Task<ControlExecution>> execute)
    {
        try
        {
            await Task.Yield();
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(0, job.Delay - job.Clock.Elapsed.TotalMilliseconds)), job.Cancellation.Token);
            lock (gate) { job.Cancellation.Token.ThrowIfCancellationRequested(); job.State = "starting"; }
            var result = await execute(job.Cancellation.Token, () => { lock (gate) job.State = job.Cancellation.IsCancellationRequested ? "stopping" : "running"; });
            lock (gate) { job.State = result.State; job.Error = result.Error; }
        }
        catch (OperationCanceledException) { lock (gate) job.State = "cancelled"; }
        catch (Exception ex) { lock (gate) { job.State = "failed"; job.Error = ex.Message; } }
    }
    public ControlJob? Get(string id) { lock (gate) return jobs.TryGetValue(id, out var job) ? Snapshot(job) : null; }
    public ControlJob[] List() { lock (gate) return jobs.Values.Select(Snapshot).ToArray(); }
    public bool Cancel(string id)
    {
        lock (gate)
        {
            if (!jobs.TryGetValue(id, out var job)) return false;
            if (!ControlProtocol.IsTerminal(job.State)) { job.State = "stopping"; job.Cancellation.Cancel(); }
            return true;
        }
    }
    public void CancelAll() { lock (gate) foreach (var job in jobs.Values.Where(j => !ControlProtocol.IsTerminal(j.State)).ToArray()) Cancel(job.Id); }
    private ControlJob Snapshot(Job job)
    {
        lock (gate) return new(job.Id, job.Macro.Id, job.Macro.Name, job.State, job.Due,
            job.State == "waiting" ? Math.Max(0, job.Delay - job.Clock.Elapsed.TotalMilliseconds) : 0, job.Error);
    }
}
