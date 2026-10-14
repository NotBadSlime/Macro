using System.Diagnostics;
using MacroHid.Core;
using MacroHid.Runtime;

internal static class SchedulingOptimizationTests
{
    internal static void Lifecycle()
    {
        var doc = new MacroDocument(1, "old", []);
        var old = new Pending();
        var next = new Pending();
        var controller = new MacroPlaybackController(doc, old);
        controller.RunNowAsync().GetAwaiter().GetResult();
        long first = controller.RunId;
        controller.UpdateDefinition(doc with { Name = "new" }, next, new(PlaybackMode.FixedCount, 1, PixelEvaluationMode.Skip, true));
        Check(!old.Disposed && next.Starts == 0, "Update must not interrupt the active version");
        controller.Stop();
        controller.TriggerPressedAsync().GetAwaiter().GetResult();
        Check(old.Token.IsCancellationRequested && next.Starts == 0 && controller.RunId == first, "Stop must not queue a restart");
        var stopped = controller.StopAsync();
        Check(!stopped.IsCompleted && !old.Disposed, "Stop must await cleanup");
        old.Finish(); stopped.GetAwaiter().GetResult();
        Check(old.Disposed, "Superseded executor must retire after completion");
        controller.RunNowAsync().GetAwaiter().GetResult();
        Check(controller.RunId > first && next.Name == "new", "Next trigger uses new definition");
        var disposed = controller.DisposeAsync().AsTask();
        controller.Dispose();
        Check(!disposed.IsCompleted && !next.Disposed && next.Token.IsCancellationRequested, "Dispose must await the active run");
        next.Finish(); disposed.GetAwaiter().GetResult();
        controller.RunNowAsync().GetAwaiter().GetResult();
        Check(next.Disposals == 1 && next.Starts == 1, "Disposed controller cannot restart or double dispose");
        for (int i = 0; i < 25; i++)
        {
            var pending = new Pending();
            var racing = new MacroPlaybackController(doc, pending);
            racing.RunNowAsync().GetAwaiter().GetResult();
            Task.WhenAll(Task.Run(racing.Stop), Task.Run(racing.Dispose),
                Task.Run(() => racing.TriggerPressedAsync())).GetAwaiter().GetResult();
            Check(pending.Starts == 1 && pending.Disposals == 0, "Concurrent stop/dispose/trigger must retain active resources");
            pending.Finish();
            racing.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Check(pending.Disposals == 1, "Racing disposal must release exactly once");
        }
    }

    internal static void CancellationAndWorkers()
    {
        var local = new AsyncLocal<string?>();
        local.Value = "first";
        var pool = new PlaybackWorkerPool(2);
        var first = pool.Run(() => { Check(local.Value == "first", "Capture execution context"); local.Value = "leak"; return Environment.CurrentManagedThreadId; }).Result;
        local.Value = "second";
        var second = pool.Run(() => { Check(local.Value == "second", "Worker must not retain previous context"); return Environment.CurrentManagedThreadId; }).Result;
        // Completion can precede returning to the idle list; repeated runs must settle to reuse.
        var ids = new HashSet<int> { first, second };
        for (int i = 0; i < 30; i++) ids.Add(pool.Run(() => Environment.CurrentManagedThreadId).Result);
        Check(ids.Count <= 4, "Short sequential runs should reuse workers");
        Task<string?> suppressed;
        using (ExecutionContext.SuppressFlow()) suppressed = new PlaybackWorkerPool(0).Run(() => local.Value);
        Check(suppressed.Result is null, "Suppressed flow must be supported without capturing submitter state");
        var doc = new MacroDocument(1, "cancel", []);
        using var controller = new MacroPlaybackController(doc, new Cancelled());
        controller.RunNowAsync().GetAwaiter().GetResult();
        controller.Stop();
        var result = controller.WhenIdleAsync().GetAwaiter().GetResult();
        Check(result.Cancelled && result.Status == PlaybackRunStatus.Completed && controller.LastError is null, "Requested cancellation is not a failure");
        using var cancellation = new CancellationTokenSource();
        using var started = new ManualResetEventSlim();
        var wait = pool.Run(() => {
            var delay = new QpcPlaybackDelayStrategy(precision: PrecisionMode.Balanced);
            started.Set();
            delay.WaitUntil(Stopwatch.GetTimestamp() + Stopwatch.Frequency * 5, Stopwatch.Frequency, cancellation.Token, false);
            return true;
        });
        started.Wait();
        var watch = Stopwatch.StartNew(); cancellation.Cancel();
        try { wait.GetAwaiter().GetResult(); throw new Exception("Wait did not cancel"); }
        catch (OperationCanceledException) { }
        Check(watch.Elapsed < TimeSpan.FromSeconds(1), "Timer cancellation must wake the worker");
        Console.WriteLine($"METRIC timer cancellation {watch.Elapsed.TotalMilliseconds:F3} ms; worker IDs={ids.Count}");
        var full = ParallelWorkerLease.Acquire(32);
        try { ParallelWorkerLease.Acquire(2); throw new Exception("Overload did not fail"); }
        catch (InvalidOperationException) { }
        full.Dispose(); full.Dispose();
        using var again = ParallelWorkerLease.Acquire(32);
    }

    internal static void CountingAndLogBackpressure()
    {
        var document = McrxParser.Parse("{\"steps\":[{\"type\":\"repeat\",\"count\":1000000,\"steps\":[{\"type\":\"key.down\",\"key\":\"A\"}]}]}");
        MacroScheduler.CountScheduledSteps(document.Steps);
        long before = GC.GetAllocatedBytesForCurrentThread(); var watch = Stopwatch.StartNew();
        long count = MacroScheduler.CountScheduledSteps(document.Steps);
        watch.Stop(); long allocation = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(count == 1000000 && allocation < 1024 * 1024, "Count must not expand repeats");
        Console.WriteLine($"METRIC million repeat count {watch.Elapsed.TotalMilliseconds:F4} ms; {allocation} bytes");
        MacroStep huge = new KeyStep(KeyActionKind.Down, HidKey.A, HidModifier.None, TimeSpan.Zero);
        for (int i = 0; i < 4; i++) huge = new RepeatStep(int.MaxValue, [huge]);
        try { MacroScheduler.CountScheduledSteps([huge]); throw new Exception("Overflow not detected"); } catch (OverflowException) { }
        using var release = new ManualResetEventSlim(); using var entered = new ManualResetEventSlim();
        var messages = new List<string>();
        var logger = new BufferedLogQueue((level, source, text) => { entered.Set(); release.Wait(); lock (messages) messages.Add(level + text); });
        logger.Write("INFO", "test", "first"); entered.Wait();
        for (int i = 0; i < 6000; i++) logger.Write("INFO", "test", "normal");
        logger.Write("ERROR", "test", "important");
        Check(!logger.Flush(TimeSpan.FromMilliseconds(1)), "Blocked disk must not report drained");
        release.Set(); Check(logger.Flush(TimeSpan.FromSeconds(3)), "Logger must flush");
        lock (messages) Check(messages.Any(m => m == "ERRORimportant") && messages.Any(m => m.Contains("Dropped")), "Errors take precedence and overflow is reported");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Pending : IMacroPlaybackExecutor, IDisposable
    {
        private readonly TaskCompletionSource<PlaybackRunResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Starts, Disposals;
        internal bool Disposed => Disposals > 0;
        internal CancellationToken Token;
        internal string? Name;
        public Task<PlaybackRunResult> RunAsync(MacroDocument document, PlaybackExecutionOptions options, CancellationToken cancellationToken)
        { Starts++; Token = cancellationToken; Name = document.Name; return completion.Task; }
        internal void Finish() => completion.SetResult(new(PlaybackRunStatus.Completed, 1, 0, Token.IsCancellationRequested, null));
        public void Dispose() => Disposals++;
    }
    private sealed class Cancelled : IMacroPlaybackExecutor
    {
        public async Task<PlaybackRunResult> RunAsync(MacroDocument document, PlaybackExecutionOptions options, CancellationToken token)
        { await Task.Delay(Timeout.Infinite, token); return new(PlaybackRunStatus.Completed, 0, 0, false, null); }
    }
}
