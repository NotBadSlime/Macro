using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MacroHid.Runtime;

// Synchronous scope: serialize changes to the process token and restore the exact
// previous privilege state after module discovery and OpenProcess have completed.
internal sealed class FpsProcessAccessScope : IDisposable
{
    private static readonly object Gate = new();
    private SafeAccessTokenHandle? token;
    private TokenPrivileges previous;
    private bool disposed;
    internal bool DebugPrivilegeEnabled { get; private set; }
    internal string Status { get; private set; } = "";

    private FpsProcessAccessScope() { }

    internal static FpsProcessAccessScope TryEnable()
    {
        Monitor.Enter(Gate);
        var scope = new FpsProcessAccessScope();
        try
        {
            scope.Enable();
            return scope;
        }
        catch
        {
            scope.Dispose();
            throw;
        }
    }

    private void Enable()
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x20 | 0x8, out var opened))
        {
            int error = Marshal.GetLastWin32Error();
            opened.Dispose();
            Status = $"OpenProcessToken failed: win32={error}";
            return;
        }
        token = opened;
        string elevated = GetTokenInformation(token, 20, out int elevation, sizeof(int), out _)
            ? (elevation != 0).ToString() : "unknown";
        Status = $"elevated={elevated}; process64={Environment.Is64BitProcess}";
        if (!LookupPrivilegeValue(null, "SeDebugPrivilege", out var luid))
        {
            Status += $"; LookupPrivilegeValue failed: win32={Marshal.GetLastWin32Error()}";
            return;
        }
        var requested = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 2 };
        bool adjusted = AdjustTokenPrivileges(token, false, ref requested,
            (uint)Marshal.SizeOf<TokenPrivileges>(), out previous, out _);
        int result = Marshal.GetLastWin32Error();
        // A true return with ERROR_NOT_ALL_ASSIGNED (1300) is NOT success.
        DebugPrivilegeEnabled = adjusted && result == 0;
        Status += DebugPrivilegeEnabled
            ? "; SeDebugPrivilege=enabled"
            : $"; SeDebugPrivilege=unavailable (win32={result})";
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            if (token is not null && previous.Count != 0)
            {
                bool restored = RestoreTokenPrivileges(token, false, ref previous, 0, 0, 0);
                int result = Marshal.GetLastWin32Error();
                if (!restored || result != 0)
                    GameFpsDiagnostics.Log($"ACCESS RESTORE ERROR SeDebugPrivilege win32={result}");
            }
        }
        finally
        {
            token?.Dispose();
            Monitor.Exit(Gate);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges { public uint Count; public Luid Luid; public uint Attributes; }

    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(nint process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass,
        out int information, int length, out int returnedLength);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "LookupPrivilegeValueW")]
    private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token, bool disableAll,
        ref TokenPrivileges requested, uint length, out TokenPrivileges previous, out uint returnedLength);
    [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "AdjustTokenPrivileges")]
    private static extern bool RestoreTokenPrivileges(SafeAccessTokenHandle token, bool disableAll,
        ref TokenPrivileges requested, uint length, nint previous, nint returnedLength);
}
