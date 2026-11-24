using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using MacroHid.Control;
using MacroHid.Core;
using MacroHid.Runtime;

internal static class ConsoleInterruptTests
{
    public static async Task<int> RunChildAsync(string[] args)
    {
        // This helper is created with CREATE_NEW_CONSOLE, hidden. Never broadcast Ctrl+C
        // from the parent test process, which might share a console with the user's shell.
        var clients = new uint[2];
        Check(GetConsoleProcessList(clients, 2) == 1 && clients[0] == Environment.ProcessId,
            "Ctrl+C test must own an isolated console.");
        using var signal = EventWaitHandle.OpenExisting(args[2]);
        using var observed = EventWaitHandle.OpenExisting(args[2] + "-observed");
        using var output = new StreamWriter(args[3]) { AutoFlush = true };
        Console.SetOut(output);
        Console.SetError(output);
        var sendSignal = Task.Run(async () =>
        {
            Check(signal.WaitOne(TimeSpan.FromSeconds(10)), "No test interrupt requested.");
            Check(GenerateConsoleCtrlEvent(0, 0), "Unable to send Ctrl+C.");
            if (args[4] == "twice")
            {
                await Task.Delay(50);
                Check(GenerateConsoleCtrlEvent(0, 0), "Unable to send repeated Ctrl+C.");
            }
        });
        ConsoleCancelEventHandler observe = (_, _) => observed.Set();
        Console.CancelKeyPress += observe;
        try
        {
            var exitCode = await MacroCtlProgram.RunAsync(args[5..], args[1]);
            await sendSignal;
            return exitCode;
        }
        finally { Console.CancelKeyPress -= observe; }
    }

    public static async Task RunAsync(MacroDocument sample)
    {
        await Scenario(sample, loop: true, interruptBeforeAcceptance: false, rejectStop: false);
        Console.WriteLine("PASS real console Ctrl+C stops only its running loop, confirms cleanup and tolerates repeated Ctrl+C");
        await Scenario(sample, loop: true, interruptBeforeAcceptance: true, rejectStop: false);
        Console.WriteLine("PASS real console Ctrl+C during acceptance cancels delayed loop before execution");
        await Scenario(sample, loop: false, interruptBeforeAcceptance: false, rejectStop: false);
        Console.WriteLine("PASS real console Ctrl+C leaves non-loop --wait jobs running");
        await Scenario(sample, loop: true, interruptBeforeAcceptance: false, rejectStop: true);
        Console.WriteLine("PASS real console Ctrl+C reports rejected stop with task ID and retry command");
    }

    private static async Task Scenario(MacroDocument sample, bool loop, bool interruptBeforeAcceptance, bool rejectStop)
    {
        var id = Guid.NewGuid().ToString("N");
        var pipeName = ControlPipe.Name + ".CtrlCTest." + id;
        var outputPath = Path.Combine(Path.GetTempPath(), "MacroHID-CtrlC-" + id + ".json");
        using var signal = new EventWaitHandle(false, EventResetMode.ManualReset, @"Local\MacroHID-CtrlC-" + id);
        using var observed = new EventWaitHandle(false, EventResetMode.ManualReset, @"Local\MacroHID-CtrlC-" + id + "-observed");
        var jobs = new ControlJobs();
        var macro = new ControlMacro("ctrl-c-test", "Ctrl+C loop", "Test", "");
        var unrelated = jobs.Schedule(macro, 60000, (_, _) => throw new Exception("Unrelated job executed."));
        var requests = new ConcurrentQueue<ControlRequest>();
        string? runId = null;
        int starts = 0;
        await using var server = new ControlPipeServer(pipeName);
        server.Start(async request =>
        {
            requests.Enqueue(request);
            if (request.Command == "run")
            {
                var job = jobs.Schedule(macro, request.DelayMilliseconds, async (token, started) =>
                {
                    Interlocked.Increment(ref starts);
                    started();
                    using var executor = new MacroPlaybackExecutor(new InertInputSink());
                    try
                    {
                        var options = new PlaybackExecutionOptions(PlaybackMode.ToggleLoop, 1,
                            PixelEvaluationMode.Live, false, PrecisionMode.Balanced);
                        var result = await executor.RunAsync(sample, options, token);
                        return new ControlExecution(result.Cancelled ? "cancelled" : "completed");
                    }
                    finally
                    {
                        // Cancellation acceptance is not completion. Require the CLI to keep
                        // polling through cleanup, including a second real console signal.
                        if (token.IsCancellationRequested) await Task.Delay(250);
                    }
                });
                runId = job.RunId;
                if (interruptBeforeAcceptance)
                {
                    signal.Set();
                    Check(await Task.Run(() => observed.WaitOne(TimeSpan.FromSeconds(10))),
                        "Ctrl+C was not observed before the acceptance response.");
                }
                return new(true, "accepted", Job: job);
            }
            if (request.Command == "stop")
            {
                if (rejectStop) return ControlResponse.Fail("test_rejected", "Stop rejected by test server.");
                Check(request.RunId == runId, "Stopped another job.");
                jobs.Cancel(request.RunId!);
                return new(true, "ok", Job: jobs.Get(request.RunId!));
            }
            if (request.Command == "status")
            {
                var job = request.RunId is null ? null : jobs.Get(request.RunId);
                if (job is not null && !interruptBeforeAcceptance) signal.Set();
                return new(true, "ok", Job: job, Capabilities: [ControlProtocol.PlaybackRepeatCapability]);
            }
            return ControlResponse.Fail("invalid", "Unexpected command.");
        });
        try
        {
            bool delayed = interruptBeforeAcceptance || !loop || rejectStop;
            var arguments = new List<string> { "--ctrl-c-cli", pipeName, @"Local\MacroHID-CtrlC-" + id,
                outputPath, loop && !delayed ? "twice" : "once", "run", "--id", macro.Id, "--wait", "--json" };
            if (loop) arguments.Add("--loop");
            else arguments.AddRange(["--count", "3"]);
            if (delayed) arguments.AddRange(["--delay", "1m"]);
            using var child = StartIsolatedConsole(arguments);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await child.WaitForExitAsync(timeout.Token); }
            finally { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); } }
            var output = await File.ReadAllTextAsync(outputPath);
            Check(child.ExitCode == (rejectStop ? 7 : 130), $"Unexpected Ctrl+C exit {child.ExitCode}: {output}");
            Check(runId is not null, "CLI did not start a job.");
            Check(jobs.Get(unrelated.RunId)!.State == "waiting", "Ctrl+C stopped an unrelated job.");
            var stops = requests.Where(r => r.Command == "stop").ToArray();
            Check(stops.Length == (loop ? 1 : 0), "Wrong number of stop requests.");
            if (loop) Check(stops[0].RunId == runId, "Stop lost its run ID.");
            using var json = JsonDocument.Parse(output);
            if (rejectStop)
            {
                Check(!json.RootElement.GetProperty("ok").GetBoolean() && output.Contains(runId!)
                    && output.Contains($"{GlobalCommand.CommandName} stop --run-id") && jobs.Get(runId!)!.State == "waiting",
                    "Rejected stop was reported as success or lost recovery instructions.");
            }
            else if (loop)
            {
                Check(json.RootElement.GetProperty("job").GetProperty("state").GetString() == "cancelled"
                    && jobs.Get(runId!)!.State == "cancelled", "CLI returned before loop cancellation finished.");
                Check(starts == (delayed ? 0 : 1), "Delayed loop executed or running loop never started.");
            }
            else Check(jobs.Get(runId!)!.State == "waiting", "Non-loop Ctrl+C cancelled the macro.");
        }
        finally
        {
            jobs.CancelAll();
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }

    private static Process StartIsolatedConsole(IEnumerable<string> arguments)
    {
        var executable = Path.ChangeExtension(Assembly.GetExecutingAssembly().Location, ".exe");
        var command = new StringBuilder(string.Join(" ", new[] { executable }.Concat(arguments).Select(a => "\"" + a + "\"")));
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Flags = 1, ShowWindow = 0 };
        if (!CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, false, 0x10, IntPtr.Zero, null, ref startup, out var info))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try { return Process.GetProcessById(info.ProcessId); }
        finally { CloseHandle(info.Thread); CloseHandle(info.Process); }
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, ReservedSize;
        public IntPtr ReservedData, StdInput, StdOutput, StdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo { public IntPtr Process, Thread; public int ProcessId, ThreadId; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string? directory,
        ref StartupInfo startup, out ProcessInfo process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GenerateConsoleCtrlEvent(uint signal, uint group);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetConsoleProcessList(uint[] processes, uint count);
}
