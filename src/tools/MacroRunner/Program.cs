using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using MacroHid.Core;
using Microsoft.Win32.SafeHandles;

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

NativeMethods.QueryPerformanceFrequency(out var qpcFrequency);
Func<PixelCondition, bool>? pixelEvaluator = options.PixelMode switch
{
    PixelMode.Skip => _ => false,
    PixelMode.MatchAll => _ => true,
    PixelMode.Live => ScreenPixelSampler.Matches,
    _ => throw new InvalidOperationException($"Unsupported pixel mode '{options.PixelMode}'.")
};

var reports = MacroReportCompiler.Compile(macro, startTick: 0, qpcFrequency, pixelEvaluator);

Console.WriteLine("MacroHID MacroRunner");
Console.WriteLine($"macro=\"{macro.Name}\" path=\"{Path.GetFullPath(options.MacroPath)}\"");
Console.WriteLine($"reports={reports.Count} pixelMode={options.PixelMode} send={options.Send}");

if (options.DryRun)
{
    PrintDryRun(reports, qpcFrequency);
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

using var client = MacroHidDeviceClient.OpenFirst();
if (client is null)
{
    Console.Error.WriteLine("MacroHID device not found. Install the test-signed driver before using --send.");
    return 2;
}

if (!client.Ping())
{
    Console.Error.WriteLine($"Driver ping failed. error={Marshal.GetLastWin32Error()}");
    return 3;
}

var histogram = new LatencyHistogram();
NativeMethods.QueryPerformanceCounter(out var startTick);

for (var i = 0; i < reports.Count; i++)
{
    var report = reports[i];
    var dueTick = startTick + report.DueTick;
    WaitUntil(dueTick, qpcFrequency, options.NoWait);

    if (!client.SubmitReport((uint)(i + 1), report.Report))
    {
        Console.Error.WriteLine($"Submit report #{i + 1} failed. error={Marshal.GetLastWin32Error()}");
        return 4;
    }

    NativeMethods.QueryPerformanceCounter(out var submittedTick);
    histogram.RecordMicroseconds(ToMicroseconds(Math.Max(0, submittedTick - dueTick), qpcFrequency));
}

var stats = client.GetStats();
if (stats.HasValue)
{
    Console.WriteLine(
        string.Create(
            CultureInfo.InvariantCulture,
            $"driverReportsSubmitted={stats.Value.ReportsSubmitted} driverReportsRejected={stats.Value.ReportsRejected} lastStatus=0x{stats.Value.LastNtStatus:X8}"));
}

Console.WriteLine($"submitLatency {histogram.Summary()}");
return 0;

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

static void WaitUntil(long dueTick, long qpcFrequency, bool noWait)
{
    if (noWait)
    {
        return;
    }

    while (true)
    {
        NativeMethods.QueryPerformanceCounter(out var now);
        var remainingTicks = dueTick - now;
        if (remainingTicks <= 0)
        {
            return;
        }

        var remainingUs = ToMicroseconds(remainingTicks, qpcFrequency);
        if (remainingUs > 2_000)
        {
            Thread.Sleep(Math.Max(0, (int)(remainingUs / 1_000) - 1));
        }
        else if (remainingUs > 200)
        {
            Thread.Yield();
        }
        else
        {
            Thread.SpinWait(64);
        }
    }
}

static long ToMicroseconds(long ticks, long qpcFrequency)
{
    return (long)Math.Round(ticks * 1_000_000.0 / qpcFrequency, MidpointRounding.AwayFromZero);
}

internal enum PixelMode
{
    Skip,
    MatchAll,
    Live
}

internal sealed record RunnerOptions(
    string? MacroPath,
    bool Send,
    bool DryRun,
    bool NoWait,
    PixelMode PixelMode,
    bool ShowHelp)
{
    public static RunnerOptions Parse(string[] args)
    {
        string? macroPath = null;
        var send = false;
        var dryRun = true;
        var noWait = false;
        var pixelMode = PixelMode.Skip;
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

    private static PixelMode ParsePixelMode(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "skip" => PixelMode.Skip,
            "match" => PixelMode.MatchAll,
            "live" => PixelMode.Live,
            _ => throw new ArgumentException($"Unsupported pixel mode '{value}'.")
        };
    }
}

internal sealed class MacroHidDeviceClient : IDisposable
{
    private readonly SafeFileHandle handle;

    private MacroHidDeviceClient(SafeFileHandle handle)
    {
        this.handle = handle;
    }

    public static MacroHidDeviceClient? OpenFirst()
    {
        var path = FindFirstDevicePath();
        if (path is null)
        {
            return null;
        }

        var handle = NativeMethods.CreateFileW(
            path,
            NativeMethods.GenericRead | NativeMethods.GenericWrite,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
            IntPtr.Zero,
            NativeMethods.OpenExisting,
            NativeMethods.FileAttributeNormal,
            IntPtr.Zero);

        return handle.IsInvalid ? null : new MacroHidDeviceClient(handle);
    }

    public bool Ping()
    {
        return NativeMethods.DeviceIoControl(
            handle,
            MacroHidProtocol.IoctlPing,
            IntPtr.Zero,
            0,
            IntPtr.Zero,
            0,
            out _,
            IntPtr.Zero);
    }

    public bool SubmitReport(uint sequence, byte[] report)
    {
        if (report.Length == 0 || report.Length > MacroHidProtocol.MaxReportSize)
        {
            throw new ArgumentOutOfRangeException(nameof(report), "Report length is outside the MacroHID protocol limits.");
        }

        NativeMethods.QueryPerformanceCounter(out var qpc);
        var packet = new MacroHidReportPacket
        {
            Size = (uint)Marshal.SizeOf<MacroHidReportPacket>(),
            Sequence = sequence,
            HostQpc = qpc,
            ReportId = report[0],
            ReportLength = (byte)report.Length,
            Report = new byte[MacroHidProtocol.MaxReportSize]
        };
        Array.Copy(report, packet.Report, report.Length);

        var packetSize = Marshal.SizeOf<MacroHidReportPacket>();
        var packetBuffer = Marshal.AllocHGlobal(packetSize);
        try
        {
            Marshal.StructureToPtr(packet, packetBuffer, false);
            return NativeMethods.DeviceIoControl(
                handle,
                MacroHidProtocol.IoctlSubmitReport,
                packetBuffer,
                (uint)packetSize,
                IntPtr.Zero,
                0,
                out _,
                IntPtr.Zero);
        }
        finally
        {
            Marshal.DestroyStructure<MacroHidReportPacket>(packetBuffer);
            Marshal.FreeHGlobal(packetBuffer);
        }
    }

    public MacroHidDriverStats? GetStats()
    {
        var statsSize = Marshal.SizeOf<MacroHidDriverStats>();
        var statsBuffer = Marshal.AllocHGlobal(statsSize);
        try
        {
            if (!NativeMethods.DeviceIoControl(
                    handle,
                    MacroHidProtocol.IoctlGetStats,
                    IntPtr.Zero,
                    0,
                    statsBuffer,
                    (uint)statsSize,
                    out var bytesReturned,
                    IntPtr.Zero)
                || bytesReturned != statsSize)
            {
                return null;
            }

            return Marshal.PtrToStructure<MacroHidDriverStats>(statsBuffer);
        }
        finally
        {
            Marshal.FreeHGlobal(statsBuffer);
        }
    }

    public void Dispose()
    {
        handle.Dispose();
    }

    private static string? FindFirstDevicePath()
    {
        var interfaceGuid = new Guid(MacroHidProtocol.DeviceInterfaceGuid);
        var deviceInfo = NativeMethods.SetupDiGetClassDevsW(
            ref interfaceGuid,
            IntPtr.Zero,
            IntPtr.Zero,
            NativeMethods.DigcfPresent | NativeMethods.DigcfDeviceInterface);

        if (deviceInfo == NativeMethods.InvalidHandleValue)
        {
            return null;
        }

        try
        {
            var interfaceData = new SpDeviceInterfaceData
            {
                CbSize = (uint)Marshal.SizeOf<SpDeviceInterfaceData>()
            };

            if (!NativeMethods.SetupDiEnumDeviceInterfaces(deviceInfo, IntPtr.Zero, ref interfaceGuid, 0, ref interfaceData))
            {
                return null;
            }

            NativeMethods.SetupDiGetDeviceInterfaceDetailW(
                deviceInfo,
                ref interfaceData,
                IntPtr.Zero,
                0,
                out var requiredSize,
                IntPtr.Zero);

            if (requiredSize == 0)
            {
                return null;
            }

            var detailBuffer = Marshal.AllocHGlobal((int)requiredSize);
            try
            {
                Marshal.WriteInt32(detailBuffer, IntPtr.Size == 8 ? 8 : 6);
                if (!NativeMethods.SetupDiGetDeviceInterfaceDetailW(
                        deviceInfo,
                        ref interfaceData,
                        detailBuffer,
                        requiredSize,
                        out _,
                        IntPtr.Zero))
                {
                    return null;
                }

                return Marshal.PtrToStringUni(IntPtr.Add(detailBuffer, 4));
            }
            finally
            {
                Marshal.FreeHGlobal(detailBuffer);
            }
        }
        finally
        {
            NativeMethods.SetupDiDestroyDeviceInfoList(deviceInfo);
        }
    }
}

internal static class ScreenPixelSampler
{
    public static bool Matches(PixelCondition condition)
    {
        return TrySample(condition.Coordinate, out var sample) && condition.Matches(sample);
    }

    private static bool TrySample(PixelCoordinate coordinate, out PixelSample sample)
    {
        sample = new PixelSample(coordinate.X, coordinate.Y, new RgbColor(0, 0, 0));
        var x = coordinate.X;
        var y = coordinate.Y;

        if (coordinate.Scope == CoordinateScope.Window)
        {
            if (string.IsNullOrWhiteSpace(coordinate.WindowTitle))
            {
                return false;
            }

            var window = NativeMethods.FindWindowW(null, coordinate.WindowTitle);
            if (window == IntPtr.Zero || !NativeMethods.GetWindowRect(window, out var rect))
            {
                return false;
            }

            x += rect.Left;
            y += rect.Top;
        }

        var desktopDc = NativeMethods.GetDC(IntPtr.Zero);
        if (desktopDc == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var colorRef = NativeMethods.GetPixel(desktopDc, x, y);
            if (colorRef == NativeMethods.ClrInvalid)
            {
                return false;
            }

            sample = new PixelSample(
                x,
                y,
                new RgbColor(
                    (byte)(colorRef & 0xFF),
                    (byte)((colorRef >> 8) & 0xFF),
                    (byte)((colorRef >> 16) & 0xFF)));
            return true;
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, desktopDc);
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct MacroHidReportPacket
{
    public uint Size;
    public uint Sequence;
    public long HostQpc;
    public byte ReportId;
    public byte ReportLength;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = MacroHidProtocol.MaxReportSize)]
    public byte[] Report;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MacroHidDriverStats
{
    public uint ProtocolVersion;
    public uint ReportsSubmitted;
    public uint ReportsRejected;
    public uint LastNtStatus;
    public long LastSubmitQpc;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SpDeviceInterfaceData
{
    public uint CbSize;
    public Guid InterfaceClassGuid;
    public uint Flags;
    public UIntPtr Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeRect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

internal static partial class NativeMethods
{
    public const uint GenericRead = 0x80000000;
    public const uint GenericWrite = 0x40000000;
    public const uint FileShareRead = 0x00000001;
    public const uint FileShareWrite = 0x00000002;
    public const uint OpenExisting = 3;
    public const uint FileAttributeNormal = 0x00000080;
    public const uint DigcfPresent = 0x00000002;
    public const uint DigcfDeviceInterface = 0x00000010;
    public const int ClrInvalid = -1;
    public static readonly IntPtr InvalidHandleValue = new(-1);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryPerformanceCounter(out long performanceCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryPerformanceFrequency(out long frequency);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint ioControlCode,
        IntPtr inBuffer,
        uint inBufferSize,
        IntPtr outBuffer,
        uint outBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr SetupDiGetClassDevsW(
        ref Guid classGuid,
        IntPtr enumerator,
        IntPtr hwndParent,
        uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet,
        IntPtr deviceInfoData,
        ref Guid interfaceClassGuid,
        uint memberIndex,
        ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiGetDeviceInterfaceDetailW(
        IntPtr deviceInfoSet,
        ref SpDeviceInterfaceData deviceInterfaceData,
        IntPtr deviceInterfaceDetailData,
        uint deviceInterfaceDetailDataSize,
        out uint requiredSize,
        IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr FindWindowW(string? className, string? windowName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern int GetPixel(IntPtr hdc, int x, int y);
}
