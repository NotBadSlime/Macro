using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using MacroHid.Core;

namespace MacroHid.Runtime;

public sealed record WindowTargetInfo(
    IntPtr Handle,
    int ProcessId,
    string ProcessName,
    string WindowTitle,
    bool IsMinimized);

public sealed record WindowActivationResult(
    bool Success,
    WindowTargetInfo? Target,
    string? Error);

public static class WindowActivationService
{
    private const int SwShow = 5;
    private const int SwMinimize = 6;
    private const int SwRestore = 9;
    private const byte VkMenu = 0x12;
    private const byte VkTab = 0x09;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpShowWindow = 0x0040;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNotTopmost = new(-2);
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    public static WindowActivationResult Activate(
        WindowActivateStep step,
        CancellationToken cancellationToken = default)
    {
        var processName = NormalizeProcessName(step.ProcessName);
        if (string.IsNullOrWhiteSpace(processName))
        {
            return new WindowActivationResult(false, null, "process name is empty");
        }

        if (step.UseTitleRegex && !IsValidTitleRegex(step.WindowTitle, out var regexError))
        {
            return new WindowActivationResult(false, null, $"invalid window title regex: {regexError}");
        }

        var timeout = step.Timeout > TimeSpan.Zero ? step.Timeout : TimeSpan.FromSeconds(3);
        var stopwatch = Stopwatch.StartNew();
        WindowTargetInfo? lastTarget = null;
        string? lastError = null;

        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targets = EnumerateTargets(processName, step.WindowTitle, step.UseTitleRegex);
            if (targets.Count >= Math.Max(1, step.MatchIndex))
            {
                lastTarget = targets[Math.Max(1, step.MatchIndex) - 1];
                if (IsForeground(lastTarget))
                {
                    return new WindowActivationResult(true, lastTarget, null);
                }

                if (!TryBringToForeground(lastTarget, step.Restore))
                {
                    lastError = "Windows rejected the foreground request";
                }
                else if (IsForeground(lastTarget))
                {
                    return new WindowActivationResult(true, lastTarget, null);
                }
            }
            else
            {
                lastError = targets.Count == 0
                    ? $"no visible window found for {step.ProcessName}"
                    : $"window match #{step.MatchIndex} was not found";
            }

            var remaining = timeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero) break;
            var wait = remaining < TimeSpan.FromMilliseconds(50)
                ? remaining
                : TimeSpan.FromMilliseconds(50);
            if (cancellationToken.WaitHandle.WaitOne(wait))
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        while (stopwatch.Elapsed < timeout);

        return new WindowActivationResult(
            false,
            lastTarget,
            lastError ?? $"window did not become foreground within {timeout.TotalMilliseconds:0} ms");
    }

    public static WindowTargetInfo? GetForegroundTarget()
    {
        var handle = GetForegroundWindow();
        return handle == IntPtr.Zero ? null : CreateTarget(handle);
    }

    public static IReadOnlyList<WindowTargetInfo> EnumerateTargets(
        string processName,
        string windowTitle = "",
        bool useTitleRegex = false)
    {
        var normalizedProcess = NormalizeProcessName(processName);
        if (normalizedProcess.Length == 0) return [];
        if (useTitleRegex && !IsValidTitleRegex(windowTitle, out _)) return [];

        return EnumerateVisibleWindows()
            .Where(target =>
                string.Equals(
                    NormalizeProcessName(target.ProcessName),
                    normalizedProcess,
                    StringComparison.OrdinalIgnoreCase)
                && TitleMatches(target.WindowTitle, windowTitle, useTitleRegex))
            .ToList();
    }

    public static IReadOnlyList<WindowTargetInfo> EnumerateVisibleWindows()
    {
        var targets = new List<WindowTargetInfo>();
        _ = EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle)) return true;
            var target = CreateTarget(handle);
            if (target is null || string.IsNullOrWhiteSpace(target.WindowTitle))
            {
                return true;
            }

            targets.Add(target);
            return true;
        }, IntPtr.Zero);
        return targets;
    }

    public static string NormalizeProcessName(string? processName)
    {
        var value = (processName ?? string.Empty).Trim();
        return value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? value[..^4]
            : value;
    }

    public static bool TitleMatches(string title, string expected, bool useRegex)
    {
        if (string.IsNullOrWhiteSpace(expected)) return true;
        if (!useRegex)
        {
            return title.Contains(expected.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        try
        {
            return Regex.IsMatch(
                title,
                expected,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                RegexTimeout);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    public static bool IsValidTitleRegex(string pattern, out string? error)
    {
        try
        {
            _ = new Regex(
                pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                RegexTimeout);
            error = null;
            return true;
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static WindowTargetInfo? CreateTarget(IntPtr handle)
    {
        _ = GetWindowThreadProcessId(handle, out var processId);
        if (processId == 0) return null;

        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            return new WindowTargetInfo(
                handle,
                checked((int)processId),
                $"{process.ProcessName}.exe",
                ReadWindowTitle(handle),
                IsIconic(handle));
        }
        catch
        {
            return null;
        }
    }

    private static bool TryBringToForeground(WindowTargetInfo target, bool restore)
    {
        try
        {
            if (restore && (target.IsMinimized || IsIconic(target.Handle)))
            {
                _ = ShowWindowAsync(target.Handle, SwRestore);
            }
            else
            {
                _ = ShowWindowAsync(target.Handle, SwShow);
            }

            var foreground = GetForegroundWindow();
            var currentThread = GetCurrentThreadId();
            var targetThread = GetWindowThreadProcessId(target.Handle, out _);
            var foregroundThread = foreground == IntPtr.Zero
                ? 0
                : GetWindowThreadProcessId(foreground, out _);
            var attachedTarget = targetThread != 0
                && targetThread != currentThread
                && AttachThreadInput(currentThread, targetThread, true);
            var attachedForeground = foregroundThread != 0
                && foregroundThread != currentThread
                && foregroundThread != targetThread
                && AttachThreadInput(currentThread, foregroundThread, true);
            var attachedForegroundToTarget = foregroundThread != 0
                && targetThread != 0
                && foregroundThread != targetThread
                && AttachThreadInput(foregroundThread, targetThread, true);

            try
            {
                _ = BringWindowToTop(target.Handle);
                _ = SetForegroundWindow(target.Handle);
                _ = SetActiveWindow(target.Handle);
                _ = SetFocus(target.Handle);
            }
            finally
            {
                if (attachedForegroundToTarget)
                {
                    _ = AttachThreadInput(foregroundThread, targetThread, false);
                }
                if (attachedForeground)
                {
                    _ = AttachThreadInput(currentThread, foregroundThread, false);
                }
                if (attachedTarget)
                {
                    _ = AttachThreadInput(currentThread, targetThread, false);
                }
            }

            if (!IsForeground(target))
            {
                keybd_event(VkMenu, 0, 0, UIntPtr.Zero);
                _ = SetForegroundWindow(target.Handle);
                keybd_event(VkMenu, 0, KeyEventKeyUp, UIntPtr.Zero);
            }

            if (!IsForeground(target))
            {
                SwitchToThisWindow(target.Handle, true);
            }

            if (!IsForeground(target))
            {
                _ = SetWindowPos(
                    target.Handle,
                    HwndTopmost,
                    0,
                    0,
                    0,
                    0,
                    SwpNoMove | SwpNoSize | SwpShowWindow);
                _ = BringWindowToTop(target.Handle);
                _ = SetForegroundWindow(target.Handle);
                _ = SetWindowPos(
                    target.Handle,
                    HwndNotTopmost,
                    0,
                    0,
                    0,
                    0,
                    SwpNoMove | SwpNoSize | SwpShowWindow);
            }

            if (!IsForeground(target))
            {
                var blockingForeground = GetForegroundWindow();
                if (blockingForeground != IntPtr.Zero && blockingForeground != target.Handle)
                {
                    keybd_event(VkMenu, 0, 0, UIntPtr.Zero);
                    keybd_event(VkTab, 0, 0, UIntPtr.Zero);
                    keybd_event(VkTab, 0, KeyEventKeyUp, UIntPtr.Zero);
                    keybd_event(VkMenu, 0, KeyEventKeyUp, UIntPtr.Zero);
                    Thread.Sleep(120);
                    _ = ShowWindowAsync(target.Handle, restore ? SwRestore : SwShow);
                    SwitchToThisWindow(target.Handle, true);
                    _ = SetForegroundWindow(target.Handle);
                }
            }

            if (!IsForeground(target))
            {
                var shellBridge = EnumerateVisibleWindows().FirstOrDefault(candidate =>
                    candidate.Handle != target.Handle
                    && string.Equals(
                        NormalizeProcessName(candidate.ProcessName),
                        "explorer",
                        StringComparison.OrdinalIgnoreCase));
                if (shellBridge is not null)
                {
                    _ = ShowWindowAsync(shellBridge.Handle, shellBridge.IsMinimized ? SwRestore : SwShow);
                    SwitchToThisWindow(shellBridge.Handle, true);
                    _ = SetForegroundWindow(shellBridge.Handle);
                    var bridgeWait = Stopwatch.StartNew();
                    while (!IsForeground(shellBridge)
                           && bridgeWait.Elapsed < TimeSpan.FromMilliseconds(600))
                    {
                        Thread.Sleep(25);
                    }
                    if (IsForeground(shellBridge))
                    {
                        _ = ShowWindowAsync(target.Handle, restore ? SwRestore : SwShow);
                        SwitchToThisWindow(target.Handle, true);
                        _ = SetForegroundWindow(target.Handle);
                    }
                }
            }

            if (!IsForeground(target))
            {
                var blockingForeground = GetForegroundWindow();
                if (blockingForeground != IntPtr.Zero && blockingForeground != target.Handle)
                {
                    _ = ShowWindowAsync(blockingForeground, SwMinimize);
                    Thread.Sleep(40);
                    _ = ShowWindowAsync(target.Handle, restore ? SwRestore : SwShow);
                    SwitchToThisWindow(target.Handle, true);
                    _ = SetForegroundWindow(target.Handle);
                }
            }

            return IsForeground(target);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsForeground(WindowTargetInfo target)
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;
        if (foreground == target.Handle) return true;
        _ = GetWindowThreadProcessId(foreground, out var processId);
        return processId == (uint)target.ProcessId;
    }

    private static string ReadWindowTitle(IntPtr handle)
    {
        var length = GetWindowTextLength(handle);
        if (length <= 0) return string.Empty;
        var buffer = new StringBuilder(length + 1);
        _ = GetWindowText(handle, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private delegate bool EnumWindowsProc(IntPtr handle, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, bool value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr handle, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(IntPtr handle, bool altTab);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr handle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr SetActiveWindow(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr handle, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);
}
