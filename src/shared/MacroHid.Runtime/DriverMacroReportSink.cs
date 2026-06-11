using System.Runtime.InteropServices;
using MacroHid.Core;
using Microsoft.Win32.SafeHandles;

namespace MacroHid.Runtime;

public sealed record MacroDriverStats(
    uint ProtocolVersion,
    uint ReportsSubmitted,
    uint ReportsRejected,
    uint LastNtStatus,
    long LastSubmitQpc);

public sealed class DriverMacroReportSink : IMacroReportSink, IDisposable
{
    private readonly SafeFileHandle handle;

    private DriverMacroReportSink(SafeFileHandle handle)
    {
        this.handle = handle;
    }

    public bool IsAvailable => !handle.IsInvalid && Ping();

    public static DriverMacroReportSink? OpenFirst()
    {
        var path = FindFirstDevicePath();
        if (path is null)
        {
            return null;
        }

        var handle = RuntimeNativeMethods.CreateFileW(
            path,
            RuntimeNativeMethods.GenericRead | RuntimeNativeMethods.GenericWrite,
            RuntimeNativeMethods.FileShareRead | RuntimeNativeMethods.FileShareWrite,
            IntPtr.Zero,
            RuntimeNativeMethods.OpenExisting,
            RuntimeNativeMethods.FileAttributeNormal,
            IntPtr.Zero);

        return handle.IsInvalid ? null : new DriverMacroReportSink(handle);
    }

    public void Submit(uint sequence, byte[] report)
    {
        if (report.Length == 0 || report.Length > MacroHidProtocol.MaxReportSize)
        {
            throw new ArgumentOutOfRangeException(nameof(report), "Report length is outside the MacroHID protocol limits.");
        }

        RuntimeNativeMethods.QueryPerformanceCounter(out var qpc);
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
            if (!RuntimeNativeMethods.DeviceIoControl(
                    handle,
                    MacroHidProtocol.IoctlSubmitReport,
                    packetBuffer,
                    (uint)packetSize,
                    IntPtr.Zero,
                    0,
                    out _,
                    IntPtr.Zero))
            {
                throw new InvalidOperationException($"Submit report failed. error={Marshal.GetLastWin32Error()}");
            }
        }
        finally
        {
            Marshal.DestroyStructure<MacroHidReportPacket>(packetBuffer);
            Marshal.FreeHGlobal(packetBuffer);
        }
    }

    public MacroDriverStats? GetStats()
    {
        var statsSize = Marshal.SizeOf<MacroHidDriverStats>();
        var statsBuffer = Marshal.AllocHGlobal(statsSize);
        try
        {
            if (!RuntimeNativeMethods.DeviceIoControl(
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

            var stats = Marshal.PtrToStructure<MacroHidDriverStats>(statsBuffer);
            return new MacroDriverStats(
                stats.ProtocolVersion,
                stats.ReportsSubmitted,
                stats.ReportsRejected,
                stats.LastNtStatus,
                stats.LastSubmitQpc);
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

    private bool Ping()
    {
        return RuntimeNativeMethods.DeviceIoControl(
            handle,
            MacroHidProtocol.IoctlPing,
            IntPtr.Zero,
            0,
            IntPtr.Zero,
            0,
            out _,
            IntPtr.Zero);
    }

    private static string? FindFirstDevicePath()
    {
        var interfaceGuid = new Guid(MacroHidProtocol.DeviceInterfaceGuid);
        var deviceInfo = RuntimeNativeMethods.SetupDiGetClassDevsW(
            ref interfaceGuid,
            IntPtr.Zero,
            IntPtr.Zero,
            RuntimeNativeMethods.DigcfPresent | RuntimeNativeMethods.DigcfDeviceInterface);

        if (deviceInfo == RuntimeNativeMethods.InvalidHandleValue)
        {
            return null;
        }

        try
        {
            var interfaceData = new SpDeviceInterfaceData
            {
                CbSize = (uint)Marshal.SizeOf<SpDeviceInterfaceData>()
            };

            if (!RuntimeNativeMethods.SetupDiEnumDeviceInterfaces(deviceInfo, IntPtr.Zero, ref interfaceGuid, 0, ref interfaceData))
            {
                return null;
            }

            RuntimeNativeMethods.SetupDiGetDeviceInterfaceDetailW(
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
                if (!RuntimeNativeMethods.SetupDiGetDeviceInterfaceDetailW(
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
            RuntimeNativeMethods.SetupDiDestroyDeviceInfoList(deviceInfo);
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
