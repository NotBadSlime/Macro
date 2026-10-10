using System.Runtime.InteropServices;

namespace MacroHid.Runtime;

internal sealed class TrafficSnapshot
{
    public static readonly TrafficSnapshot Empty = new(
        new HashSet<int>(),
        new Dictionary<Tcp4, int>(),
        new Dictionary<Udp4, int>(),
        new Dictionary<Tcp6, int>(),
        new Dictionary<Udp6, int>());

    private TrafficSnapshot(
        HashSet<int> pids,
        Dictionary<Tcp4, int> tcp4,
        Dictionary<Udp4, int> udp4,
        Dictionary<Tcp6, int> tcp6,
        Dictionary<Udp6, int> udp6)
    {
        Pids = pids;
        Tcp4 = tcp4;
        Udp4 = udp4;
        Tcp6 = tcp6;
        Udp6 = udp6;
    }

    public HashSet<int> Pids { get; }

    public int ProcessCount => Pids.Count;

    public Dictionary<Tcp4, int> Tcp4 { get; }

    public Dictionary<Udp4, int> Udp4 { get; }

    public Dictionary<Tcp6, int> Tcp6 { get; }

    public Dictionary<Udp6, int> Udp6 { get; }

    public bool Targets(int pid) => Pids.Contains(pid);

    public static TrafficSnapshot Capture(string executablePath)
    {
        var pids = WeakNetProcessQuery.PidsForPath(executablePath).ToHashSet();
        var tcp4 = new Dictionary<Tcp4, int>();
        var udp4 = new Dictionary<Udp4, int>();
        var tcp6 = new Dictionary<Tcp6, int>();
        var udp6 = new Dictionary<Udp6, int>();
        ReadTcp4(tcp4);
        ReadUdp4(udp4);
        ReadTcp6(tcp6);
        ReadUdp6(udp6);
        return new TrafficSnapshot(pids, tcp4, udp4, tcp6, udp6);
    }

    private static void ReadTcp4(Dictionary<Tcp4, int> table)
    {
        ReadTable(GetExtendedTcpTable, addressFamily: 2, tableClass: 5, rowSize: 24, (row, count) =>
        {
            for (var i = 0; i < count; i++)
            {
                var offset = row + i * 24;
                var local = (uint)Marshal.ReadInt32(offset, 4);
                var localPort = HostPort((uint)Marshal.ReadInt32(offset, 8));
                var remote = (uint)Marshal.ReadInt32(offset, 12);
                var remotePort = HostPort((uint)Marshal.ReadInt32(offset, 16));
                var pid = Marshal.ReadInt32(offset, 20);
                table[new Tcp4(local, localPort, remote, remotePort)] = pid;
            }
        });
    }

    private static void ReadUdp4(Dictionary<Udp4, int> table)
    {
        ReadTable(GetExtendedUdpTable, addressFamily: 2, tableClass: 1, rowSize: 12, (row, count) =>
        {
            for (var i = 0; i < count; i++)
            {
                var offset = row + i * 12;
                var local = (uint)Marshal.ReadInt32(offset, 0);
                var localPort = HostPort((uint)Marshal.ReadInt32(offset, 4));
                var pid = Marshal.ReadInt32(offset, 8);
                table[new Udp4(local, localPort)] = pid;
            }
        });
    }

    private static void ReadTcp6(Dictionary<Tcp6, int> table)
    {
        ReadTable(GetExtendedTcpTable, addressFamily: 23, tableClass: 5, rowSize: 56, (row, count) =>
        {
            for (var i = 0; i < count; i++)
            {
                var offset = row + i * 56;
                var local = ReadAddr(offset);
                var localPort = HostPort((uint)Marshal.ReadInt32(offset, 20));
                var remote = ReadAddr(offset + 24);
                var remotePort = HostPort((uint)Marshal.ReadInt32(offset, 44));
                var pid = Marshal.ReadInt32(offset, 52);
                table[new Tcp6(local, localPort, remote, remotePort)] = pid;
            }
        });
    }

    private static void ReadUdp6(Dictionary<Udp6, int> table)
    {
        ReadTable(GetExtendedUdpTable, addressFamily: 23, tableClass: 1, rowSize: 28, (row, count) =>
        {
            for (var i = 0; i < count; i++)
            {
                var offset = row + i * 28;
                var local = ReadAddr(offset);
                var localPort = HostPort((uint)Marshal.ReadInt32(offset, 20));
                var pid = Marshal.ReadInt32(offset, 24);
                table[new Udp6(local, localPort)] = pid;
            }
        });
    }

    private static void ReadTable(
        TableReader reader,
        int addressFamily,
        int tableClass,
        int rowSize,
        Action<IntPtr, int> consume)
    {
        var size = 0;
        reader(IntPtr.Zero, ref size, false, addressFamily, tableClass, 0);
        if (size <= 0)
        {
            return;
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            var result = reader(buffer, ref size, false, addressFamily, tableClass, 0);
            if (result == 122)
            {
                Marshal.FreeHGlobal(buffer);
                buffer = IntPtr.Zero;
                buffer = Marshal.AllocHGlobal(size);
                result = reader(buffer, ref size, false, addressFamily, tableClass, 0);
            }

            if (result != 0 || buffer == IntPtr.Zero || size < 4)
            {
                return;
            }

            var count = Marshal.ReadInt32(buffer);
            var needed = 4 + count * rowSize;
            if (count < 0 || needed > size)
            {
                return;
            }

            consume(IntPtr.Add(buffer, 4), count);
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    private static Addr16 ReadAddr(IntPtr offset)
    {
        return new Addr16(Marshal.ReadInt64(offset, 0), Marshal.ReadInt64(offset, 8));
    }

    public static ushort HostPort(uint raw)
    {
        return (ushort)(((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF));
    }

    private delegate uint TableReader(IntPtr table, ref int size, bool order, int addressFamily, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int addressFamily, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool order, int addressFamily, int tableClass, uint reserved);
}

internal readonly record struct Tcp4(uint Local, ushort LocalPort, uint Remote, ushort RemotePort);

internal readonly record struct Udp4(uint Local, ushort LocalPort);

internal readonly record struct Addr16(long A, long B);

internal readonly record struct Tcp6(Addr16 Local, ushort LocalPort, Addr16 Remote, ushort RemotePort);

internal readonly record struct Udp6(Addr16 Local, ushort LocalPort);

internal static class FlowLookup
{
    public static bool TryGetOwner(ReadOnlySpan<byte> packet, TrafficSnapshot snapshot, out int pid, out bool tcpSyn)
    {
        pid = 0;
        tcpSyn = false;
        if (packet.Length < 1)
        {
            return false;
        }

        return (packet[0] >> 4) switch
        {
            4 => TryIpv4(packet, snapshot, ref pid, ref tcpSyn),
            6 => TryIpv6(packet, snapshot, ref pid, ref tcpSyn),
            _ => false
        };
    }

    private static bool TryIpv4(ReadOnlySpan<byte> packet, TrafficSnapshot snapshot, ref int pid, ref bool tcpSyn)
    {
        var headerLength = (packet[0] & 0x0F) * 4;
        if (headerLength < 20 || packet.Length < headerLength + 4)
        {
            return false;
        }

        var protocol = packet[9];
        var source = BitConverter.ToUInt32(packet.Slice(12, 4));
        var destination = BitConverter.ToUInt32(packet.Slice(16, 4));
        var sourcePort = ReadPort(packet, headerLength);
        var destinationPort = ReadPort(packet, headerLength + 2);
        if (protocol == 6)
        {
            tcpSyn = packet.Length > headerLength + 13 && (packet[headerLength + 13] & 0x12) == 0x02;
            return snapshot.Tcp4.TryGetValue(new Tcp4(source, sourcePort, destination, destinationPort), out pid)
                || snapshot.Tcp4.TryGetValue(new Tcp4(destination, destinationPort, source, sourcePort), out pid);
        }

        if (protocol == 17)
        {
            return snapshot.Udp4.TryGetValue(new Udp4(source, sourcePort), out pid)
                || snapshot.Udp4.TryGetValue(new Udp4(0, sourcePort), out pid)
                || snapshot.Udp4.TryGetValue(new Udp4(destination, destinationPort), out pid)
                || snapshot.Udp4.TryGetValue(new Udp4(0, destinationPort), out pid);
        }

        return false;
    }

    private static bool TryIpv6(ReadOnlySpan<byte> packet, TrafficSnapshot snapshot, ref int pid, ref bool tcpSyn)
    {
        if (packet.Length < 40 + 4)
        {
            return false;
        }

        var protocol = packet[6];
        var source = new Addr16(BitConverter.ToInt64(packet.Slice(8, 8)), BitConverter.ToInt64(packet.Slice(16, 8)));
        var destination = new Addr16(BitConverter.ToInt64(packet.Slice(24, 8)), BitConverter.ToInt64(packet.Slice(32, 8)));
        var sourcePort = ReadPort(packet, 40);
        var destinationPort = ReadPort(packet, 42);
        if (protocol == 6)
        {
            tcpSyn = packet.Length > 40 + 13 && (packet[53] & 0x12) == 0x02;
            return snapshot.Tcp6.TryGetValue(new Tcp6(source, sourcePort, destination, destinationPort), out pid)
                || snapshot.Tcp6.TryGetValue(new Tcp6(destination, destinationPort, source, sourcePort), out pid);
        }

        if (protocol == 17)
        {
            var unspecified = new Addr16(0, 0);
            return snapshot.Udp6.TryGetValue(new Udp6(source, sourcePort), out pid)
                || snapshot.Udp6.TryGetValue(new Udp6(unspecified, sourcePort), out pid)
                || snapshot.Udp6.TryGetValue(new Udp6(destination, destinationPort), out pid)
                || snapshot.Udp6.TryGetValue(new Udp6(unspecified, destinationPort), out pid);
        }

        return false;
    }

    private static ushort ReadPort(ReadOnlySpan<byte> packet, int offset)
    {
        return (ushort)((packet[offset] << 8) | packet[offset + 1]);
    }
}
