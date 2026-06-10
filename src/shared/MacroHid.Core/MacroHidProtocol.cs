namespace MacroHid.Core;

public static class MacroHidProtocol
{
    public const int Version = 1;
    public const int MaxReportSize = 64;
    public const byte KeyboardReportId = 0x01;
    public const byte MouseReportId = 0x02;
    public const byte ConsumerReportId = 0x03;
    public const string DeviceInterfaceGuid = "{7F6E65AB-43E6-4ED1-8F0D-3891D94F6270}";
}
