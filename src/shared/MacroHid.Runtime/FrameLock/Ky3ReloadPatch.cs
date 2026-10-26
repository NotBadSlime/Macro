using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace MacroHid.Runtime;

public enum Ky3PatchState { Unsupported, Original, Patched }
public static class Ky3ReloadPatch
{
    internal static Func<bool>? TestGameRunning { get; set; }
    private sealed record Variant(string OriginalHash, string PatchedHash, (int Rva, string Expected)[] Sites);
    private static readonly Variant[] Variants =
    {
        new("0EC8DD34AB869690ACEAD919BF0AF65530DACC3DEB5788D70F9C2A0FDE44C16B", "3FD1EEA553C2A99C7DD2B0B7780F81ECFFF873065BB50ED7E23D834302B585D7",
            new[] { (0x61f4, "81FAD0070000"), (0xa6a3, "81F9D0070000") }),
        // KY3 launcher update 2026-10-24 (plugin 2650112 bytes).
        new("53F34C3EC6E708A5D2515E031812166CE2F7CCF0F4ED75FDEECDA095D2156B07", "974D2BFA8692D2C084C38CD5597F99E76F7B53B8BDB2BDF36AF37F9CEB2DE428",
            new[] { (0x677d, "81F9D0070000"), (0xa783, "81F9D0070000") }),
    };
    public static string OriginalHash => Variants[0].OriginalHash;
    public static string PatchedHash => Variants[0].PatchedHash;
    public static Ky3PatchState Inspect(string path) => State(File.ReadAllBytes(path));
    private static (Ky3PatchState State, Variant? Variant) Classify(byte[] bytes)
    {
        string hash = Convert.ToHexString(SHA256.HashData(bytes));
        foreach (var variant in Variants)
        {
            if (hash == variant.OriginalHash) return (Ky3PatchState.Original, variant);
            if (hash == variant.PatchedHash) return (Ky3PatchState.Patched, variant);
        }
        return (Ky3PatchState.Unsupported, null);
    }
    private static Ky3PatchState State(byte[] bytes) => Classify(bytes).State;
    internal static byte[] Build(byte[] original)
    {
        var (state, variant) = Classify(original);
        if (state != Ky3PatchState.Original || variant is null) throw new InvalidOperationException("KY3 原文件哈希不匹配，拒绝生成补丁。");
        byte[] result = (byte[])original.Clone();
        using var pe = new PEReader(new MemoryStream(original));
        if (pe.PEHeaders.CoffHeader.Machine != Machine.Amd64) throw new InvalidOperationException("仅支持 x64 KY3。");
        foreach (var (rva, expected) in variant.Sites)
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
        if (Convert.ToHexString(SHA256.HashData(result)) != variant.PatchedHash) throw new InvalidOperationException("补丁生成结果与已验证副本不一致。");
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
        var variant = Classify(bytes).Variant!;
        string backup = dll + ".macro-original.bak";
        bool keepBackup = false;
        if (File.Exists(backup))
        {
            // A backup of an older KY3 release is stale after a launcher update; refresh it with the current original.
            var backupState = Classify(File.ReadAllBytes(backup));
            if (backupState.State != Ky3PatchState.Original) throw new InvalidOperationException("已有备份不匹配，拒绝覆盖。");
            keepBackup = backupState.Variant == variant;
        }
        Replace(dll, patched, keepBackup ? null : backup);
        GameFpsDiagnostics.Log($"KY3 PATCH installed path={dll} original={variant.OriginalHash} patched={variant.PatchedHash}; restart game required");
    }
    public static void Restore(string dll)
    {
        RequireGameClosed();
        string backup = dll + ".macro-original.bak";
        var current = Classify(File.ReadAllBytes(dll));
        var saved = File.Exists(backup) ? Classify(File.ReadAllBytes(backup)) : (Ky3PatchState.Unsupported, null);
        if (current.State != Ky3PatchState.Patched || saved.State != Ky3PatchState.Original || saved.Variant != current.Variant)
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
