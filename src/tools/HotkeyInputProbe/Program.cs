using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using MacroHid.Core;
using MacroStudio;

// What the hotkey front end costs everyone else: SendInput duration for keys and mouse moves while MacroStudio's
// GlobalKeyboardHook is running in a given mode, compared with no hook at all. The keys injected are F24 (no default
// binding); injected input is ignored by the hook in both modes, as it is in MacroStudio.
//
//   HotkeyInputProbe --mode none|hook|raw --samples 1500
string Option(string key, string fallback)
{
    int at = Array.IndexOf(args, key);
    return at >= 0 && at + 1 < args.Length ? args[at + 1] : fallback;
}

var mode = Option("--mode", "hook");
int samples = int.Parse(Option("--samples", "1500"), CultureInfo.InvariantCulture);
using var hook = new GlobalKeyboardHook
{
    Mode = mode == "raw" ? HotkeyInputMode.RawInput : HotkeyInputMode.LowLevelHook
};
if (mode != "none")
{
    // A realistic binding set: colour sample (UpArrow, swallowed), a force-stop chord and one macro hotkey.
    hook.Start([
        new HotkeyBinding(CoreHotkeys.ColorSampleId, CoreHotkeys.DefaultColorSample),
        new HotkeyBinding(CoreHotkeys.ForceStopId, new HotkeyGesture(HidModifier.LeftCtrl, HidKey.F12)),
        new HotkeyBinding("macro", new HotkeyGesture(HidModifier.None, HidKey.F8))]);
    Console.WriteLine($"active={hook.ActiveMode}");
}

Thread.CurrentThread.Priority = ThreadPriority.Highest;
var size = Marshal.SizeOf<Input>();
var down = new[] { new Input { Type = 1, Ki = new Ki { Vk = 0x87 } } };
var up = new[] { new Input { Type = 1, Ki = new Ki { Vk = 0x87, Flags = 2 } } };
var move = new[] { new Input { Type = 0, Mi = new Mi { Flags = 1 } } };
for (int i = 0; i < 50; i++) { SendInput(1, down, size); SendInput(1, up, size); SendInput(1, move, size); }
var key = new double[samples];
var mouse = new double[samples];
for (int i = 0; i < samples; i++)
{
    Thread.Sleep(1);
    long a = Stopwatch.GetTimestamp(); SendInput(1, down, size); long b = Stopwatch.GetTimestamp();
    SendInput(1, up, size);
    long c = Stopwatch.GetTimestamp(); SendInput(1, move, size); long d = Stopwatch.GetTimestamp();
    key[i] = (b - a) * 1e6 / Stopwatch.Frequency;
    mouse[i] = (d - c) * 1e6 / Stopwatch.Frequency;
}
string Summary(double[] values)
{
    var sorted = values.OrderBy(v => v).ToArray();
    double P(double q) => sorted[Math.Min(sorted.Length - 1, (int)(q * sorted.Length))];
    return string.Create(CultureInfo.InvariantCulture, $"p50={P(.5):F1} p90={P(.9):F1} p99={P(.99):F1} p999={P(.999):F1} max={sorted[^1]:F1}");
}
Console.WriteLine($"SUMMARY mode={mode} key {Summary(key)}");
Console.WriteLine($"SUMMARY mode={mode} move {Summary(mouse)}");
hook.Stop();
return 0;

[DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, Input[] inputs, int size);

[StructLayout(LayoutKind.Explicit, Size = 40)]
struct Input { [FieldOffset(0)] public uint Type; [FieldOffset(8)] public Ki Ki; [FieldOffset(8)] public Mi Mi; }
[StructLayout(LayoutKind.Sequential)] struct Ki { public ushort Vk, Scan; public uint Flags, Time; public IntPtr Extra; }
[StructLayout(LayoutKind.Sequential)] struct Mi { public int Dx, Dy; public uint Data, Flags, Time; public IntPtr Extra; }
