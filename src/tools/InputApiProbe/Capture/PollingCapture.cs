using System.Diagnostics;

namespace InputApiProbe;

/// <summary>A3: dedicated thread polling GetAsyncKeyState. periodMs = 0 spins continuously; otherwise sleeps periodMs between polls.</summary>
internal sealed class PollingCapture(ushort vk, int periodMs) : ICaptureStrategy
{
    private Thread? thread;
    private volatile bool stop;

    public string Id => periodMs == 0 ? "poll-spin" : $"poll-{periodMs}ms";
    public event Action<long>? Captured;

    public void Start()
    {
        stop = false;
        thread = new Thread(Loop) { IsBackground = true, Name = "ApiProbe-" + Id, Priority = ThreadPriority.AboveNormal };
        thread.Start();
    }

    private void Loop()
    {
        bool wasDown = (Native.GetAsyncKeyState(vk) & 0x8000) != 0;
        while (!stop)
        {
            bool down = (Native.GetAsyncKeyState(vk) & 0x8000) != 0;
            long tick = Stopwatch.GetTimestamp();
            if (down && !wasDown) Captured?.Invoke(tick);
            wasDown = down;
            if (periodMs > 0) Thread.Sleep(periodMs);
        }
    }

    public void Stop() { stop = true; thread?.Join(1000); thread = null; }
    public void Dispose() => Stop();
}
