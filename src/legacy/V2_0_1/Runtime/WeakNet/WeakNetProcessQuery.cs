using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace MacroHid.Runtime;

internal static class WeakNetProcessQuery
{
    private const int QueryLimitedInformation = 0x1000;

    public static IReadOnlyList<int> PidsForPath(string executablePath)
    {
        var pids = new List<int>();
        var name = Path.GetFileNameWithoutExtension(executablePath);
        foreach (var process in Process.GetProcessesByName(name))
        {
            try
            {
                var path = QueryPath(process);
                if (path == null || string.Equals(path, executablePath, StringComparison.OrdinalIgnoreCase))
                {
                    pids.Add(process.Id);
                }
            }
            catch
            {
                pids.Add(process.Id);
            }
            finally
            {
                process.Dispose();
            }
        }

        return pids;
    }

    private static readonly object CacheGate = new();
    private static readonly Dictionary<string, (string Path, int Tick)> PathCache = new(StringComparer.OrdinalIgnoreCase);

    public static bool TryResolveRunningPath(string processName, out string path)
    {
        return TryResolveRunningPath(processName, out path, allowCache: true);
    }

    internal static bool TryResolveRunningPath(string processName, out string path, bool allowCache)
    {
        path = string.Empty;
        var fileName = NormalizeFileName(processName);
        if (allowCache && TryReadCache(fileName, out path))
        {
            return true;
        }

        var processNameOnly = Path.GetFileNameWithoutExtension(fileName);
        foreach (var process in Process.GetProcessesByName(processNameOnly))
        {
            try
            {
                var candidate = QueryPath(process);
                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    path = candidate;
                    Remember(fileName, path);
                    return true;
                }
            }
            catch
            {
                // 受保护进程读不到路径时继续找下一个实例。
            }
            finally
            {
                process.Dispose();
            }
        }

        return false;
    }

    private static bool TryReadCache(string fileName, out string path)
    {
        path = string.Empty;
        if (fileName.Length == 0)
        {
            return false;
        }

        lock (CacheGate)
        {
            if (!PathCache.TryGetValue(fileName, out var hit))
            {
                return false;
            }

            var age = unchecked(Environment.TickCount - hit.Tick);
            if ((uint)age > 50 || hit.Path.Length == 0)
            {
                return false;
            }

            path = hit.Path;
            return true;
        }
    }

    private static void Remember(string fileName, string path)
    {
        lock (CacheGate)
        {
            PathCache[fileName] = (path, Environment.TickCount);
        }
    }

    public static string NormalizeFileName(string? processName)
    {
        var file = Path.GetFileName((processName ?? string.Empty).Trim());
        if (string.IsNullOrWhiteSpace(file))
        {
            return string.Empty;
        }

        return file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? file : file + ".exe";
    }

    private static string? QueryPath(Process process)
    {
        var handle = IntPtr.Zero;
        try
        {
            handle = OpenProcess(QueryLimitedInformation, false, process.Id);
            if (handle == IntPtr.Zero)
            {
                return null;
            }

            var buffer = new StringBuilder(32768);
            var size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size)
                ? buffer.ToString()
                : null;
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                CloseHandle(handle);
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder exeName, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
