using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MacroHid.Runtime;

internal static class SystemTimeShift
{
    private static readonly object Gate = new();
    private static bool shifted;
    private static TimeSpan applied = TimeSpan.Zero;
    private static bool exitHooked;
    private static Func<DateTime> now = static () => DateTime.Now;
    private static Action<DateTime>? testApply;
    private static Func<TimeSpan> elapsed = static () => TimeSpan.FromSeconds((double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency);
    private static DateTime originalTime;
    private static TimeSpan startedAt;

    public static bool IsShifted
    {
        get
        {
            lock (Gate)
            {
                return shifted;
            }
        }
    }

    public static void ShiftForwardOnce(TimeSpan amount)
    {
        lock (Gate)
        {
            if (shifted || amount <= TimeSpan.Zero)
            {
                return;
            }

            EnsureExitHook();
            originalTime = now();
            startedAt = elapsed();
            Apply(originalTime.Add(amount));
            applied = amount;
            shifted = true;
        }
    }

    public static void Restore()
    {
        lock (Gate)
        {
            if (!shifted)
            {
                return;
            }

            Apply(originalTime.Add(elapsed() - startedAt));
            shifted = false;
            applied = TimeSpan.Zero;
        }
    }

    internal static void UseTestClock(Func<DateTime> clock, Action<DateTime> apply, Func<TimeSpan>? elapsedClock = null)
    {
        lock (Gate)
        {
            now = clock;
            testApply = apply;
            elapsed = elapsedClock ?? (() => TimeSpan.Zero);
            shifted = false;
            applied = TimeSpan.Zero;
        }
    }

    internal static void ResetForTests()
    {
        lock (Gate)
        {
            shifted = false;
            applied = TimeSpan.Zero;
            now = static () => DateTime.Now;
            testApply = null;
            elapsed = static () => TimeSpan.FromSeconds((double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency);
        }
    }

    private static void EnsureExitHook()
    {
        if (exitHooked)
        {
            return;
        }

        exitHooked = true;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => RestoreAtExit();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => RestoreAtExit();
    }

    private static void RestoreAtExit()
    {
        try { Restore(); }
        catch (Exception ex) { System.Diagnostics.Trace.TraceError("System clock restore failed: {0}", ex); }
    }

    private static void Apply(DateTime local)
    {
        if (testApply is not null)
        {
            testApply(local);
            return;
        }

        EnableSystemTimePrivilege();
        var native = new SystemTimeNative
        {
            Year = (ushort)local.Year,
            Month = (ushort)local.Month,
            DayOfWeek = (ushort)local.DayOfWeek,
            Day = (ushort)local.Day,
            Hour = (ushort)local.Hour,
            Minute = (ushort)local.Minute,
            Second = (ushort)local.Second,
            Milliseconds = (ushort)local.Millisecond
        };
        if (!SetLocalTime(ref native))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法调整系统时间。");
        }
    }

    private static void EnableSystemTimePrivilege()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法调整系统时间。");
        }

        try
        {
            if (!LookupPrivilegeValue(null, "SeSystemtimePrivilege", out var luid))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法调整系统时间。");
            }

            var privileges = new TokenPrivileges
            {
                PrivilegeCount = 1,
                Privileges = new LuidAndAttributes
                {
                    Luid = luid,
                    Attributes = PrivilegeEnabled
                }
            };
            SetLastError(0);
            if (!AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法调整系统时间。");
            }

            var error = Marshal.GetLastWin32Error();
            if (error != 0)
            {
                throw new Win32Exception(error, "无法调整系统时间。请用管理员身份运行。");
            }
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint PrivilegeEnabled = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemTimeNative
    {
        public ushort Year;
        public ushort Month;
        public ushort DayOfWeek;
        public ushort Day;
        public ushort Hour;
        public ushort Minute;
        public ushort Second;
        public ushort Milliseconds;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LuidAndAttributes
    {
        public Luid Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public LuidAndAttributes Privileges;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern void SetLastError(uint error);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetLocalTime(ref SystemTimeNative time);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr tokenHandle,
        bool disableAllPrivileges,
        ref TokenPrivileges newState,
        uint bufferLength,
        IntPtr previousState,
        IntPtr returnLength);
}
