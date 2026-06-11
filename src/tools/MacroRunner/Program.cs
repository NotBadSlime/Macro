using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using MacroHid.Core;
using MacroHid.Runtime;

RunnerOptions options;
try
{
    options = RunnerOptions.Parse(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    RunnerOptions.PrintUsage();
    return 1;
}

if (options.ShowHelp || options.MacroPath is null)
{
    RunnerOptions.PrintUsage();
    return options.ShowHelp ? 0 : 1;
}

if (!File.Exists(options.MacroPath))
{
    Console.Error.WriteLine($"Macro file not found: {options.MacroPath}");
    return 1;
}

var json = File.ReadAllText(options.MacroPath);
var macro = McrxParser.Parse(json);
Func<PixelCondition, bool> pixelEvaluator = options.PixelMode switch
{
    PixelEvaluationMode.Skip => _ => false,
    PixelEvaluationMode.MatchAll => _ => true,
    PixelEvaluationMode.Live => ScreenPixelSampler.Matches,
    _ => throw new InvalidOperationException($"Unsupported pixel mode '{options.PixelMode}'.")
};
var reports = MacroReportCompiler.Compile(macro, startTick: 0, Stopwatch.Frequency, pixelEvaluator);

Console.WriteLine("MacroHID MacroRunner");
Console.WriteLine($"macro=\"{macro.Name}\" path=\"{Path.GetFullPath(options.MacroPath)}\"");
Console.WriteLine($"reports={reports.Count} pixelMode={options.PixelMode} send={options.Send}");

if (options.DryRun)
{
    PrintDryRun(reports, Stopwatch.Frequency);
}

if (!options.Send)
{
    return 0;
}

try
{
    Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;
    Thread.CurrentThread.Priority = ThreadPriority.Highest;
}
catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
{
    Console.Error.WriteLine($"Warning: failed to raise priority: {ex.Message}");
}

using var driverSink = DriverMacroReportSink.OpenFirst();
if (driverSink is null)
{
    Console.Error.WriteLine("MacroHID device not found. Install the test-signed driver before using --send.");
    return 2;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

var executor = new MacroPlaybackExecutor(driverSink);
var runResult = await executor.RunAsync(
    macro,
    new PlaybackExecutionOptions(PlaybackMode.FixedCount, Count: 1, options.PixelMode, options.NoWait),
    cancellation.Token);

if (runResult.Status == PlaybackRunStatus.DriverMissing)
{
    Console.Error.WriteLine("MacroHID device not found. Install the test-signed driver before using --send.");
    return 2;
}

if (runResult.DriverStats is not null)
{
    Console.WriteLine(
        string.Create(
            CultureInfo.InvariantCulture,
            $"driverReportsSubmitted={runResult.DriverStats.ReportsSubmitted} driverReportsRejected={runResult.DriverStats.ReportsRejected} lastStatus=0x{runResult.DriverStats.LastNtStatus:X8}"));
}

Console.WriteLine(
    string.Create(
        CultureInfo.InvariantCulture,
        $"execution iterations={runResult.IterationsCompleted} reports={runResult.ReportsSubmitted} cancelled={runResult.Cancelled}"));
return runResult.Cancelled ? 130 : 0;

static void PrintDryRun(IReadOnlyList<ScheduledHidReport> reports, long qpcFrequency)
{
    for (var i = 0; i < reports.Count; i++)
    {
        var report = reports[i];
        var dueUs = ToMicroseconds(report.DueTick, qpcFrequency);
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"#{i + 1:000000} due={dueUs}us id=0x{report.ReportId:X2} bytes={Convert.ToHexString(report.Report)}"));
    }
}

static long ToMicroseconds(long ticks, long qpcFrequency)
{
    return (long)Math.Round(ticks * 1_000_000.0 / qpcFrequency, MidpointRounding.AwayFromZero);
}

internal sealed record RunnerOptions(
    string? MacroPath,
    bool Send,
    bool DryRun,
    bool NoWait,
    PixelEvaluationMode PixelMode,
    bool ShowHelp)
{
    public static RunnerOptions Parse(string[] args)
    {
        string? macroPath = null;
        var send = false;
        var dryRun = true;
        var noWait = false;
        var pixelMode = PixelEvaluationMode.Skip;
        var showHelp = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-h":
                case "--help":
                    showHelp = true;
                    break;
                case "--macro":
                    macroPath = RequireValue(args, ref i, "--macro");
                    break;
                case "--send":
                    send = true;
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--no-dry-run":
                    dryRun = false;
                    break;
                case "--no-wait":
                    noWait = true;
                    break;
                case "--pixels":
                    pixelMode = ParsePixelMode(RequireValue(args, ref i, "--pixels"));
                    break;
                default:
                    if (arg.StartsWith("--pixels=", StringComparison.OrdinalIgnoreCase))
                    {
                        pixelMode = ParsePixelMode(arg["--pixels=".Length..]);
                    }
                    else if (arg.StartsWith("-", StringComparison.Ordinal))
                    {
                        throw new ArgumentException($"Unsupported option '{arg}'.");
                    }
                    else if (macroPath is null)
                    {
                        macroPath = arg;
                    }
                    else
                    {
                        throw new ArgumentException($"Unexpected argument '{arg}'.");
                    }

                    break;
            }
        }

        return new RunnerOptions(macroPath, send, dryRun, noWait, pixelMode, showHelp);
    }

    public static void PrintUsage()
    {
        Console.WriteLine(
            """
            Usage:
              MacroRunner --macro <file.mcrx> [--dry-run] [--send] [--pixels skip|match|live] [--no-wait]

            Defaults:
              --dry-run is enabled.
              --pixels skip keeps pixel.when branches deterministic unless live sampling is requested.
              --send submits reports to the MacroHID driver after opening the device and pinging it.
              Press Ctrl+C while --send is running to request cancellation.
            """);
    }

    private static string RequireValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"{option} requires a value.");
        }

        index++;
        return args[index];
    }

    private static PixelEvaluationMode ParsePixelMode(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "skip" => PixelEvaluationMode.Skip,
            "match" => PixelEvaluationMode.MatchAll,
            "live" => PixelEvaluationMode.Live,
            _ => throw new ArgumentException($"Unsupported pixel mode '{value}'.")
        };
    }
}
