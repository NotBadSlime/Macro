using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using MacroHid.Control;

return await MacroCtlProgram.RunAsync(args);

public static class MacroCtlProgram
{
    public static async Task<int> RunAsync(string[] args, string? pipeName = null)
    {
        bool json = args.Contains("--json");
        bool interrupted = false;
        try
        {
            if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
            {
                Console.WriteLine($"""
                    {GlobalCommand.CommandName} list [--json]
                    {GlobalCommand.CommandName} run (--id ID | --library LIBRARY --name NAME) [--count N | --loop] [--delay 500ms|3s|1m] [--wait] [--launch] [--json]
                    {GlobalCommand.CommandName} status [--run-id ID] [--json]
                    {GlobalCommand.CommandName} stop --run-id ID [--json]
                    {GlobalCommand.CommandName} register --studio PATH-TO-MacroStudio.exe
                    {GlobalCommand.CommandName} unregister --studio PATH-TO-MacroStudio.exe
                    {GlobalCommand.CommandName} doctor
                    run defaults to one iteration; --count N executes N iterations; --loop runs until stopped.
                    Delay applies once before the first iteration, starting when Studio accepts the request.
                    --loop --wait: Ctrl+C stops that loop and waits for it to stop (exit 130).
                    Other --wait commands: Ctrl+C only stops waiting. Use stop to cancel the macro.
                    Exit codes: 0 success; 2 invalid arguments; 3 Studio unavailable; 4 not found;
                    5 ambiguous macro; 6 busy; 7 execution failed; 8 cancelled; 130 interrupted wait.
                    """);
                return 0;
            }
            var command = args[0];
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            var flags = new HashSet<string>();
            for (int i = 1; i < args.Length; i++)
            {
                var key = args[i];
                if (key is "--json" or "--wait" or "--launch" or "--loop") { if (!flags.Add(key)) throw new ArgumentException("Duplicate flag: " + key); }
                else if (key is "--id" or "--library" or "--name" or "--run-id" or "--delay" or "--studio" or "--count")
                {
                    if (++i >= args.Length || args[i].StartsWith("--") || !values.TryAdd(key, args[i])) throw new ArgumentException("Missing or duplicate value: " + key);
                }
                else throw new ArgumentException("Unknown option: " + key);
            }
            string? Value(string key) => values.GetValueOrDefault(key);
            var allowed = command switch
            {
                "list" => new[] { "--json", "--launch" },
                "run" => ["--id", "--library", "--name", "--delay", "--wait", "--launch", "--json", "--count", "--loop"],
                "status" => ["--run-id", "--json", "--launch"],
                "stop" => ["--run-id", "--json"],
                "register" or "unregister" => ["--studio"],
                "doctor" => [],
                _ => throw new ArgumentException("Unknown command: " + command)
            };
            if (values.Keys.Concat(flags).Except(allowed).Any()) throw new ArgumentException("Option is not supported for " + command);
            if (command == "doctor") { Console.WriteLine(GlobalCommand.Describe()); return 0; }
            if (command is "register" or "unregister")
            {
                var path = Value("--studio") ?? throw new ArgumentException("--studio is required.");
                if (command == "register") GlobalCommand.Register(path, AppContext.BaseDirectory);
                else if (!GlobalCommand.Unregister(path)) throw new ArgumentException("This version is not the registered target; registration unchanged.");
                Console.WriteLine(GlobalCommand.Describe()); return 0;
            }
            if (command == "run" && ((Value("--id") is not null) == (Value("--name") is not null)
                || (Value("--name") is not null && Value("--library") is null)
                || (Value("--id") is not null && Value("--library") is not null)))
                throw new ArgumentException("Use --id, or both --library and --name.");
            if (command == "stop" && Value("--run-id") is null) throw new ArgumentException("stop requires --run-id.");
            int? count = null;
            if (Value("--count") is { } countText)
            {
                if (!int.TryParse(countText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedCount))
                    throw new ArgumentException("--count must be a positive integer.");
                count = parsedCount;
            }
            ControlProtocol.ValidatePlayback(count, flags.Contains("--loop"));
            var request = new ControlRequest(command, Value("--id"), Value("--library"), Value("--name"), Value("--run-id"),
                Value("--delay") is { } delay ? ControlProtocol.ParseDelay(delay) : 0, Guid.NewGuid().ToString("N"), count, flags.Contains("--loop"));
            var stopLoopOnInterrupt = command == "run" && request.Loop && flags.Contains("--wait");
            using var interrupt = new CancellationTokenSource();
            ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; interrupted = true; interrupt.Cancel(); };
            Console.CancelKeyPress += cancel;
            try
            {
                async Task<ControlResponse> SendCommand()
                {
                    if (command == "run" && (request.Count is not null || request.Loop))
                    {
                        var status = await ControlPipe.SendAsync(new ControlRequest("status"), interrupt.Token, pipeName);
                        if (!status.Ok) return status;
                        if (status.Capabilities?.Contains(ControlProtocol.PlaybackRepeatCapability) != true)
                            return ControlResponse.Fail("unsupported", "This Studio version does not support --count/--loop. Restart using the updated Studio build.");
                    }
                    interrupt.Token.ThrowIfCancellationRequested();
                    // Keep the acceptance response even if Ctrl+C arrives in flight: we need its
                    // run ID to stop exactly this job, including while its startup delay is pending.
                    return await ControlPipe.SendAsync(request, stopLoopOnInterrupt ? CancellationToken.None : interrupt.Token, pipeName);
                }
                ControlResponse response;
                try { response = await SendCommand(); }
                catch (Exception ex) when (!interrupt.IsCancellationRequested && flags.Contains("--launch") && ex is IOException or OperationCanceledException)
                {
                    var registration = GlobalCommand.Read();
                    if (!File.Exists(registration?.StudioPath)) throw new IOException("Studio path is missing. Register the installed/portable version again.");
                    Process.Start(new ProcessStartInfo(registration!.StudioPath) { UseShellExecute = true, Arguments = "--tray" });
                    response = await WaitForStudio(SendCommand, interrupt.Token);
                }
                if (flags.Contains("--wait") && response.Ok && response.Job is { } job)
                {
                    var runId = job.RunId;
                    try
                    {
                        while (response.Job is { } current && !ControlProtocol.IsTerminal(current.State))
                        {
                            await Task.Delay(200, interrupt.Token);
                            response = await ControlPipe.SendAsync(new ControlRequest("status", RunId: runId), interrupt.Token, pipeName);
                            if (!response.Ok) break;
                        }
                    }
                    catch (OperationCanceledException) when (stopLoopOnInterrupt && interrupt.IsCancellationRequested)
                    { return await StopLoopAsync(runId, json, pipeName); }
                }
                Print(response, json);
                if (!response.Ok) return response.Code switch { "not_found" => 4, "ambiguous" => 5, "busy" => 6, "invalid" => 2, _ => 7 };
                return response.Job?.State switch { "cancelled" => 8, "failed" => 7, "busy" => 6, _ => 0 };
            }
            finally { Console.CancelKeyPress -= cancel; }
        }
        catch (ArgumentException ex) { Print(ControlResponse.Fail("invalid", ex.Message), json); return 2; }
        catch (OperationCanceledException) { Print(ControlResponse.Fail("unavailable", "Studio unavailable or wait interrupted. Start Studio, or use --launch."), json); return interrupted ? 130 : 3; }
        catch (Exception ex) { Print(ControlResponse.Fail("unavailable", ex.Message), json); return 3; }
    }
    private static async Task<int> StopLoopAsync(string runId, bool json, string? pipeName)
    {
        string Retry(string detail) => $"Could not confirm loop {runId} stopped: {detail} Retry: {GlobalCommand.CommandName} stop --run-id \"{runId}\"";
        try
        {
            // The Ctrl+C token is already cancelled. Repeated Ctrl+C must not interrupt cleanup.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var response = await ControlPipe.SendAsync(new ControlRequest("stop", RunId: runId), timeout.Token, pipeName);
            while (response.Ok && response.Job is { } current && !ControlProtocol.IsTerminal(current.State))
            {
                await Task.Delay(100, timeout.Token);
                response = await ControlPipe.SendAsync(new ControlRequest("status", RunId: runId), timeout.Token, pipeName);
            }
            if (!response.Ok || response.Job is null)
            {
                Print(ControlResponse.Fail("stop_failed", Retry(response.Message ?? response.Code)), json);
                return 7;
            }
            Print(response, json);
            return response.Job.State == "failed" ? 7 : 130;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            Print(ControlResponse.Fail("unavailable", Retry(ex.Message)), json);
            return 3;
        }
    }
    private static async Task<ControlResponse> WaitForStudio(Func<Task<ControlResponse>> send, CancellationToken token)
    {
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(60))
        {
            try { return await send(); }
            catch (Exception ex) when (!token.IsCancellationRequested && ex is IOException or OperationCanceledException) { await Task.Delay(300, token); }
        }
        throw new IOException("Studio did not become ready within 60 seconds.");
    }
    private static void Print(ControlResponse response, bool json)
    {
        if (json) { Console.WriteLine(JsonSerializer.Serialize(response, ControlProtocol.Json)); return; }
        if (!response.Ok) { Console.Error.WriteLine($"{response.Code}: {response.Message}"); return; }
        if (response.Macros is { } macros) foreach (var macro in macros) Console.WriteLine($"{macro.Id}\t{macro.Library}\t{macro.Folder}\t{macro.Name}");
        if (response.Job is { } job) PrintJob(job);
        if (response.Jobs is { } jobs) { Console.WriteLine($"busy={response.Busy}"); foreach (var item in jobs) PrintJob(item); }
    }
    private static void PrintJob(ControlJob job) => Console.WriteLine($"{job.RunId}\t{job.State}\t{job.Name}\tremaining={Math.Ceiling(job.RemainingMilliseconds)}ms\t{job.Error}");
}
