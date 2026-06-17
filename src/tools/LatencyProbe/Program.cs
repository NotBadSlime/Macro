using System.Diagnostics;
using System.Runtime;
using MacroHid.Core;
using MacroHid.Runtime;

var iterations = ParseInt(args, "--iterations", 2_000);
var intervalUs = ParseInt(args, "--interval-us", 1_000);
var warmup = ParseInt(args, "--warmup", 100);
var legacy = args.Any(a => string.Equals(a, "--legacy", StringComparison.OrdinalIgnoreCase));

Console.WriteLine("MacroHID LatencyProbe");
Console.WriteLine($"iterations={iterations} interval={intervalUs}us warmup={warmup} mode={(!legacy ? "precision" : "legacy")}");
Console.WriteLine($"timerFrequency={Stopwatch.Frequency} ticks/sec highResolution={Stopwatch.IsHighResolution}");

using var precisionContext = legacy
    ? null
    : PrecisionPlaybackContext.Enter(PrecisionMode.ExtremeDuringPlayback);

if (!legacy && OperatingSystem.IsWindows())
{
    RuntimeNativeMethods.NtQueryTimerResolution(out var minRes, out var maxRes, out var curRes);
    Console.WriteLine($"timerResolution min={minRes * 100}ns max={maxRes * 100}ns current={curRes * 100}ns");
    Console.WriteLine($"gcLatencyMode={GCSettings.LatencyMode}");
}

var clock = new QpcHighResolutionClock();
var delayStrategy = new QpcPlaybackDelayStrategy(clock);
Console.WriteLine($"clockFrequency={clock.Frequency}");
Console.WriteLine();

var scheduleJitter = new LatencyHistogram();
var submitDuration = new LatencyHistogram();
var encodeCost = new LatencyHistogram();
var conditionTrigger = new LatencyHistogram();
var intervalTicks = Math.Max(1, intervalUs * clock.Frequency / 1_000_000);
var nextTick = clock.GetTimestamp() + intervalTicks;
var noInputSink = new SendInputMacroSink();
var emptyPrepared = PreparedInputBatch.FromActions(Array.Empty<InputAction>());

InputAction[] probeActions =
[
    new KeyInputAction(KeyActionKind.Down, HidKey.A, HidModifier.None),
    new KeyInputAction(KeyActionKind.Up, HidKey.A, HidModifier.None)
];

using var cancellation = new CancellationTokenSource();

for (var i = -warmup; i < iterations; i++)
{
    if (legacy)
    {
        LegacyWaitUntil(nextTick, clock);
    }
    else
    {
        delayStrategy.WaitUntil(nextTick, clock.Frequency, cancellation.Token, noWait: false);
    }

    var actualTick = clock.GetTimestamp();
    var scheduleDeltaUs = Math.Abs(ToMicroseconds(actualTick - nextTick, clock.Frequency));

    var encodeStart = clock.GetTimestamp();
    _ = PreparedInputBatch.FromActions(probeActions);
    var encodeEnd = clock.GetTimestamp();

    var submitStart = clock.GetTimestamp();
    if (OperatingSystem.IsWindows())
    {
        noInputSink.SubmitPrepared(0, emptyPrepared);
    }
    var submitEnd = clock.GetTimestamp();

    var conditionStart = clock.GetTimestamp();
    var conditionDue = conditionStart + Math.Max(1, intervalTicks / 4);
    if (!legacy)
    {
        delayStrategy.WaitUntil(conditionDue, clock.Frequency, cancellation.Token, noWait: false);
    }
    else
    {
        LegacyWaitUntil(conditionDue, clock);
    }

    var conditionEnd = clock.GetTimestamp();

    if (i >= 0)
    {
        scheduleJitter.RecordMicroseconds(scheduleDeltaUs);
        encodeCost.RecordMicroseconds(Math.Abs(ToMicroseconds(encodeEnd - encodeStart, clock.Frequency)));
        submitDuration.RecordMicroseconds(Math.Abs(ToMicroseconds(submitEnd - submitStart, clock.Frequency)));
        conditionTrigger.RecordMicroseconds(Math.Abs(ToMicroseconds(conditionEnd - conditionDue, clock.Frequency)));
    }

    nextTick += intervalTicks;
}

Console.WriteLine($"scheduleJitter {scheduleJitter.Summary()}");
Console.WriteLine($"submitDuration {submitDuration.Summary()}");
Console.WriteLine($"encodeCost {encodeCost.Summary()}");
Console.WriteLine($"conditionTrigger {conditionTrigger.Summary()}");
Console.WriteLine("Note: this probe measures user-mode scheduler jitter and prepared SendInput overhead without sending real key events.");

static void LegacyWaitUntil(long targetTick, IHighResolutionClock clock)
{
    while (true)
    {
        var remaining = targetTick - clock.GetTimestamp();
        if (remaining <= 0)
        {
            return;
        }

        var remainingUs = remaining * 1_000_000 / clock.Frequency;
        if (remainingUs > 2_000)
        {
            Thread.Sleep(1);
        }
        else if (remainingUs > 200)
        {
            Thread.Yield();
        }
        else
        {
            Thread.SpinWait(32);
        }
    }
}

static long ToMicroseconds(long ticks, long frequency)
{
    return frequency <= 0 ? 0 : (long)Math.Round(ticks * 1_000_000.0 / frequency, MidpointRounding.AwayFromZero);
}

static int ParseInt(string[] args, string name, int defaultValue)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(args[i + 1], out var value)
            && value > 0)
        {
            return value;
        }
    }

    return defaultValue;
}
