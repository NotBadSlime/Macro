using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MacroStudio.Services;

public static class ForegroundProcessService
{
    private static readonly ForegroundProcessNameCache Cache = new(QueryForeground, ResolveProcessName);

    /// <summary>
    /// Name of the foreground window's process. The expensive process lookup only runs when the
    /// foreground (window, process id) pair changes; every call still re-reads that pair, so the result is never stale.
    /// </summary>
    public static string? GetForegroundProcessName() => Cache.Get();

    private static (IntPtr Window, uint ProcessId) QueryForeground()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return (IntPtr.Zero, 0);
        }

        _ = GetWindowThreadProcessId(hwnd, out var processId);
        return (hwnd, processId);
    }

    private static string? ResolveProcessName(uint processId)
    {
        using var process = Process.GetProcessById((int)processId);
        return process.ProcessName;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
}

/// <summary>
/// Caches the last resolved name keyed by (window handle, process id). Failures (null or exception) are not cached,
/// so an exited process is looked up again on the next call. Safe for concurrent callers; a race only costs one extra lookup.
/// </summary>
public sealed class ForegroundProcessNameCache(
    Func<(IntPtr Window, uint ProcessId)> queryForeground,
    Func<uint, string?> resolveName)
{
    private sealed record Entry(IntPtr Window, uint ProcessId, string Name);

    private volatile Entry? entry;

    public string? Get()
    {
        try
        {
            var (window, processId) = queryForeground();
            if (window == IntPtr.Zero || processId == 0)
            {
                return null;
            }

            var cached = entry;
            if (cached is not null && cached.Window == window && cached.ProcessId == processId)
            {
                return cached.Name;
            }

            var name = resolveName(processId);
            if (name is null)
            {
                return null;
            }

            entry = new Entry(window, processId, name);
            return name;
        }
        catch
        {
            return null;
        }
    }
}
