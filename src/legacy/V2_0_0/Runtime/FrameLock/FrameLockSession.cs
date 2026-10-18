namespace MacroHid.Runtime;

public static class FrameLockSession
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, FrameLockProfileBackup> Backups = new(StringComparer.OrdinalIgnoreCase);
    private static string? activeProcess;

    internal static Func<string, FrameLockProfileBackup>? TestReadBackup { get; set; }
    internal static Action<string, int>? TestApplyFps { get; set; }
    internal static Action<string, FrameLockProfileBackup>? TestRestore { get; set; }

    public static bool TryStart(string processName, int fps, out string? error)
    {
        if (!PlaybackEffectScope.TryAcquire("frames", out error)) return false;
        var name = NormalizeFileName(processName);
        if (name.Length == 0)
        {
            error = "还没有选定要限制的程序。";
            return false;
        }

        if (fps < FrameLockProfileBackup.MinFps || fps > FrameLockProfileBackup.MaxFps)
        {
            error = $"RTSS 目标需要在 {FrameLockProfileBackup.MinFps} 到 {FrameLockProfileBackup.MaxFps} 之间。";
            return false;
        }

        lock (Gate)
        {
            if (activeProcess is not null
                && !string.Equals(activeProcess, name, StringComparison.OrdinalIgnoreCase)
                && !RestoreProcess(activeProcess, out error))
            {
                return false;
            }

            if (!Backups.ContainsKey(name))
            {
                try
                {
                    Backups[name] = ReadBackup(name);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    return false;
                }
            }

            try
            {
                ApplyFps(name, fps);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }

            activeProcess = name;
            error = null;
            return true;
        }
    }

    public static bool TryStop(string processName, out string? error)
    {
        if (!PlaybackEffectScope.CanRelease("frames"))
        {
            error = "另一个宏正在控制锁帧。";
            return false;
        }
        var name = NormalizeFileName(processName);
        if (name.Length == 0)
        {
            error = "还没有选定要恢复的程序。";
            return false;
        }

        lock (Gate)
        {
            if (!Backups.ContainsKey(name))
            {
                error = null;
                return true;
            }

            if (!RestoreProcess(name, out error))
            {
                return false;
            }

            error = null;
            return true;
        }
    }

    public static void EndPlayback()
    {
        if (!PlaybackEffectScope.CanRelease("frames")) return;
        lock (Gate)
        {
            var failures = new List<string>();
            foreach (var name in Backups.Keys.ToList())
            {
                if (!RestoreProcess(name, out var error)) failures.Add($"{name}：{error}");
            }
            if (failures.Count > 0) throw new InvalidOperationException("锁帧恢复失败：" + string.Join("；", failures));
        }
    }

    internal static void ResetForTests()
    {
        lock (Gate)
        {
            Backups.Clear();
            activeProcess = null;
            TestReadBackup = null;
            TestApplyFps = null;
            TestRestore = null;
        }
    }

    internal static string NormalizeFileName(string? processName)
    {
        var name = Path.GetFileName(processName?.Trim() ?? string.Empty);
        if (name.Length == 0)
        {
            return string.Empty;
        }

        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
    }

    private static bool RestoreProcess(string name, out string? error)
    {
        if (!Backups.TryGetValue(name, out var backup))
        {
            error = $"没有「{name}」的锁帧备份，不能恢复。";
            return false;
        }

        try
        {
            RestoreBackup(name, backup);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        Backups.Remove(name);
        if (string.Equals(activeProcess, name, StringComparison.OrdinalIgnoreCase))
        {
            activeProcess = null;
        }

        error = null;
        return true;
    }

    private static FrameLockProfileBackup ReadBackup(string name)
    {
        if (TestReadBackup is not null || TestApplyFps is not null || TestRestore is not null)
        {
            return (TestReadBackup ?? throw new InvalidOperationException("锁帧测试没有提供备份读取。")).Invoke(name);
        }

        return FrameLockRtss.ReadBackup(name);
    }

    private static void ApplyFps(string name, int fps)
    {
        if (TestReadBackup is not null || TestApplyFps is not null || TestRestore is not null)
        {
            (TestApplyFps ?? throw new InvalidOperationException("锁帧测试没有提供写入。")).Invoke(name, fps);
            return;
        }

        FrameLockRtss.ApplyFps(name, fps);
    }

    private static void RestoreBackup(string name, FrameLockProfileBackup backup)
    {
        if (TestReadBackup is not null || TestApplyFps is not null || TestRestore is not null)
        {
            (TestRestore ?? throw new InvalidOperationException("锁帧测试没有提供恢复。")).Invoke(name, backup);
            return;
        }

        FrameLockRtss.Restore(name, backup);
    }
}

internal sealed record FrameLockProfileBackup(
    bool Existed,
    uint Fps,
    uint Detection,
    uint Denominator,
    bool HasDenominator,
    byte[]? OriginalBytes = null)
{
    public const int MinFps = 1;
    public const int MaxFps = 1000;
}
