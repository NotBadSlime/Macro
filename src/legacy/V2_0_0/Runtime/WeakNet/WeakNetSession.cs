using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MacroHid.Runtime;

public static class WeakNetSession
{
    private static readonly HashSet<string> DeniedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "svchost.exe",
        "lsass.exe",
        "csrss.exe",
        "services.exe",
        "wininit.exe",
        "smss.exe"
    };

    private static readonly object Gate = new();
    private static PacketDropService? service;

    public static bool IsActive
    {
        get
        {
            lock (Gate)
            {
                return service?.IsActive == true;
            }
        }
    }

    public static bool IsDeniedProcess(string? processName)
    {
        var file = WeakNetProcessQuery.NormalizeFileName(processName);
        return file.Length > 0 && DeniedNames.Contains(file);
    }

    public static bool TryStart(string processName, TimeSpan timeShift, out string? error)
    {
        if (!PlaybackEffectScope.TryAcquire("network", out error)) return false;
        if (IsDeniedProcess(processName))
        {
            error = $"不能对系统进程 {WeakNetProcessQuery.NormalizeFileName(processName)} 开启断网。";
            return false;
        }

        if (!WeakNetProcessQuery.TryResolveRunningPath(processName, out var path))
        {
            error = $"进程 {WeakNetProcessQuery.NormalizeFileName(processName)} 没有在运行。";
            return false;
        }

        lock (Gate)
        {
            service ??= new PacketDropService();
            NativeFiles.EnsureExtracted();
            if (!service.TryStart(path, out error))
            {
                return false;
            }
        }

        try
        {
            SystemTimeShift.ShiftForwardOnce(timeShift);
        }
        catch (Exception ex)
        {
            Stop();
            error = ex.Message;
            return false;
        }

        error = null;
        return true;
    }

    public static void EndPlayback()
    {
        if (!PlaybackEffectScope.CanRelease("network")) return;
        try { Stop(); }
        finally { SystemTimeShift.Restore(); }
    }

    public static void Stop()
    {
        if (!PlaybackEffectScope.CanRelease("network")) return;
        PacketDropService? current;
        lock (Gate)
        {
            current = service;
        }

        current?.Stop();
    }

    internal static void KeepWarm()
    {
        lock (Gate)
        {
            service ??= new PacketDropService();
            service.KeepWarm = true;
            service.EnsurePump();
        }
    }

    internal static void ReleaseWarm()
    {
        PacketDropService? current;
        lock (Gate)
        {
            current = service;
            if (current is not null)
            {
                current.KeepWarm = false;
            }
        }

        current?.Stop();
    }
}

internal sealed class PacketDropService : IDisposable
{
    private readonly object gate = new();
    private readonly object firewallGate = new();
    private Timer? timer;
    private Thread? thread;
    private IntPtr handle;
    private volatile TrafficSnapshot snapshot = TrafficSnapshot.Empty;
    private string? path;
    private volatile bool running;
    private volatile bool dropping;
    private bool explainedDriverError;
    private bool timerRaised;
    private bool firewallUsed;
    private int sendFailures;
    internal bool KeepWarm { get; set; }

    public bool IsActive => dropping;

    public void EnsurePump()
    {
        TryStartCore(null, out _);
    }

    public bool TryStart(string executablePath, out string? error)
    {
        if (!TryStartCore(executablePath, out error))
        {
            return false;
        }

        try
        {
            lock (firewallGate)
            {
                firewallUsed = true;
                AppFirewall.Block(executablePath);
            }
            return true;
        }
        catch (Exception ex)
        {
            try { Stop(); }
            catch (Exception cleanup) { error = $"防火墙设置失败：{ex.Message}；恢复失败：{cleanup.Message}"; return false; }
            error = $"防火墙设置失败：{ex.Message}";
            return false;
        }
    }

    public void Stop()
    {
        dropping = false;
        lock (gate)
        {
            path = null;
            snapshot = TrafficSnapshot.Empty;
        }

        try
        {
            lock (firewallGate)
            {
                if (firewallUsed)
                {
                    AppFirewall.Clear();
                    firewallUsed = false;
                }
            }
        }
        finally { if (!KeepWarm) Shutdown(); }
    }

    public void Dispose()
    {
        KeepWarm = false;
        Stop();
    }

    private bool TryStartCore(string? executablePath, out string? error)
    {
        var needOpen = false;
        lock (gate)
        {
            needOpen = !(running && WinDivertNative.IsOpen(handle));
        }

        if (needOpen)
        {
            NativeFiles.EnsureExtracted();
            var opened = WinDivertNative.OpenOutbound();
            var openError = Marshal.GetLastWin32Error();
            lock (gate)
            {
                if (running && WinDivertNative.IsOpen(handle))
                {
                    WinDivertNative.Close(opened);
                }
                else if (!WinDivertNative.IsOpen(opened))
                {
                    error = Describe(openError);
                    return false;
                }
                else
                {
                    handle = opened;
                    running = true;
                    if (!timerRaised)
                    {
                        RuntimeNativeMethods.timeBeginPeriod(1);
                        timerRaised = true;
                    }

                    timer = new Timer(_ => Refresh(), null, 0, 8);
                    thread = new Thread(ForwardLoop)
                    {
                        IsBackground = true,
                        Name = "MacroHID.WeakNet",
                        Priority = ThreadPriority.Highest
                    };
                    thread.Start();
                }
            }
        }

        if (string.IsNullOrWhiteSpace(executablePath))
        {
            error = null;
            return true;
        }

        var snap = TrafficSnapshot.Capture(executablePath);
        lock (gate)
        {
            path = executablePath;
            snapshot = snap;
        }

        dropping = true;
        error = null;
        return true;
    }

    private void Shutdown()
    {
        running = false;
        dropping = false;
        Thread? worker;
        Timer? refresh;
        IntPtr openHandle;
        lock (gate)
        {
            refresh = timer;
            timer = null;
            worker = thread;
            thread = null;
            openHandle = handle;
            handle = IntPtr.Zero;
            snapshot = TrafficSnapshot.Empty;
            path = null;
        }

        if (WinDivertNative.IsOpen(openHandle))
        {
            WinDivertNative.Shutdown(openHandle);
        }

        worker?.Join(TimeSpan.FromSeconds(1));
        try
        {
            refresh?.Change(Timeout.Infinite, Timeout.Infinite);
        }
        catch
        {
            // 定时器可能已经释放。
        }

        WinDivertNative.Close(openHandle);
        if (timerRaised)
        {
            RuntimeNativeMethods.timeEndPeriod(1);
            timerRaised = false;
        }
    }

    private void Refresh()
    {
        string? current;
        lock (gate)
        {
            if (!dropping || string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            current = path;
        }

        var snap = TrafficSnapshot.Capture(current);
        lock (gate)
        {
            if (dropping && string.Equals(path, current, StringComparison.OrdinalIgnoreCase))
            {
                snapshot = snap;
            }
        }
    }

    private void ForwardLoop()
    {
        var packet = new byte[65535];
        var address = new byte[WinDivertNative.AddressSize];
        var packetPin = GCHandle.Alloc(packet, GCHandleType.Pinned);
        var addressPin = GCHandle.Alloc(address, GCHandleType.Pinned);
        try
        {
            var packetPtr = packetPin.AddrOfPinnedObject();
            var addressPtr = addressPin.AddrOfPinnedObject();
            while (running)
            {
                var openHandle = handle;
                if (!WinDivertNative.IsOpen(openHandle))
                {
                    break;
                }

                if (!WinDivertNative.Recv(openHandle, packetPtr, (uint)packet.Length, out var length, addressPtr))
                {
                    if (!running)
                    {
                        break;
                    }

                    var error = Marshal.GetLastWin32Error();
                    if (error is 232 or 995 or 6)
                    {
                        break;
                    }

                    continue;
                }

                if (length == 0 || length > packet.Length)
                {
                    continue;
                }

                if (!dropping)
                {
                    WinDivertNative.Send(openHandle, packetPtr, length, addressPtr);
                    continue;
                }

                var current = snapshot;
                var owned = FlowLookup.TryGetOwner(packet.AsSpan(0, (int)length), current, out var pid, out _);
                if (owned && current.Targets(pid))
                {
                    continue;
                }

                if (!WinDivertNative.Send(openHandle, packetPtr, length, addressPtr) && running && sendFailures == 0)
                {
                    sendFailures++;
                }
            }
        }
        finally
        {
            packetPin.Free();
            addressPin.Free();
        }
    }

    private string Describe(int error)
    {
        var message = new Win32Exception(error).Message;
        if (explainedDriverError || error is not (5 or 577 or 1275))
        {
            return $"{message}（{error}）";
        }

        explainedDriverError = true;
        return $"{message}（{error}）。若安全软件拦截了 WinDivert64.sys，请允许该驱动后再试。";
    }
}
