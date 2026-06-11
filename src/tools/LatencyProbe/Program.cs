using System.Diagnostics;
using MacroHid.Core;
using MacroHid.Runtime;

var iterations = ParseInt(args, "--iterations", 2_000);
var intervalUs = ParseInt(args, "--interval-us", 1_000);
var warmup = ParseInt(args, "--warmup", 100);

Console.WriteLine("MacroHID LatencyProbe");
Console.WriteLine($"iterations={iterations} interval={intervalUs}us warmup={warmup}");
Console.WriteLine($"timerFrequency={Stopwatch.Frequency} ticks/sec highResolution={Stopwatch.IsHighResolution}");

Thread.CurrentThread.Priority = ThreadPriority.Highest;

var histogram = new LatencyHistogram();
var stopwatch = Stopwatch.StartNew();
var intervalTicks = intervalUs * Stopwatch.Frequency / 1_000_000;
var nextTick = stopwatch.ElapsedTicks + intervalTicks;

for (var i = -warmup; i < iterations; i++)
{
    WaitUntil(stopwatch, nextTick);
    var actualTick = stopwatch.ElapsedTicks;

    if (i >= 0)
    {
        var deltaUs = Math.Abs(actualTick - nextTick) * 1_000_000 / Stopwatch.Frequency;
        histogram.RecordMicroseconds(deltaUs);
    }

    // Exercise SendInput encoding in the hot path so the probe resembles local submission work.
    _ = SendInputEncoder.Encode(new KeyInputAction(KeyActionKind.Down, HidKey.A, HidModifier.None));
    nextTick += intervalTicks;
}

Console.WriteLine(histogram.Summary());
Console.WriteLine("Note: this probe measures user-mode scheduler jitter and encoding overhead only.");

static void WaitUntil(Stopwatch stopwatch, long targetTick)
{
    while (true)
    {
        var remaining = targetTick - stopwatch.ElapsedTicks;
        if (remaining <= 0)
        {
            return;
        }

        var remainingUs = remaining * 1_000_000 / Stopwatch.Frequency;
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
