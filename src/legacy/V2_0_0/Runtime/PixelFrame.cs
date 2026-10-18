using System.Buffers;
using System.Diagnostics;
using MacroHid.Core;

namespace MacroHid.Runtime;

internal readonly record struct PixelValue(byte R, byte G, byte B);

// The owner and each consumer hold one reference. The pool never sees a live frame.
internal sealed class PixelFrame : IDisposable
{
    private readonly byte[] bytes;
    private readonly bool pooled;
    private int references = 1;
    internal int Count { get; }
    internal byte[] Bytes => bytes;
    internal PixelFrame(byte[] bytes, int count, bool pooled = false)
    {
        if (count < 0 || checked(count * 4) > bytes.Length) throw new ArgumentException("Truncated BGRA frame");
        this.bytes = bytes; Count = count; this.pooled = pooled;
    }
    internal PixelValue this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
            int p = index * 4;
            return new(bytes[p + 2], bytes[p + 1], bytes[p]);
        }
    }
    internal double Luma(int index)
    {
        var p = this[index];
        return p.R * .299 + p.G * .587 + p.B * .114;
    }
    internal static PixelFrame FromRgb(RgbColor[] pixels)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(checked(pixels.Length * 4));
        for (int i = 0; i < pixels.Length; i++)
        {
            buffer[i * 4] = pixels[i].B; buffer[i * 4 + 1] = pixels[i].G; buffer[i * 4 + 2] = pixels[i].R;
            buffer[i * 4 + 3] = 255;
        }
        return new(buffer, pixels.Length, true);
    }
    internal PixelFrameLease Lease()
    {
        // Called while the cache owns a reference, under its entry lock.
        Interlocked.Increment(ref references);
        return new(this);
    }
    public void Dispose()
    {
        if (Interlocked.Decrement(ref references) == 0 && pooled) ArrayPool<byte>.Shared.Return(bytes);
    }
}

internal sealed class PixelFrameLease(PixelFrame frame) : IDisposable
{
    private PixelFrame? value = frame;
    internal PixelFrame Frame => value ?? throw new ObjectDisposedException(nameof(PixelFrameLease));
    public void Dispose() => Interlocked.Exchange(ref value, null)?.Dispose();
}

internal sealed class PixelFrameCache : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<ScreenRegion, Entry> entries = new();
    private bool disposed;
    private sealed class Entry
    {
        internal readonly SemaphoreSlim Work = new(1);
        internal PixelFrame? Frame;
        internal long Completed;
        internal int Users;
    }
    internal PixelFrameLease Get(ScreenRegion region, CancellationToken token)
    {
        Entry entry;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!entries.TryGetValue(region, out entry!))
            {
                if (entries.Count >= 16)
                {
                    foreach (var pair in entries.Where(p => p.Value.Users == 0).ToArray())
                    {
                        entries.Remove(pair.Key);
                        pair.Value.Frame?.Dispose();
                        pair.Value.Work.Dispose();
                    }
                }
                entries.Add(region, entry = new());
            }
            entry.Users++;
        }
        bool held = false;
        try
        {
            entry.Work.Wait(token); held = true;
            token.ThrowIfCancellationRequested();
            if (entry.Frame is null || Stopwatch.GetTimestamp() - entry.Completed >= Stopwatch.Frequency / 1000)
            {
                var next = ScreenCaptureService.CaptureFrame(region);
                entry.Frame?.Dispose();
                entry.Frame = next;
                entry.Completed = Stopwatch.GetTimestamp();
            }
            return entry.Frame.Lease();
        }
        finally
        {
            if (held) entry.Work.Release();
            lock (gate)
            {
                if (--entry.Users == 0 && disposed) { entry.Frame?.Dispose(); entry.Work.Dispose(); }
            }
        }
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            foreach (var entry in entries.Values.Where(e => e.Users == 0)) { entry.Frame?.Dispose(); entry.Work.Dispose(); }
            entries.Clear();
        }
    }
}
