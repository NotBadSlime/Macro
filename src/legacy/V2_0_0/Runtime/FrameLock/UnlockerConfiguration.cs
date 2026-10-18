using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MacroHid.Core;
using Microsoft.Win32.SafeHandles;

namespace MacroHid.Runtime;

public sealed record UnlockerSettings(string? FufuDirectory = null, string? Ky3Directory = null, bool AllowSlowKy3 = false);

public static class UnlockerConfiguration
{
    internal const string FufuHash = "58240C6B54A074AE6525C314B79A995ED6F614AB3626EF527EF9B88250DBBE6B";
    internal static string Root { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MacroHID", "unlockers");
    public static event Action<FpsBackend, string>? Faulted;
    internal static void Report(FpsBackend backend, string error)
    {
        GameFpsDiagnostics.Log("CONFIG ERROR " + error);
        foreach (var handler in Faulted?.GetInvocationList() ?? Array.Empty<Delegate>())
            try { ((Action<FpsBackend, string>)handler)(backend, error); }
            catch (Exception ex) { GameFpsDiagnostics.Log("NOTIFICATION ERROR " + ex.Message); }
    }
    public static UnlockerSettings Load() => File.Exists(Path.Combine(Root, "settings.json"))
        ? JsonSerializer.Deserialize<UnlockerSettings>(File.ReadAllText(Path.Combine(Root, "settings.json"))) ?? new() : new();
    public static void Save(UnlockerSettings settings)
    {
        Directory.CreateDirectory(Root);
        AtomicText(Path.Combine(Root, "settings.json"), JsonSerializer.Serialize(settings));
    }
    internal static void AtomicText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { byte[] bytes = Encoding.UTF8.GetBytes(text); file.Write(bytes); file.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static string PluginName(FpsBackend backend) => backend == FpsBackend.Fufu ? "FufuLauncher.UnlockerIsland.dll" : "Ky3-luancher-Plugin.dll";
    public static string ConfigName(FpsBackend backend) => backend == FpsBackend.Fufu ? "config.ini" : "dll_config.ini";
    public static string[] FindDirectories(FpsBackend backend, IEnumerable<string> locations)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string location in locations)
        {
            string directory = File.Exists(location) ? Path.GetDirectoryName(Path.GetFullPath(location))! : Path.GetFullPath(location);
            foreach (string candidate in backend == FpsBackend.Fufu
                ? new[] { directory, Path.Combine(directory, "Plugins", "FuFuPlugin") } : new[] { directory })
                if (File.Exists(Path.Combine(candidate, PluginName(backend))) && File.Exists(Path.Combine(candidate, ConfigName(backend)))) result.Add(candidate);
        }
        return result.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public static string[] DetectDirectories(FpsBackend backend)
    {
        var locations = new List<string>();
        foreach (string name in backend == FpsBackend.Fufu ? new[] { "FufuLauncher", "Launcher_2" } : new[] { "ky3launcher" })
            foreach (var process in Process.GetProcessesByName(name))
                using (process)
                using (var handle = OpenProcess(0x1000, false, process.Id))
                {
                    var buffer = new StringBuilder(32768); int size = buffer.Capacity;
                    if (!handle.IsInvalid && QueryFullProcessImageName(handle, 0, buffer, ref size)) locations.Add(buffer.ToString());
                }
        return FindDirectories(backend, locations);
    }
    internal static string Resolve(FpsBackend backend)
    {
        var settings = Load();
        string? saved = backend == FpsBackend.Fufu ? settings.FufuDirectory : settings.Ky3Directory;
        var candidates = saved is null ? Array.Empty<string>() : FindDirectories(backend, new[] { saved });
        if (candidates.Length == 0) candidates = DetectDirectories(backend);
        if (candidates.Length != 1) throw new InvalidOperationException($"{backend}：请在「解帧器设置」选择安装目录（检测到 {candidates.Length} 个候选）。");
        return candidates[0];
    }
    internal static void Validate(FpsBackend backend, string directory)
    {
        string dll = Path.Combine(directory, PluginName(backend));
        if (backend == FpsBackend.Fufu)
        {
            if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dll))) != FufuHash)
                throw new InvalidOperationException("Fufu DLL 版本不受支持，请更新适配；未修改配置。");
        }
        else
        {
            var state = Ky3ReloadPatch.Inspect(dll);
            if (state == Ky3PatchState.Unsupported) throw new InvalidOperationException("KY3 DLL 版本不受支持，请更新适配；未修改配置。");
            if (state == Ky3PatchState.Original && !Load().AllowSlowKy3)
                throw new InvalidOperationException("请在「解帧器设置」安装 KY3 20 毫秒补丁并重新启动游戏，或明确启用 2 秒备用模式。");
        }
        GameFpsDiagnostics.Log($"CONFIG DLL backend={backend} path={dll} sha256={Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dll)))}");
    }
    public static int ReadConfigurationTarget(FpsBackend backend, string directory)
    {
        var ini = new FpsIni(File.ReadAllBytes(Path.Combine(directory, ConfigName(backend))), backend);
        if (!ini.Enabled) throw new InvalidOperationException($"请先在 {backend} 中启用解帧。");
        int target = ini.Target, min = backend == FpsBackend.Fufu ? 1 : 30, max = backend == FpsBackend.Fufu ? 100000 : 1000;
        if (target < min || target > max) throw new InvalidOperationException($"{backend} 配置目标范围无效。");
        return target;
    }
    public static IReadOnlyList<string> RecoverPending() => ConfigFpsChannel.RecoverAll();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(SafeProcessHandle handle, uint flags, StringBuilder path, ref int length);
}
