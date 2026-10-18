using System.Buffers.Binary;

namespace MacroHid.Runtime;

// Resolve the int32 used by the game's setter/getter. No fixed RVA, patch or injection.
internal static class GameFpsLocator
{
    internal sealed record Location(uint FpsRva, uint SetterBodyRva, byte[] SetterBody, uint ImageSize,
        uint SetterCallRva, byte[] SetterCall, uint GetterCallRva, byte[] GetterCall,
        uint GetterBodyRva, byte[] GetterBody);
    private sealed record Section(uint Rva, uint RawSize, uint RawOffset, uint Flags);
    private static readonly object Gate = new();
    private static readonly Dictionary<string, (long Length, DateTime Modified, Location Value)> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly int[] Setter = [0xE8,-1,-1,-1,-1,0xE8,-1,-1,-1,-1,0x83,0xF8,0x1F,0x0F,0x9C,0x05,-1,-1,-1,-1,0x48,0x8B,0x05,-1,-1,-1,-1];
    private static readonly int[] Getter = [0xE8,-1,-1,-1,-1,0x85,0xC0,0x7E,0x0E,0xE8,-1,-1,-1,-1,0x0F,0x57,0xC0,0xF3,0x0F,0x2A,0xC0,0xEB,0x08];

    internal static Location Locate(string path)
    {
        lock (Gate)
        {
            var info = new FileInfo(path);
            if (Cache.TryGetValue(path, out var cached) && cached.Length == info.Length && cached.Modified == info.LastWriteTimeUtc)
                return cached.Value;
            var value = Resolve(path);
            Cache[path] = (info.Length, info.LastWriteTimeUtc, value);
            return value;
        }
    }

    private static Location Resolve(string path)
    {
        using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new BinaryReader(file);
        if (reader.ReadUInt16() != 0x5A4D) throw Unsupported();
        file.Position = 0x3C;
        uint pe = reader.ReadUInt32();
        if (pe > file.Length - 256) throw Unsupported();
        file.Position = pe;
        if (reader.ReadUInt32() != 0x4550 || reader.ReadUInt16() != 0x8664) throw Unsupported();
        ushort count = reader.ReadUInt16();
        file.Position = pe + 20;
        ushort optionalSize = reader.ReadUInt16();
        file.Position = pe + 24;
        if (reader.ReadUInt16() != 0x20B || count is 0 or > 96 || optionalSize < 60) throw Unsupported();
        file.Position = pe + 24 + 56;
        uint imageSize = reader.ReadUInt32();
        var sections = new List<Section>();
        for (int i = 0; i < count; i++)
        {
            long table = pe + 24 + optionalSize + i * 40L;
            file.Position = table + 12;
            uint rva = reader.ReadUInt32(), rawSize = reader.ReadUInt32(), rawOffset = reader.ReadUInt32();
            file.Position = table + 36;
            uint flags = reader.ReadUInt32();
            if ((long)rawOffset + rawSize > file.Length || (long)rva + rawSize > imageSize) throw Unsupported();
            sections.Add(new(rva, rawSize, rawOffset, flags));
        }
        uint setter = FindUniqueCall(file, sections, Setter, out uint setterCallRva, out byte[] setterCall);
        uint getter = FindUniqueCall(file, sections, Getter, out uint getterCallRva, out byte[] getterCall);
        var (setterBodyRva, setterBody) = FollowThunk(file, sections, setter);
        var (getterBodyRva, getterBody) = FollowThunk(file, sections, getter);
        if (setterBody[0] != 0x89 || setterBody[1] != 0x0D || setterBody[6] != 0xC3 ||
            getterBody[0] != 0x8B || getterBody[1] != 0x05 || getterBody[6] != 0xC3) throw Unsupported();
        uint fromSetter = Relative(setterBodyRva, 6, setterBody.AsSpan(2, 4));
        uint fromGetter = Relative(getterBodyRva, 6, getterBody.AsSpan(2, 4));
        if (fromSetter != fromGetter || fromSetter > imageSize - 4 || (fromSetter & 3) != 0 ||
            !sections.Any(s => fromSetter >= s.Rva && (long)fromSetter + 4 <= (long)s.Rva + s.RawSize && (s.Flags & 0x80000000) != 0))
            throw Unsupported();
        return new(fromSetter, setterBodyRva, setterBody, imageSize,
            setterCallRva, setterCall, getterCallRva, getterCall, getterBodyRva, getterBody);
    }

    private static uint FindUniqueCall(FileStream file, List<Section> sections, int[] pattern, out uint callRva, out byte[] callBytes)
    {
        callRva = 0;
        callBytes = [];
        uint? result = null;
        const int chunk = 1024 * 1024;
        var buffer = new byte[chunk + pattern.Length - 1];
        foreach (var section in sections.Where(s => (s.Flags & 0x20000000) != 0))
        {
            for (long offset = 0; offset < section.RawSize; offset += chunk)
            {
                int length = (int)Math.Min(buffer.Length, section.RawSize - offset);
                file.Position = section.RawOffset + offset;
                file.ReadExactly(buffer.AsSpan(0, length));
                int pos = 0;
                while (pos <= length - pattern.Length && pos < chunk)
                {
                    int found = buffer.AsSpan(pos, length - pattern.Length + 1 - pos).IndexOf((byte)0xE8);
                    if (found < 0) break;
                    pos += found;
                    bool match = true;
                    for (int i = 1; i < pattern.Length; i++)
                        if (pattern[i] >= 0 && buffer[pos + i] != pattern[i]) { match = false; break; }
                    if (match)
                    {
                        if (result.HasValue) throw Unsupported();
                        callRva = checked(section.Rva + (uint)offset + (uint)pos);
                        callBytes = buffer.AsSpan(pos, pattern.Length).ToArray();
                        result = Relative(callRva, 5, buffer.AsSpan(pos + 1, 4));
                    }
                    pos++;
                }
            }
        }
        return result ?? throw Unsupported();
    }

    private static (uint Rva, byte[] Bytes) FollowThunk(FileStream file, List<Section> sections, uint rva)
    {
        for (int hop = 0; hop < 5; hop++)
        {
            var section = sections.SingleOrDefault(s => rva >= s.Rva && (long)rva + 7 <= (long)s.Rva + s.RawSize && (s.Flags & 0x20000000) != 0)
                ?? throw Unsupported();
            file.Position = section.RawOffset + (long)rva - section.Rva;
            var bytes = new byte[7];
            file.ReadExactly(bytes);
            if (bytes[0] != 0xE9) return (rva, bytes);
            rva = Relative(rva, 5, bytes.AsSpan(1, 4));
        }
        throw Unsupported();
    }
    private static uint Relative(uint rva, int size, ReadOnlySpan<byte> displacement)
        => checked((uint)((long)rva + size + BinaryPrimitives.ReadInt32LittleEndian(displacement)));
    private static InvalidOperationException Unsupported()
        => new("无法可靠定位当前游戏版本的帧率变量，未修改内存。请更新内存锁帧模块。");
}
