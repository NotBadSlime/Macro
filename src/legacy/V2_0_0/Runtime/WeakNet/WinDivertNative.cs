using System.Runtime.InteropServices;

namespace MacroHid.Runtime;

internal static class WinDivertNative
{
    public const int LayerNetwork = 0;
    public const int AddressSize = 80;
    public const uint ShutdownBoth = 0x3;

    public static IntPtr OpenOutbound()
    {
        return WinDivertOpen("!loopback", LayerNetwork, 0, 0);
    }

    public static IntPtr OpenTraffic(short priority)
    {
        return WinDivertOpen("outbound and !loopback", LayerNetwork, priority, 0);
    }

    public static bool IsOutbound(ReadOnlySpan<byte> address)
    {
        if (address.Length < 12)
        {
            return true;
        }

        var flags = BitConverter.ToUInt32(address.Slice(8, 4));
        return (flags & (1u << 17)) != 0;
    }

    public static bool Recv(IntPtr handle, IntPtr packet, uint packetLen, out uint recvLen, IntPtr address)
    {
        return WinDivertRecv(handle, packet, packetLen, out recvLen, address);
    }

    public static bool Send(IntPtr handle, IntPtr packet, uint packetLen, IntPtr address)
    {
        return WinDivertSend(handle, packet, packetLen, out _, address);
    }

    public static void Shutdown(IntPtr handle)
    {
        if (IsOpen(handle))
        {
            WinDivertShutdown(handle, ShutdownBoth);
        }
    }

    public static void Close(IntPtr handle)
    {
        if (IsOpen(handle))
        {
            WinDivertClose(handle);
        }
    }

    public static bool IsOpen(IntPtr handle)
    {
        return handle != IntPtr.Zero && handle != new IntPtr(-1);
    }

    [DllImport("WinDivert.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr WinDivertOpen(string filter, int layer, short priority, ulong flags);

    [DllImport("WinDivert.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    private static extern bool WinDivertRecv(IntPtr handle, IntPtr packet, uint packetLen, out uint recvLen, IntPtr address);

    [DllImport("WinDivert.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    private static extern bool WinDivertSend(IntPtr handle, IntPtr packet, uint packetLen, out uint sendLen, IntPtr address);

    [DllImport("WinDivert.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    private static extern bool WinDivertShutdown(IntPtr handle, uint how);

    [DllImport("WinDivert.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    private static extern bool WinDivertClose(IntPtr handle);
}
