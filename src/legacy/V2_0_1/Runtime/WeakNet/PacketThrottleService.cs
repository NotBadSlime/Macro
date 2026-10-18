using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MacroHid.Runtime;

internal sealed class PacketThrottleService : IDisposable
{
    private const int MaxQueuedPackets = 256;
    private const int MaxQueuedBytes = 1024 * 1024;
    private const short Priority = 10;

    private readonly object gate = new();
    private readonly Queue<HeldPacket> queue = new();
    private Timer? timer;
    private Thread? receiveThread;
    private Thread? sendThread;
    private IntPtr handle;
    private volatile TrafficSnapshot snapshot = TrafficSnapshot.Empty;
    private string? path;
    private uint bytesPerSecond = 695;
    private volatile bool running;
    private bool explainedDriverError;
    private bool timerRaised;
    internal bool KeepWarm { get; set; }
    private long outboundNextTick;
    private int queuedBytes;

    public bool IsActive
    {
        get
        {
            lock (gate)
            {
                return running && path is not null;
            }
        }
    }

    public void EnsurePump()
    {
        lock (gate)
        {
            if (running && WinDivertNative.IsOpen(handle))
            {
                return;
            }
        }

        TryStart(string.Empty, 695, out _);
        lock (gate)
        {
            path = null;
            snapshot = TrafficSnapshot.Empty;
        }
    }

    public bool TryStart(string executablePath, uint rate, out string? error)
    {
        var retarget = false;
        lock (gate)
        {
            retarget = running && WinDivertNative.IsOpen(handle);
        }

        if (!retarget)
        {
            Stop();
        }

        TrafficSnapshot? captured = null;
        if (retarget && !string.IsNullOrWhiteSpace(executablePath))
        {
            captured = TrafficSnapshot.Capture(executablePath);
        }

        lock (gate)
        {
            path = string.IsNullOrWhiteSpace(executablePath) ? null : executablePath;
            bytesPerSecond = rate < 1 ? 695 : rate;
            explainedDriverError = false;
            outboundNextTick = 0;
            if (retarget)
            {
                if (captured is not null)
                {
                    snapshot = captured;
                }

                error = null;
                return true;
            }
            handle = WinDivertNative.OpenTraffic(Priority);
            if (!WinDivertNative.IsOpen(handle))
            {
                error = Describe(Marshal.GetLastWin32Error());
                return false;
            }

            snapshot = string.IsNullOrWhiteSpace(executablePath)
                ? TrafficSnapshot.Empty
                : TrafficSnapshot.Capture(executablePath);
            running = true;
            if (!timerRaised)
            {
                RuntimeNativeMethods.timeBeginPeriod(1);
                timerRaised = true;
            }

            timer = new Timer(_ => Refresh(), null, 0, 8);
            receiveThread = new Thread(ReceiveLoop)
            {
                IsBackground = true,
                Name = "MacroHID.WeakNetThrottle",
                Priority = ThreadPriority.Highest
            };
            sendThread = new Thread(SendLoop)
            {
                IsBackground = true,
                Name = "MacroHID.WeakNetRelease",
                Priority = ThreadPriority.Highest
            };
            receiveThread.Start();
            sendThread.Start();
        }

        error = null;
        return true;
    }

    public void Stop()
    {
        if (KeepWarm && running)
        {
            IntPtr warmHandle;
            lock (gate)
            {
                path = null;
                snapshot = TrafficSnapshot.Empty;
                outboundNextTick = 0;
                warmHandle = handle;
            }
            Flush(warmHandle);
            lock (gate) Monitor.PulseAll(gate);
            return;
        }

        running = false;
        Thread? receiver;
        Thread? sender;
        Timer? refresh;
        IntPtr openHandle;
        var wasRunning = false;
        lock (gate)
        {
            wasRunning = handle != IntPtr.Zero || timer != null || receiveThread != null;
            refresh = timer;
            timer = null;
            receiver = receiveThread;
            sender = sendThread;
            receiveThread = null;
            sendThread = null;
            openHandle = handle;
            snapshot = TrafficSnapshot.Empty;
            path = null;
            Monitor.PulseAll(gate);
        }

        if (!wasRunning && !WinDivertNative.IsOpen(openHandle))
        {
            return;
        }

        if (WinDivertNative.IsOpen(openHandle))
        {
            WinDivertNative.Shutdown(openHandle);
        }

        receiver?.Join(TimeSpan.FromSeconds(1));
        sender?.Join(TimeSpan.FromSeconds(1));
        Flush(openHandle);
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

        lock (gate)
        {
            if (handle == openHandle)
            {
                handle = IntPtr.Zero;
            }

            queue.Clear();
            queuedBytes = 0;
        }
    }

    public void Dispose()
    {
        Stop();
    }

    private void Refresh()
    {
        string? current;
        lock (gate)
        {
            if (!running || string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            current = path;
        }

        var snap = TrafficSnapshot.Capture(current);
        lock (gate)
        {
            if (running && string.Equals(path, current, StringComparison.OrdinalIgnoreCase))
            {
                snapshot = snap;
            }
        }
    }

    private void ReceiveLoop()
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

                if (!WinDivertNative.IsOutbound(address))
                {
                    WinDivertNative.Send(openHandle, packetPtr, length, addressPtr);
                    continue;
                }

                var current = snapshot;
                var owned = FlowLookup.TryGetOwner(packet.AsSpan(0, (int)length), current, out var pid, out _);
                if (!owned || !current.Targets(pid))
                {
                    WinDivertNative.Send(openHandle, packetPtr, length, addressPtr);
                    continue;
                }

                Hold(packet, (int)length, address);
            }
        }
        finally
        {
            packetPin.Free();
            addressPin.Free();
        }
    }

    private void Hold(byte[] packet, int length, byte[] address)
    {
        var copy = new byte[length];
        var addressCopy = new byte[WinDivertNative.AddressSize];
        Buffer.BlockCopy(packet, 0, copy, 0, length);
        Buffer.BlockCopy(address, 0, addressCopy, 0, Math.Min(address.Length, addressCopy.Length));
        lock (gate)
        {
            if (!running)
            {
                return;
            }

            if (queue.Count >= MaxQueuedPackets || queuedBytes + length > MaxQueuedBytes)
            {
                return;
            }

            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            var release = BandwidthPacer.Charge(ref outboundNextTick, now, length, bytesPerSecond, System.Diagnostics.Stopwatch.Frequency);
            if (path is null) release = now;
            queue.Enqueue(new HeldPacket(copy, addressCopy, release));
            queuedBytes += length;
            Monitor.Pulse(gate);
        }
    }

    private void SendLoop()
    {
        var ready = new List<HeldPacket>();
        while (true)
        {
            IntPtr openHandle;
            lock (gate)
            {
                while (running && queue.Count == 0)
                {
                    Monitor.Wait(gate);
                }

                if (!running && queue.Count == 0)
                {
                    return;
                }

                openHandle = handle;
                var now = System.Diagnostics.Stopwatch.GetTimestamp();
                if (running && queue.Count > 0 && queue.Peek().ReleaseTick > now)
                {
                    var waitMs = (queue.Peek().ReleaseTick - now) * 1000 / System.Diagnostics.Stopwatch.Frequency;
                    if (waitMs > 50)
                    {
                        waitMs = 50;
                    }

                    if (waitMs < 1)
                    {
                        waitMs = 1;
                    }

                    Monitor.Wait(gate, (int)waitMs);
                    continue;
                }

                now = System.Diagnostics.Stopwatch.GetTimestamp();
                while (queue.Count > 0 && (!running || queue.Peek().ReleaseTick <= now))
                {
                    var item = queue.Dequeue();
                    queuedBytes -= item.Packet.Length;
                    ready.Add(item);
                    if (!running)
                    {
                        now = long.MaxValue;
                    }
                }
            }

            foreach (var item in ready)
            {
                SendHeld(openHandle, item);
            }

            ready.Clear();
            if (!running)
            {
                return;
            }
        }
    }

    private void Flush(IntPtr openHandle)
    {
        HeldPacket[] leftover;
        lock (gate)
        {
            leftover = queue.ToArray();
            queue.Clear();
            queuedBytes = 0;
        }

        foreach (var item in leftover)
        {
            SendHeld(openHandle, item);
        }
    }

    private static void SendHeld(IntPtr openHandle, HeldPacket item)
    {
        if (!WinDivertNative.IsOpen(openHandle))
        {
            return;
        }

        var packetPin = GCHandle.Alloc(item.Packet, GCHandleType.Pinned);
        var addressPin = GCHandle.Alloc(item.Address, GCHandleType.Pinned);
        try
        {
            WinDivertNative.Send(
                openHandle,
                packetPin.AddrOfPinnedObject(),
                (uint)item.Packet.Length,
                addressPin.AddrOfPinnedObject());
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

    private sealed record HeldPacket(byte[] Packet, byte[] Address, long ReleaseTick);
}
