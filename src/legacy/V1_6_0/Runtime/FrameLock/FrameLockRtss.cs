#pragma warning disable CA1416
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace MacroHid.Runtime;

internal static class FrameLockRtss
{
    private const uint LimiterDisabled = 4;
    private const int UpdateSettings = 0x8000 + 100;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, string> ProfilePaths = new(StringComparer.OrdinalIgnoreCase);
    private static RtssApi api;
    private static string? installDir;
    private static IntPtr cachedWindow;
    private static bool profileWritable;

    public static FrameLockProfileBackup ReadBackup(string exeName)
    {
        lock (Gate)
        {
            EnsureReady();
            var profilePath = FindProfileFile(exeName);
            var existed = profilePath is not null;
            var originalBytes = existed ? TryReadProfileBytes(profilePath!) : null;
            Load(exeName);
            if (!TryGetDword("FramerateLimit", out var fps) || !TryGetDword("AppDetectionLevel", out var detection))
            {
                throw new InvalidOperationException("备份 RTSS 程序配置失败。");
            }

            var hasDenominator = TryGetDword("FramerateLimitDenominator", out var denominator);
            return new FrameLockProfileBackup(existed, fps, detection, hasDenominator ? denominator : 1, hasDenominator, originalBytes);
        }
    }

    public static void Prepare()
    {
        lock (Gate)
        {
            try
            {
                EnsureReady();
                if (!IsRtssRunning())
                {
                    EnsureRunning();
                }

                cachedWindow = FindRtssWindow();
            }
            catch
            {
                // 预热失败时，真正锁帧仍会报告原来的错误。
            }
        }
    }

    public static void ApplyFps(string exeName, int fps)
    {
        lock (Gate)
        {
            EnsureReady();
            if (!IsRtssRunning())
            {
                EnsureRunning();
            }
            if (fps is < FrameLockProfileBackup.MinFps or > FrameLockProfileBackup.MaxFps)
            {
                throw new InvalidOperationException($"RTSS 目标需要在 {FrameLockProfileBackup.MinFps} 到 {FrameLockProfileBackup.MaxFps} 之间。");
            }

            if (api.GetFlags is not null && (api.GetFlags() & LimiterDisabled) != 0)
            {
                throw new InvalidOperationException("RTSS 的全局限帧器已关闭，请在 RTSS 中启用后再锁帧。");
            }

            EnsureProfileWritable();
            Load(exeName);
            if (!SetDword("FramerateLimit", (uint)fps))
            {
                throw new InvalidOperationException("写入 RTSS FramerateLimit 失败。");
            }

            SetDword("FramerateLimitDenominator", 1);
            if (TryGetDword("AppDetectionLevel", out var detect) && detect == 0)
            {
                SetDword("AppDetectionLevel", 1);
            }

            Save(exeName);
            api.UpdateProfiles!();
            Notify();
        }
    }

    public static void Restore(string exeName, FrameLockProfileBackup backup)
    {
        lock (Gate)
        {
            EnsureReady();
            if (backup.OriginalBytes is { Length: > 0 } bytes && TryWriteProfileBytes(exeName, bytes))
            {
                api.UpdateProfiles!();
                Notify();
                return;
            }

            Load(exeName);
            if (!SetDword("FramerateLimit", backup.Fps)
                || !SetDword("AppDetectionLevel", backup.Detection)
                || (backup.HasDenominator && !SetDword("FramerateLimitDenominator", backup.Denominator)))
            {
                throw new InvalidOperationException("恢复 RTSS 原配置失败。");
            }

            Save(exeName);
            api.UpdateProfiles!();
            Notify();
        }
    }

    private static void EnsureReady()
    {
        if (api.Ready)
        {
            return;
        }

        installDir = DetectInstallDir();
        if (string.IsNullOrEmpty(installDir))
        {
            throw new InvalidOperationException("没有找到 RTSS（RivaTuner Statistics Server / 微星小飞机附带组件）。请先安装后再用这条路线。");
        }

        var dllPath = Path.Combine(installDir, "RTSSHooks64.dll");
        if (!File.Exists(dllPath))
        {
            throw new InvalidOperationException($"RTSS 目录里没有 RTSSHooks64.dll：{dllPath}");
        }

        SetDllDirectory(installDir);
        try
        {
            api.Module = LoadLibrary(dllPath);
        }
        finally
        {
            SetDllDirectory(null);
        }

        if (api.Module == IntPtr.Zero)
        {
            throw new InvalidOperationException("加载 RTSSHooks64.dll 失败。");
        }

        api.LoadProfile = GetExport<ProfileString>(api.Module, "LoadProfile");
        api.SaveProfile = GetExport<ProfileString>(api.Module, "SaveProfile");
        api.GetProperty = GetExport<ProfileProperty>(api.Module, "GetProfileProperty");
        api.SetProperty = GetExport<ProfileProperty>(api.Module, "SetProfileProperty");
        api.UpdateProfiles = GetExport<UpdateProfilesDelegate>(api.Module, "UpdateProfiles");
        api.GetFlags = GetOptionalExport<GetFlagsDelegate>(api.Module, "GetFlags");
        if (api.LoadProfile is null || api.SaveProfile is null
            || api.GetProperty is null || api.SetProperty is null || api.UpdateProfiles is null)
        {
            FreeLibrary(api.Module);
            api = default;
            throw new InvalidOperationException("RTSSHooks64.dll 接口不完整，当前 RTSS 版本可能过旧。");
        }

        api.Ready = true;
    }

    private static bool IsRtssRunning()
    {
        if (cachedWindow != IntPtr.Zero && IsWindow(cachedWindow))
        {
            return true;
        }

        cachedWindow = FindRtssWindow();
        return cachedWindow != IntPtr.Zero || Process.GetProcessesByName("RTSS").Length > 0;
    }

    private static void EnsureRunning()
    {
        if (Process.GetProcessesByName("RTSS").Length > 0 || FindRtssWindow() != IntPtr.Zero)
        {
            return;
        }

        var exe = Path.Combine(installDir!, "RTSS.exe");
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = installDir,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Minimized
            });
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("无法启动 RTSS.exe，请手动打开「RivaTuner Statistics Server」。", ex);
        }

        for (var i = 0; i < 50; i++)
        {
            if (Process.GetProcessesByName("RTSS").Length > 0 || FindRtssWindow() != IntPtr.Zero)
            {
                return;
            }

            Thread.Sleep(100);
        }

        throw new InvalidOperationException("已启动 RTSS，但窗口还没就绪。请等托盘出现小飞机图标后再试一次。");
    }

    private static void EnsureProfileWritable()
    {
        if (profileWritable)
        {
            return;
        }

        var dir = Path.Combine(installDir!, "Profiles");
        Directory.CreateDirectory(dir);
        var config = Path.Combine(dir, "Config");
        try
        {
            if (File.Exists(config))
            {
                using var stream = new FileStream(config, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
                profileWritable = true;
                return;
            }

            using var created = new FileStream(
                Path.Combine(dir, "macrohid.writeprobe"),
                FileMode.Create,
                FileAccess.Write,
                FileShare.Delete,
                1,
                FileOptions.DeleteOnClose);
            profileWritable = true;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("无法写入 RTSS 程序配置。请用管理员身份运行。", ex);
        }
    }

    private static string ProfilePath(string exeName) => Path.Combine(installDir!, "Profiles", exeName);

    private static string? FindProfileFile(string exeName)
    {
        if (ProfilePaths.TryGetValue(exeName, out var cached) && File.Exists(cached))
        {
            return cached;
        }

        var dir = Path.Combine(installDir!, "Profiles");
        if (!Directory.Exists(dir))
        {
            return null;
        }

        foreach (var file in Directory.EnumerateFiles(dir))
        {
            if (string.Equals(Path.GetFileName(file), exeName, StringComparison.OrdinalIgnoreCase))
            {
                ProfilePaths[exeName] = file;
                return file;
            }
        }

        return null;
    }

    private static byte[]? TryReadProfileBytes(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bytes = new byte[stream.Length];
            if (bytes.Length == 0)
            {
                return null;
            }

            stream.ReadExactly(bytes);
            return bytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool TryWriteProfileBytes(string exeName, byte[] bytes)
    {
        try
        {
            var path = FindProfileFile(exeName) ?? ProfilePath(exeName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            stream.Write(bytes, 0, bytes.Length);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void Load(string exeName) => api.LoadProfile!(Ansi(exeName));

    private static void Save(string exeName) => api.SaveProfile!(Ansi(exeName));

    private static bool SetDword(string name, uint value)
    {
        var data = BitConverter.GetBytes(value);
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            return api.SetProperty!(Ansi(name), handle.AddrOfPinnedObject(), (uint)data.Length) != 0;
        }
        finally
        {
            handle.Free();
        }
    }

    private static bool TryGetDword(string name, out uint value)
    {
        var data = new byte[4];
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            if (api.GetProperty!(Ansi(name), handle.AddrOfPinnedObject(), (uint)data.Length) == 0)
            {
                value = 0;
                return false;
            }

            value = BitConverter.ToUInt32(data, 0);
            return true;
        }
        finally
        {
            handle.Free();
        }
    }

    private static void Notify()
    {
        if (cachedWindow == IntPtr.Zero || !IsWindow(cachedWindow))
        {
            cachedWindow = FindRtssWindow();
        }

        if (cachedWindow != IntPtr.Zero)
        {
            PostMessage(cachedWindow, UpdateSettings, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private static IntPtr FindRtssWindow()
    {
        var window = FindWindow(null, "RTSS");
        return window != IntPtr.Zero ? window : FindWindow(null, "RivaTuner Statistics Server");
    }

    private static string? DetectInstallDir()
    {
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            foreach (var root in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            {
                foreach (var path in new[] { @"SOFTWARE\Unwinder\RTSS", @"SOFTWARE\WOW6432Node\Unwinder\RTSS" })
                {
                    try
                    {
                        using var baseKey = RegistryKey.OpenBaseKey(root, view);
                        using var key = baseKey.OpenSubKey(path);
                        var dir = key?.GetValue("InstallDir") as string;
                        if (!string.IsNullOrWhiteSpace(dir))
                        {
                            return dir.Trim().TrimEnd('\\', '/');
                        }
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }
            }
        }

        foreach (var path in new[]
        {
            @"C:\Program Files (x86)\RivaTuner Statistics Server",
            @"C:\Program Files\RivaTuner Statistics Server"
        })
        {
            if (File.Exists(Path.Combine(path, "RTSS.exe")))
            {
                return path;
            }
        }

        return null;
    }

    private static byte[] Ansi(string text) => Encoding.Default.GetBytes(text + "\0");

    private static T? GetExport<T>(IntPtr module, string name) where T : Delegate
    {
        var address = GetProcAddress(module, name);
        return address == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    private static T? GetOptionalExport<T>(IntPtr module, string name) where T : Delegate => GetExport<T>(module, name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ProfileString(byte[] profile);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ProfileProperty(byte[] name, IntPtr data, uint size);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void UpdateProfilesDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint GetFlagsDelegate();

    private struct RtssApi
    {
        public IntPtr Module;
        public ProfileString? LoadProfile;
        public ProfileString? SaveProfile;
        public ProfileProperty? GetProperty;
        public ProfileProperty? SetProperty;
        public UpdateProfilesDelegate? UpdateProfiles;
        public GetFlagsDelegate? GetFlags;
        public bool Ready;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string? path);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string path);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr module);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string windowName);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
