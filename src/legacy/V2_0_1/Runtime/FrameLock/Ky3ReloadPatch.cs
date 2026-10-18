using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace MacroHid.Runtime;

public enum Ky3PatchState { Unsupported, Original, Patched }
public static class Ky3ReloadPatch
{
    internal static Func<bool>? TestGameRunning { get; set; }
    public const string OriginalHash = "0EC8DD34AB869690ACEAD919BF0AF65530DACC3DEB5788D70F9C2A0FDE44C16B";
    public const string PatchedHash = "3FD1EEA553C2A99C7DD2B0B7780F81ECFFF873065BB50ED7E23D834302B585D7";
    public static Ky3PatchState Inspect(string path) => State(File.ReadAllBytes(path));
    private static Ky3PatchState State(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)) switch
    { OriginalHash => Ky3PatchState.Original, PatchedHash => Ky3PatchState.Patched, _ => Ky3PatchState.Unsupported };
    internal static byte[] Build(byte[] original)
    {
        if (State(original) != Ky3PatchState.Original) throw new InvalidOperationException("KY3 原文件哈希不匹配，拒绝生成补丁。");
        byte[] result = (byte[])original.Clone();
        using var pe = new PEReader(new MemoryStream(original));
        if (pe.PEHeaders.CoffHeader.Machine != Machine.Amd64) throw new InvalidOperationException("仅支持 x64 KY3。");
        foreach (var (rva, expected) in new[] { (0x61f4, "81FAD0070000"), (0xa6a3, "81F9D0070000") })
        {
            var section = pe.PEHeaders.SectionHeaders.Single(s => rva >= s.VirtualAddress && rva + 6 <= s.VirtualAddress + s.SizeOfRawData);
            int offset = section.PointerToRawData + rva - section.VirtualAddress;
            if (!original.AsSpan(offset, 6).SequenceEqual(Convert.FromHexString(expected))) throw new InvalidOperationException("KY3 比较指令不匹配。");
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(offset + 2, 4), 20);
        }
        int checksumOffset = BinaryPrimitives.ReadInt32LittleEndian(result.AsSpan(0x3c, 4)) + 24 + 64;
        result.AsSpan(checksumOffset, 4).Clear();
        ulong sum = 0;
        for (int i = 0; i < result.Length; i += 2)
        {
            sum += (uint)(result[i] | (i + 1 < result.Length ? result[i + 1] << 8 : 0));
            sum = (sum & 0xffff) + (sum >> 16);
        }
        sum = (sum & 0xffff) + (sum >> 16);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(checksumOffset, 4), (uint)((sum & 0xffff) + (ulong)result.Length));
        if (State(result) != Ky3PatchState.Patched) throw new InvalidOperationException("补丁生成结果与已验证副本不一致。");
        return result;
    }
    private static void RequireGameClosed()
    {
        if (TestGameRunning is not null)
        {
            if (TestGameRunning()) throw new InvalidOperationException("请先退出游戏，再安装或恢复 KY3 补丁。");
            return;
        }
        bool running = false;
        foreach (string name in new[] { "YuanShen", "GenshinImpact" })
            foreach (var process in Process.GetProcessesByName(name)) using (process) running = true;
        if (running) throw new InvalidOperationException("请先退出游戏，再安装或恢复 KY3 补丁。完成后重新启动游戏。");
    }
    public static void Install(string dll)
    {
        RequireGameClosed();
        var bytes = File.ReadAllBytes(dll);
        if (State(bytes) == Ky3PatchState.Patched) return;
        byte[] patched = Build(bytes);
        string backup = dll + ".macro-original.bak";
        if (File.Exists(backup) && Inspect(backup) != Ky3PatchState.Original) throw new InvalidOperationException("已有备份不匹配，拒绝覆盖。");
        Replace(dll, patched, File.Exists(backup) ? null : backup);
        GameFpsDiagnostics.Log($"KY3 PATCH installed path={dll} original={OriginalHash} patched={PatchedHash}; restart game required");
    }
    public static void Restore(string dll)
    {
        RequireGameClosed();
        string backup = dll + ".macro-original.bak";
        if (Inspect(dll) != Ky3PatchState.Patched || !File.Exists(backup) || Inspect(backup) != Ky3PatchState.Original)
            throw new InvalidOperationException("当前 DLL 或原文件备份不匹配，拒绝覆盖。");
        Replace(dll, File.ReadAllBytes(backup), null);
        GameFpsDiagnostics.Log($"KY3 PATCH restored path={dll}; restart game required");
    }
    private static void Replace(string path, byte[] bytes, string? backup)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(bytes); file.Flush(true); }
            // Hold the current object against concurrent in-place writers until replacement.
            using var guard = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            byte[] current = new byte[guard.Length]; guard.ReadExactly(current);
            var expected = State(bytes) == Ky3PatchState.Patched ? Ky3PatchState.Original : Ky3PatchState.Patched;
            if (State(current) != expected) throw new InvalidOperationException("KY3 DLL 已被其他程序更新，操作已取消。");
            File.Replace(temporary, path, backup);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
