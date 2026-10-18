using System.Diagnostics;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using MacroHid.Core;

namespace MacroHid.Runtime;

internal static class UnlockerTargetChannel
{
    internal static IYsrFpsChannel Open(FpsBackend backend) => backend is FpsBackend.Ky3 or FpsBackend.Fufu
        ? ConfigFpsChannel.Open(backend)
        : throw new ArgumentOutOfRangeException(nameof(backend));
}

// Retained for legacy diagnostic tests; production KY3/Fufu cards use ConfigFpsChannel.
internal sealed class Ky3TargetChannel(IGameFpsMemory memory, nint address) : IYsrFpsChannel
{
    public int Read() => BitConverter.ToInt32(memory.Read(address, 4));
    public void Write(int fps)
    {
        int target = fps == 0 ? 999 : fps;
        if (target is < 30 or > 1000) throw new InvalidOperationException("KY3 目标帧率必须为 30 到 1000，或 0（999 FPS）。");
        if (!memory.IsAlive || !memory.IsWritable(address, 4)) throw new InvalidOperationException("KY3 目标帧率内存已不可访问。");
        if (Read() == target) return;
        memory.Write(address, BitConverter.GetBytes(target));
        if (Read() != target) throw new InvalidOperationException("KY3 目标值写入后未保持，请检查是否有其他控制器。");
        GameFpsDiagnostics.Log($"UNLOCKER KY3 target={target}");
    }
    public void Restore(int fps) { if (memory.IsAlive) Write(fps); }
    public void Dispose() => memory.Dispose();
}

internal sealed class FufuConfigTargetChannel : IYsrFpsChannel
{
    private readonly string path;
    internal FufuConfigTargetChannel(string path)
    {
        this.path = path;
        if (!File.Exists(path) || GetPrivateProfileInt("FpsUnlock", "Value", 0, path) == 0)
            throw new InvalidOperationException("Fufu 配置不存在或未启用解帧；当前支持 UnlockerIsland 的 config.ini 自动重载接口。");
        if (Read() is < 1 or > 1000) throw new InvalidOperationException("Fufu 原目标帧率超出支持范围，未修改配置。");
        GameFpsDiagnostics.Log($"UNLOCKER Fufu config={path} original={Read()}");
    }
    public int Read() => checked((int)GetPrivateProfileInt("TargetFps", "Value", 0, path));
    public void Write(int fps)
    {
        int target = fps == 0 ? 999 : fps;
        if (target is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(fps));
        if (Read() == target) return;
        if (!WritePrivateProfileString("TargetFps", "Value", target.ToString(System.Globalization.CultureInfo.InvariantCulture), path))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法修改 Fufu 目标帧率配置。");
        if (Read() != target) throw new InvalidOperationException("Fufu 配置写入校验失败。");
        GameFpsDiagnostics.Log($"UNLOCKER Fufu target={target}; awaiting module config reload");
    }
    public void Dispose() { }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetPrivateProfileIntW")]
    private static extern uint GetPrivateProfileInt(string section, string key, int fallback, string file);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "WritePrivateProfileStringW")]
    private static extern bool WritePrivateProfileString(string section, string key, string value, string file);
}

internal sealed class FufuMemoryTargetChannel(IGameFpsMemory memory, nint address, Action<int, int>? sample = null) : IYsrFpsChannel
{
    public int Read() => BitConverter.ToInt32(memory.Read(address, 4));
    public void Write(int fps) => Set(fps == 0 ? 999 : fps, 1000);
    private void Set(int target, int maximum)
    {
        if (target < 1 || target > maximum) throw new ArgumentOutOfRangeException(nameof(target));
        if (!memory.IsAlive || !memory.IsWritable(address, 4)) throw new InvalidOperationException("Fufu 目标帧率内存已不可访问。");
        int before = Read();
        sample?.Invoke(target, before);
        if (before == target) return;
        memory.Write(address, BitConverter.GetBytes(target));
        int after = Read();
        GameFpsDiagnostics.Log($"UNLOCKER Fufu MEMORY before={before} target={target} after={after}");
        if (after != target) throw new InvalidOperationException("Fufu 目标值写入后未保持。");
    }
    public void Restore(int fps) { if (memory.IsAlive) Set(fps, 100000); }
    public void Dispose() => memory.Dispose();
}
