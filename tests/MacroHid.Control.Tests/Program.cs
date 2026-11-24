using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.CompilerServices;
using MacroHid.Control;
using MacroHid.Core;
using MacroHid.Runtime;
using MacroStudio;
using System.Text.Json;

if (args is ["--ctrl-c-cli", ..]) return await ConsoleInterruptTests.RunChildAsync(args);

if (args is ["--client-pipe", var childPipe])
{
    var response = await ControlPipe.SendAsync(new ControlRequest("status"), pipeName: childPipe);
    return response.Ok ? 0 : 1;
}

static void Check(bool value, string message) { if (!value) throw new Exception(message); }
static async Task<ControlJob> Finished(ControlJobs jobs, string id)
{
    using var timeout = new CancellationTokenSource(5000);
    while (true)
    {
        timeout.Token.ThrowIfCancellationRequested();
        var job = jobs.Get(id)!;
        if (ControlProtocol.IsTerminal(job.State)) return job;
        await Task.Delay(10, timeout.Token);
    }
}
foreach (var pair in new[] { ("500ms", 500d), ("3s", 3000d), ("1m", 60000d), ("0", 0d), ("1.5s", 1500d) })
    Check(ControlProtocol.ParseDelay(pair.Item1) == pair.Item2, "Delay units failed.");
foreach (var invalid in new[] { "-1s", "NaN", "Infinity", "8m?", "99999999m" })
{
    try { ControlProtocol.ParseDelay(invalid); throw new Exception("Invalid delay accepted."); } catch (ArgumentException) { }
}
Console.WriteLine("PASS delay units and validation");

var jobs = new ControlJobs();
var macro = new ControlMacro("test", "中文宏", "Test", "");
int starts = 0;
var clock = Stopwatch.StartNew();
var delayed = jobs.Schedule(macro, 150, (_, started) => { Interlocked.Increment(ref starts); started(); return Task.FromResult(new ControlExecution("completed")); });
Check(jobs.Get(delayed.RunId)!.State == "waiting", "Delayed macro started immediately.");
Check((await Finished(jobs, delayed.RunId)).State == "completed" && starts == 1 && clock.ElapsedMilliseconds >= 140, "Delayed execution failed.");
var cancelled = jobs.Schedule(macro, 10000, (_, _) => { Interlocked.Increment(ref starts); return Task.FromResult(new ControlExecution("completed")); });
Check(jobs.Cancel(cancelled.RunId), "Cancellation rejected.");
Check((await Finished(jobs, cancelled.RunId)).State == "cancelled" && starts == 1, "Cancelled pending macro executed.");
var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var running = jobs.Schedule(macro, 0, async (token, started) => { started(); entered.SetResult(); await Task.Delay(10000, token); return new("completed"); });
await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
Check(jobs.Get(running.RunId)!.State == "running", "Running status failed.");
jobs.Cancel(running.RunId);
Check((await Finished(jobs, running.RunId)).State == "cancelled", "Running cancellation failed.");
var busy = jobs.Schedule(macro, 0, (_, _) => Task.FromResult(new ControlExecution("busy")));
Check((await Finished(jobs, busy.RunId)).State == "busy", "Busy result lost.");
var failed = jobs.Schedule(macro, 0, (_, _) => throw new IOException("failed test"));
Check((await Finished(jobs, failed.RunId)).Error == "failed test", "Failure was not reported.");
jobs.CancelAll();
Console.WriteLine("PASS delayed execution, status, pending/running cancellation, busy and errors");
var limited = new ControlJobs();
var pending = Enumerable.Range(0, 64).Select(_ => limited.Schedule(macro, 60000,
    (_, _) => throw new Exception("Stopped waiting job ran."))).ToArray();
try { limited.Schedule(macro, 60000, (_, _) => Task.FromResult(new ControlExecution("completed"))); throw new Exception("Unbounded task acceptance."); }
catch (ArgumentException) { }
limited.CancelAll();
foreach (var task in pending) Check((await Finished(limited, task.RunId)).State == "cancelled", "Emergency cancellation left a waiting task.");
Console.WriteLine("PASS pending task bound and cancel-all");

var pipeName = ControlPipe.Name + ".Test." + Guid.NewGuid().ToString("N");
await using (var server = new ControlPipeServer(pipeName))
{
    bool collision = false;
    try { await using var second = new ControlPipeServer(pipeName); } catch (IOException) { collision = true; }
    Check(collision, "Duplicate Studio endpoint allowed.");
    server.Start(request => Task.FromResult(new ControlResponse(true, request.Command, Macros: [macro])));
    using (var bad = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
    {
        await bad.ConnectAsync(2000);
        await bad.WriteAsync(BitConverter.GetBytes(int.MaxValue));
    }
    var response = await ControlPipe.SendAsync(new ControlRequest("list"), pipeName: pipeName);
    Check(response.Ok && response.Macros![0].Name == macro.Name, "Pipe did not recover from malformed client or lost Unicode.");
    for (int i = 0; i < 100; i++)
        Check((await ControlPipe.SendAsync(new ControlRequest("status"), pipeName: pipeName)).Code == "status", "Pipe reconnect failed.");
    var start = new ProcessStartInfo("dotnet") { UseShellExecute = false };
    start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    start.ArgumentList.Add("--client-pipe"); start.ArgumentList.Add(pipeName);
    using var child = Process.Start(start)!;
    await child.WaitForExitAsync();
    Check(child.ExitCode == 0, "Cross-process pipe request failed.");
}
Console.WriteLine("PASS real Windows pipe roundtrips, malformed lengths, reconnects and single endpoint ownership");

// Exercise the actual Studio command router against an isolated library without
// constructing the real window, installing hooks or touching the user's data.
var root = Path.Combine(Path.GetTempPath(), "MacroHID-ControlTests-" + Guid.NewGuid().ToString("N"));
try
{
    var store = new MacroLibraryStore(root);
    var group = store.CreateGroup("测试库");
    var item = store.CreateMacro("宏A", groupId: group.Id);
    var window = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
    void Set(string name, object value) => typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, value);
    Set("libraryStore", store);
    var routerJobs = new ControlJobs();
    Set("externalJobs", routerJobs);
    Set("acceptedExternalRequests", new Dictionary<string, ControlResponse>());
    var method = typeof(MainWindow).GetMethod("HandleControlRequest", BindingFlags.NonPublic | BindingFlags.Instance)!;
    ControlResponse Send(ControlRequest request) => (ControlResponse)method.Invoke(window, [request])!;
    Check(Send(new("list")).Macros!.Any(m => m.Id == item.Id && m.Library == group.Name), "Studio list failed.");
    var request = new ControlRequest("run", Id: item.Id, DelayMilliseconds: 10000, RequestId: Guid.NewGuid().ToString());
    var accepted = Send(request);
    Check(accepted.Ok, "Studio rejected saved macro without hotkey/listening.");
    var repeated = Send(request);
    Check(accepted.Job!.RunId == repeated.Job!.RunId && routerJobs.List().Length == 1, "Retried request started twice.");
    Check(Send(new("stop", RunId: accepted.Job.RunId)).Ok, "Studio stop failed.");
    await Finished(routerJobs, accepted.Job.RunId);
    Check(Send(new("status", RunId: accepted.Job.RunId)).Job!.State == "cancelled", "Studio status failed.");
    Check(!Send(new("run", Id: item.Id)).Ok, "Missing idempotency ID accepted.");
    Check(!Send(new("run", Id: item.Id, DelayMilliseconds: -1, RequestId: Guid.NewGuid().ToString())).Ok, "Negative wire delay accepted.");
    Check(Send(new("run", Id: item.Id, Count: 0, RequestId: Guid.NewGuid().ToString())).Code == "invalid", "Invalid wire count accepted.");
    Check(Send(new("run", Id: item.Id, Count: 2, Loop: true, RequestId: Guid.NewGuid().ToString())).Code == "invalid", "Conflicting wire loop/count accepted.");
    Check(Send(new("run", Id: "missing", RequestId: Guid.NewGuid().ToString())).Code == "not_found", "Missing macro not reported.");
    var conditionMacro = store.CreateMacro(new MacroDocument(1, "守护条件宏", PlaybackSettings.Default with { RunAsDaemon = true }, [],
        [new("test", "test", -1, -1, new PixelMatcher(ScreenRegion.FromSinglePixel(0, 0), new RgbColor(1, 2, 3), 0), [])],
        Kind: MacroKind.Condition), groupId: group.Id);
    var conditionJob = Send(new("run", Id: conditionMacro.Id, DelayMilliseconds: 60000, RequestId: Guid.NewGuid().ToString(), Loop: true));
    Check(conditionJob.Ok && Send(new("stop", RunId: conditionJob.Job!.RunId)).Ok,
        "A standalone condition macro should accept an external loop request and stop by job ID.");
    Check((await Finished(routerJobs, conditionJob.Job!.RunId)).State == "cancelled", "Condition-only job was not cancelled.");
    Set("preparingImmediate", true);
    var busyRun = Send(new("run", Id: item.Id, RequestId: Guid.NewGuid().ToString()));
    Check(busyRun.Ok && (await Finished(routerJobs, busyRun.Job!.RunId)).State == "busy", "Studio did not reject execution at deadline when preparing another macro.");
    Set("preparingImmediate", false);
    store.CreateMacro("宏A", groupId: group.Id);
    Check(Send(new("run", Library: group.Name, Name: "宏A", RequestId: Guid.NewGuid().ToString())).Code == "ambiguous", "Ambiguous macro silently selected.");
}
finally { Directory.Delete(root, true); }
Console.WriteLine("PASS actual Studio router, saved macro selection, task stop/status and idempotency");

foreach (var arguments in new[] {
    new[] { "run", "--id", "x", "--delay", "-1s" },
    new[] { "run", "--id", "x", "--name", "a" },
    new[] { "run", "--name", "a" },
    new[] { "stop" }, new[] { "list", "--delay", "3s" }, new[] { "run", "--id", "x", "--unknown" } })
    Check(await MacroCtlProgram.RunAsync(arguments) == 2, "CLI accepted invalid arguments.");
Console.WriteLine("PASS CLI validation and exit codes");
foreach (var arguments in new[] {
    new[] { "run", "--id", "x", "--count", "0" }, new[] { "run", "--id", "x", "--count", "-3" },
    new[] { "run", "--id", "x", "--count", "1.5" }, new[] { "run", "--id", "x", "--count", "2147483648" },
    new[] { "run", "--id", "x", "--loop", "--count", "2" }, new[] { "status", "--loop" } })
    Check(await MacroCtlProgram.RunAsync(arguments) == 2, "CLI accepted invalid loop/count arguments.");

var optionMethod = typeof(MainWindow).GetMethod("CreateExternalPlaybackOptions", BindingFlags.NonPublic | BindingFlags.Static)!;
var baseOptions = new PlaybackExecutionOptions(PlaybackMode.HoldLoop, 9, PixelEvaluationMode.Live, false, PrecisionMode.Balanced);
PlaybackExecutionOptions Options(ControlRequest request) => (PlaybackExecutionOptions)optionMethod.Invoke(null, [baseOptions, request])!;
Check(Options(new("run")).Mode == PlaybackMode.FixedCount && Options(new("run")).Count == 1, "Default run no longer runs once.");
var countOptions = Options(new("run", Count: 3));
Check(countOptions.Mode == PlaybackMode.FixedCount && countOptions.Count == 3 && countOptions.Precision == baseOptions.Precision, "Playback count or precision changed.");
var sample = new MacroDocument(1, "count-test", [new KeyStep(KeyActionKind.Down, HidKey.A, HidModifier.None, TimeSpan.Zero), new KeyStep(KeyActionKind.Up, HidKey.A, HidModifier.None, TimeSpan.Zero), new WaitStep(TimeSpan.FromMilliseconds(1))]);
var documentMethod = typeof(MainWindow).GetMethod("CreateExternalPlaybackDocument", BindingFlags.NonPublic | BindingFlags.Static)!;
var countedSnapshot = (MacroDocument)documentMethod.Invoke(null, [sample, countOptions])!;
var loopSnapshot = (MacroDocument)documentMethod.Invoke(null, [sample, Options(new("run", Loop: true))])!;
Check(countedSnapshot.Playback.Count == 3 && loopSnapshot.Playback.Mode == PlaybackMode.ToggleLoop
    && sample.Playback.Count == 1 && sample.Playback.Mode == PlaybackMode.FixedCount,
    "Historical-engine snapshot lost external options or modified saved settings.");
using (var executor = new MacroPlaybackExecutor(new InertInputSink()))
{
    var result = await executor.RunAsync(sample, countOptions, CancellationToken.None);
    Check(result.IterationsCompleted == 3 && !result.Cancelled, "External count did not reach the real executor.");
}
using (var stopLoop = new CancellationTokenSource(3000))
using (var executor = new MacroPlaybackExecutor(new InertInputSink(() => stopLoop.Cancel())))
{
    var loopOptions = Options(new("run", Loop: true));
    Check(loopOptions.Mode == PlaybackMode.ToggleLoop, "Continuous mode was converted to a single run.");
    var result = await executor.RunAsync(sample, loopOptions, stopLoop.Token);
    Check(result.Cancelled && result.IterationsCompleted >= 2, "Continuous mode did not repeat until stopped.");
}
Console.WriteLine("PASS external repeat count and cancellable loop through the real executor (inert input)");

var cliPipe = ControlPipe.Name + ".CliTest." + Guid.NewGuid().ToString("N");
var cliJobs = new ControlJobs();
var cliRequests = new List<ControlRequest>();
await using (var server = new ControlPipeServer(cliPipe))
{
    server.Start(request => { cliRequests.Add(request); return Task.FromResult(request.Command switch
    {
        "run" => new ControlResponse(true, "accepted", Job: cliJobs.Schedule(macro, request.DelayMilliseconds,
            (_, started) => { started(); return Task.FromResult(new ControlExecution(request.Id == "busy" ? "busy" : "completed")); })),
        "status" => new ControlResponse(true, "ok", Job: request.RunId is null ? null : cliJobs.Get(request.RunId), Capabilities: [ControlProtocol.PlaybackRepeatCapability]),
        _ => ControlResponse.Fail("invalid", "test")
    }); });
    var output = Console.Out;
    using var capture = new StringWriter();
    try
    {
        Console.SetOut(capture);
        Check(await MacroCtlProgram.RunAsync(["run", "--id", "test", "--delay", "100ms", "--wait", "--json"], cliPipe) == 0, "CLI wait failed.");
        using var json = JsonDocument.Parse(capture.ToString());
        Check(json.RootElement.GetProperty("job").GetProperty("state").GetString() == "completed", "--wait returned before completion.");
        Check(await MacroCtlProgram.RunAsync(["run", "--id", "busy", "--wait"], cliPipe) == 6, "CLI busy exit code failed.");
        Check(await MacroCtlProgram.RunAsync(["run", "--id", "test", "--delay", "100ms", "--count", "5", "--wait"], cliPipe) == 0, "CLI counted run failed.");
        Check(cliRequests.Any(r => r.Command == "run" && r.Count == 5 && !r.Loop && r.DelayMilliseconds == 100), "Count was lost in pipe transport.");
        Check(await MacroCtlProgram.RunAsync(["run", "--id", "test", "--loop"], cliPipe) == 0, "CLI loop request failed.");
        Check(cliRequests.Any(r => r.Command == "run" && r.Loop && r.Count is null), "Loop was lost in pipe transport.");
    }
    finally { Console.SetOut(output); }
}
Console.WriteLine("PASS CLI JSON completion and busy exit code over real pipe");
await using (var oldServer = new ControlPipeServer(cliPipe))
{
    var attemptedRun = false;
    oldServer.Start(request => { attemptedRun |= request.Command == "run"; return Task.FromResult(new ControlResponse(true, "ok")); });
    Check(await MacroCtlProgram.RunAsync(["run", "--id", "test", "--count", "5"], cliPipe) == 7 && !attemptedRun,
        "A new client silently ran one iteration on an old Studio server.");
}
Console.WriteLine("PASS repeat commands refuse older Studio without starting the macro");
await ConsoleInterruptTests.RunAsync(sample);
return 0;

internal sealed class InertInputSink(Action? cancel = null) : IMacroInputSink
{
    private int submissions;
    public bool IsAvailable => true;
    public void Submit(uint sequence, InputAction action) { if (Interlocked.Increment(ref submissions) == 8) cancel?.Invoke(); }
    public InputSubmissionStats? GetStats() => null;
}
