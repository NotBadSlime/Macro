namespace MacroHid.Core;

public static class MacroHidProtocol
{
    public const int Version = 1;
    public const int MaxReportSize = 64;
    public const byte KeyboardReportId = 0x01;
    public const byte MouseReportId = 0x02;
    public const byte ConsumerReportId = 0x03;
    public const string DeviceInterfaceGuid = "{7F6E65AB-43E6-4ED1-8F0D-3891D94F6270}";

    public const uint FileDeviceMacroHid = 0x8000;
    public const uint MethodBuffered = 0;
    public const uint FileAnyAccess = 0;
    public const uint FileReadData = 0x0001;
    public const uint FileWriteData = 0x0002;

    public const uint IoctlPing = (FileDeviceMacroHid << 16) | (FileAnyAccess << 14) | (0x801 << 2) | MethodBuffered;
    public const uint IoctlSubmitReport = (FileDeviceMacroHid << 16) | (FileWriteData << 14) | (0x802 << 2) | MethodBuffered;
    public const uint IoctlGetStats = (FileDeviceMacroHid << 16) | (FileReadData << 14) | (0x803 << 2) | MethodBuffered;
}
