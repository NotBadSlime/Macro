using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using MacroHid.Runtime;
using Microsoft.Win32.SafeHandles;

internal static class FpsProcessAccessTests
{
    internal static void PrivilegeLifetime()
    {
        bool? original = ReadEnabled();
        try
        {
            using var outer = FpsProcessAccessScope.TryEnable();
            VerifyScope(outer, original);
            using (var inner = FpsProcessAccessScope.TryEnable()) VerifyScope(inner, original);
            Require(ReadEnabled() == (original.HasValue ? true : null), "Nested scope changed outer privilege");
            throw new ApplicationException("simulate module enumeration failure");
        }
        catch (ApplicationException) { }
        Require(ReadEnabled() == original, "Privilege was not restored after failure");

        Parallel.For(0, 12, _ =>
        {
            using var scope = FpsProcessAccessScope.TryEnable();
            VerifyScope(scope, original);
        });
        Require(ReadEnabled() == original, "Concurrent scopes did not restore privilege");
        var twice = FpsProcessAccessScope.TryEnable();
        twice.Dispose();
        twice.Dispose();
        Require(ReadEnabled() == original, "Repeated dispose changed privilege");
    }

    private static void VerifyScope(FpsProcessAccessScope scope, bool? original)
    {
        Require(scope.DebugPrivilegeEnabled == original.HasValue, "Privilege enable result disagrees with token");
        Require(ReadEnabled() == (original.HasValue ? true : null), "Token has incorrect privilege state");
        Require(scope.Status.Contains(original.HasValue ? "SeDebugPrivilege=enabled" : "win32=1300"),
            "Missing privilege diagnostic: " + scope.Status);
    }

    private static bool? ReadEnabled()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!LookupPrivilegeValue(null, "SeDebugPrivilege", out long debugLuid))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        GetTokenInformation(identity.AccessToken, 3, 0, 0, out int size);
        nint buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!GetTokenInformation(identity.AccessToken, 3, buffer, size, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            int count = Marshal.ReadInt32(buffer);
            for (int index = 0; index < count; index++)
            {
                int offset = 4 + index * 12;
                if (Marshal.ReadInt64(buffer, offset) == debugLuid)
                    return (Marshal.ReadInt32(buffer, offset + 8) & 2) != 0;
            }
            return null;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass,
        nint information, int length, out int returnedLength);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "LookupPrivilegeValueW")]
    private static extern bool LookupPrivilegeValue(string? system, string name, out long luid);
}
