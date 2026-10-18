using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MacroHid.Core;

namespace MacroHid.Runtime;

internal sealed class ConfigConflictException(string message) : InvalidOperationException(message);
internal sealed record FpsRecovery(string Path, FpsBackend Backend, int Original, int LastWritten, int Previous);

internal sealed class FpsIni
{
    private readonly Encoding encoding;
    private readonly byte[] prefix;
    private readonly string text;
    private readonly FpsBackend backend;
    internal FpsIni(byte[] bytes, FpsBackend backend)
    {
        this.backend = backend;
        int skip = 0;
        encoding = Encoding.Latin1; // Lossless for ASCII keys in UTF-8 and legacy code-page files.
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) { encoding = Encoding.Unicode; skip = 2; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) { encoding = Encoding.BigEndianUnicode; skip = 2; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) skip = 3;
        prefix = bytes[..skip]; text = encoding.GetString(bytes, skip, bytes.Length - skip);
    }
    private (int Offset, int Length, int Value) Field(string section, string key)
    {
        string current = ""; var matches = new List<(int, int, int)>();
        foreach (Match line in Regex.Matches(text, @"[^\r\n]+"))
        {
            var heading = Regex.Match(line.Value, @"^\s*\[([^\]]+)\]\s*(?:[;#].*)?$");
            if (heading.Success) { current = heading.Groups[1].Value.Trim(); continue; }
            if (!current.Equals(section, StringComparison.OrdinalIgnoreCase)) continue;
            var field = Regex.Match(line.Value, @"^[ \t]*" + Regex.Escape(key) + @"[ \t]*=[ \t]*(\d+)[ \t]*(?:[;#].*)?$", RegexOptions.IgnoreCase);
            if (!field.Success)
            {
                if (Regex.IsMatch(line.Value, @"^[ \t]*" + Regex.Escape(key) + @"[ \t]*=", RegexOptions.IgnoreCase))
                    throw new InvalidOperationException($"配置 [{section}] {key} 必须为整数。");
                continue;
            }
            var number = field.Groups[1];
            if (!int.TryParse(number.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int value)) throw new InvalidOperationException("配置数值超出范围。");
            matches.Add((line.Index + number.Index, number.Length, value));
        }
        if (matches.Count != 1) throw new InvalidOperationException($"配置 [{section}] {key} 缺失或重复，未修改文件。");
        return matches[0];
    }
    internal int Target => backend == FpsBackend.Fufu ? Field("TargetFps", "Value").Value : Field("Settings", "targetFps").Value;
    internal bool Enabled => (backend == FpsBackend.Fufu ? Field("FpsUnlock", "Value").Value : Field("Settings", "enableFps").Value) != 0;
    internal byte[] WithTarget(int target)
    {
        var field = backend == FpsBackend.Fufu ? Field("TargetFps", "Value") : Field("Settings", "targetFps");
        string updated = text[..field.Offset] + target.ToString(CultureInfo.InvariantCulture) + text[(field.Offset + field.Length)..];
        return prefix.Concat(encoding.GetBytes(updated)).ToArray();
    }
}

internal sealed class ConfigFpsChannel : IYsrFpsChannel
{
    private readonly string path;
    private readonly FpsBackend backend;
    private readonly string journal;
    private FileStream? lease;
    private int original, lastWritten;
    private bool disposed;
    private readonly Action? validateVersion;
    internal ConfigFpsChannel(string path, FpsBackend backend, Action? validateVersion = null)
    {
        this.validateVersion = validateVersion;
        this.path = System.IO.Path.GetFullPath(path); this.backend = backend;
        journal = JournalPath(this.path);
        if (File.Exists(journal)) throw new InvalidOperationException("此配置有未完成恢复，请先在解帧器设置中处理恢复记录。");
        var ini = ReadIni();
        if (!ini.Enabled) throw new InvalidOperationException($"请先在 {backend} 中启用解帧。");
        original = lastWritten = ini.Target;
        ValidateRange(original, true);
        GameFpsDiagnostics.Log($"CONFIG OPEN backend={backend} path={this.path} original={original}; configuration target only");
    }
    internal static IYsrFpsChannel Open(FpsBackend backend)
    {
        string directory = UnlockerConfiguration.Resolve(backend);
        UnlockerConfiguration.Validate(backend, directory);
        return new ConfigFpsChannel(System.IO.Path.Combine(directory, UnlockerConfiguration.ConfigName(backend)), backend,
            () => UnlockerConfiguration.Validate(backend, directory));
    }
    private FpsIni ReadIni()
    {
        for (int attempt = 0; ; attempt++)
            try { return new FpsIni(File.ReadAllBytes(path), backend); }
            catch (IOException ex) when (attempt < 5 && (ex.HResult & 0xffff) is 32 or 33) { Thread.Sleep(20); }
    }
    private static string Key(string path) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(System.IO.Path.GetFullPath(path).ToUpperInvariant())));
    private static string JournalPath(string path) => System.IO.Path.Combine(UnlockerConfiguration.Root, Key(path) + ".recovery.json");
    private static FileStream Acquire(string path)
    {
        Directory.CreateDirectory(UnlockerConfiguration.Root);
        try { return new FileStream(System.IO.Path.Combine(UnlockerConfiguration.Root, Key(path) + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new InvalidOperationException("另一个 Macro 实例正在控制此解帧器配置。", ex); }
    }
    private void ValidateRange(int fps, bool restore)
    {
        int min = backend == FpsBackend.Ky3 ? 30 : 1;
        int max = restore && backend == FpsBackend.Fufu ? 100000 : 1000;
        if (fps < min || fps > max) throw new InvalidOperationException($"{backend} 目标帧率必须为 {min}–{max}，或 0（999 FPS）。");
    }
    public int Read() { ObjectDisposedException.ThrowIf(disposed, this); return ReadIni().Target; }
    public void Write(int fps)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        int target = fps == 0 ? 999 : fps; ValidateRange(target, false);
        validateVersion?.Invoke();
        lease ??= Acquire(path);
        Check();
        if (target == lastWritten) return;
        if (!ReadIni().Enabled) throw new InvalidOperationException("解帧已关闭，未修改配置。");
        UnlockerConfiguration.AtomicText(journal, JsonSerializer.Serialize(new FpsRecovery(path, backend, original, target, lastWritten)));
        ReplaceTarget(path, backend, lastWritten, target);
        lastWritten = target;
        GameFpsDiagnostics.Log($"CONFIG WRITE backend={backend} path={path} target={target}; waiting for unlocker application");
    }
    internal void Check()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        int current = ReadIni().Target;
        if (current != lastWritten)
        {
            ArchiveConflict(journal);
            throw new ConfigConflictException($"{backend} 配置目标被其他程序修改（当前 {current}，Macro 目标 {lastWritten}），已停止控制并保留外部值。");
        }
    }
    public void Restore(int fps)
    {
        ObjectDisposedException.ThrowIf(disposed, this); ValidateRange(fps, true);
        if (lease is null) return;
        Check();
        ReplaceTarget(path, backend, lastWritten, fps);
        lastWritten = fps;
        if (File.Exists(journal)) File.Delete(journal);
        GameFpsDiagnostics.Log($"CONFIG RESTORE backend={backend} path={path} target={fps}; waiting for unlocker application");
    }
    internal static void ReplaceTarget(string path, FpsBackend backend, int expected, int target)
    {
        for (int attempt = 0; ; attempt++)
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using var guard = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                byte[] before = new byte[guard.Length]; guard.ReadExactly(before);
                var ini = new FpsIni(before, backend);
                if (ini.Target != expected) throw new ConfigConflictException("配置目标在写入前发生变化，已取消操作。");
                if (expected == target) return;
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(ini.WithTarget(target)); file.Flush(true); }
                File.Replace(temporary, path, null);
                return;
            }
            catch (IOException ex) when (attempt < 5 && (ex.HResult & 0xffff) is 32 or 33) { Thread.Sleep(20); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
    private static void ArchiveConflict(string journal)
    {
        if (File.Exists(journal)) File.Move(journal, journal + ".conflict-" + Guid.NewGuid().ToString("N"));
    }
    internal static IReadOnlyList<string> RecoverAll()
    {
        var results = new List<string>();
        if (!Directory.Exists(UnlockerConfiguration.Root)) return results;
        foreach (string file in Directory.GetFiles(UnlockerConfiguration.Root, "*.recovery.json"))
        {
            try
            {
                var record = JsonSerializer.Deserialize<FpsRecovery>(File.ReadAllText(file)) ?? throw new IOException("恢复记录为空。");
                if (record.Backend is not (FpsBackend.Fufu or FpsBackend.Ky3) || !System.IO.Path.IsPathFullyQualified(record.Path)
                    || !System.IO.Path.GetFileName(record.Path).Equals(UnlockerConfiguration.ConfigName(record.Backend), StringComparison.OrdinalIgnoreCase)
                    || !System.IO.Path.GetFullPath(file).Equals(JournalPath(record.Path), StringComparison.OrdinalIgnoreCase)) throw new IOException("恢复记录路径不匹配。");
                int min = record.Backend == FpsBackend.Fufu ? 1 : 30, max = record.Backend == FpsBackend.Fufu ? 100000 : 1000;
                if (record.Original < min || record.Original > max) throw new IOException("恢复目标无效。");
                using var held = Acquire(record.Path);
                int current = new FpsIni(File.ReadAllBytes(record.Path), record.Backend).Target;
                if (current == record.Original) { File.Delete(file); continue; }
                if (current != record.LastWritten && current != record.Previous)
                {
                    ArchiveConflict(file); results.Add($"{record.Backend}：配置已被其他程序修改，保留当前值 {current}；恢复记录已归档。"); continue;
                }
                ReplaceTarget(record.Path, record.Backend, current, record.Original);
                File.Delete(file);
                GameFpsDiagnostics.Log($"CONFIG RECOVER backend={record.Backend} path={record.Path} target={record.Original}");
                results.Add($"{record.Backend}：已恢复配置目标 {record.Original}，等待解帧器应用。");
            }
            catch (Exception ex) { results.Add("配置恢复未完成：" + ex.Message); GameFpsDiagnostics.Log("RECOVERY ERROR " + ex); }
        }
        return results;
    }
    public void Dispose() { lease?.Dispose(); lease = null; disposed = true; }
}
