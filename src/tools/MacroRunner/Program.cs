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
var actions = InputActionCompiler.Compile(macro, startTick: 0, Stopwatch.Frequency, pixelEvaluator);

Console.WriteLine("MacroHID MacroRunner");
Console.WriteLine($"macro=\"{macro.Name}\" path=\"{Path.GetFullPath(options.MacroPath)}\"");
Console.WriteLine($"actions={actions.Count} pixelMode={options.PixelMode} send={options.Send}");

if (options.DryRun)
{
    PrintDryRun(actions, Stopwatch.Frequency);
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

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

var executor = new MacroPlaybackExecutor(new SendInputMacroSink());
var runResult = await executor.RunAsync(
    macro,
    new PlaybackExecutionOptions(PlaybackMode.FixedCount, Count: 1, options.PixelMode, options.NoWait),
    cancellation.Token);

if (runResult.Status == PlaybackRunStatus.InputUnavailable)
{
    Console.Error.WriteLine("SendInput backend is unavailable on this system.");
    return 2;
}

if (runResult.InputStats is not null)
{
    Console.WriteLine(
        string.Create(
            CultureInfo.InvariantCulture,
            $"inputActionsSubmitted={runResult.InputStats.ActionsSubmitted} nativeInputsSubmitted={runResult.InputStats.NativeInputsSubmitted} failures={runResult.InputStats.FailedSubmissions} lastError={runResult.InputStats.LastWin32Error}"));
}

Console.WriteLine(
    string.Create(
        CultureInfo.InvariantCulture,
        $"execution iterations={runResult.IterationsCompleted} actions={runResult.ActionsSubmitted} cancelled={runResult.Cancelled}"));
return runResult.Cancelled ? 130 : 0;

static void PrintDryRun(IReadOnlyList<ScheduledInputAction> actions, long qpcFrequency)
{
    for (var i = 0; i < actions.Count; i++)
    {
        var action = actions[i];
        var dueUs = ToMicroseconds(action.DueTick, qpcFrequency);
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"#{i + 1:000000} due={dueUs}us action=\"{DescribeAction(action.Action)}\""));
    }
}

static string DescribeAction(InputAction action)
{
    return action switch
    {
        KeyInputAction key => $"key.{key.Kind} key={key.Key} modifiers={key.Modifiers}",
        TextInputAction text => $"key.text length={text.Text.Length}",
        MouseMoveInputAction move => $"mouse.move mode={move.Mode} x={move.X} y={move.Y} buttons={move.Buttons}",
        MouseButtonInputAction button => $"mouse.button {button.Kind} button={button.Button}",
        MouseWheelInputAction wheel => $"mouse.wheel vertical={wheel.Vertical} horizontal={wheel.Horizontal}",
        ConsumerInputAction consumer => $"consumer.{consumer.Kind} control={consumer.Control}",
        _ => action.GetType().Name
    };
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
              --send submits input through the Windows SendInput backend.
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
