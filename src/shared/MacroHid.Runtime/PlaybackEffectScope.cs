namespace MacroHid.Runtime;

// AsyncLocal flows into condition workers and parallel branches of the same playback.
internal sealed class PlaybackEffectScope : IDisposable
{
    private static readonly AsyncLocal<Guid?> Current = new();
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Guid> Owners = [];
    private readonly Guid? previous = Current.Value;
    private readonly Guid id = Guid.NewGuid();

    public PlaybackEffectScope() => Current.Value = id;

    internal static bool TryAcquire(string resource, out string? error)
    {
        lock (Gate)
        {
            if (Current.Value is null)
            {
                error = null;
                return true;
            }
            var owner = Current.Value ?? Guid.Empty;
            if (Owners.TryGetValue(resource, out var existing) && existing != owner)
            {
                error = "另一个宏正在控制此效果，请先停止该宏。";
                return false;
            }
            Owners[resource] = owner;
            error = null;
            return true;
        }
    }

    internal static bool CanRelease(string resource)
    {
        lock (Gate)
            return !Owners.TryGetValue(resource, out var owner) || owner == (Current.Value ?? Guid.Empty);
    }

    public void Dispose()
    {
        var failures = new List<Exception>();
        try
        {
            if (Owns("network"))
            {
                Cleanup(WeakNetSession.EndPlayback, failures);
                Cleanup(NetLimitSession.EndPlayback, failures);
            }
            if (Owns("frames")) Cleanup(FrameLockSession.EndPlayback, failures);
            if (Owns("ysr-frames")) Cleanup(YsrFpsSession.EndPlayback, failures);
            if (Owns("fufu-frames")) Cleanup(FufuFpsSession.EndPlayback, failures);
            if (Owns("game-memory-frames")) Cleanup(GameMemoryFpsSession.EndPlayback, failures);
        }
        finally
        {
            lock (Gate)
                foreach (var key in Owners.Where(pair => pair.Value == id).Select(pair => pair.Key).ToArray())
                    Owners.Remove(key);
            Current.Value = previous;
        }
        if (failures.Count > 0)
            throw new InvalidOperationException("播放已停止，但效果恢复失败：" + string.Join("；", failures.Select(e => e.Message)), new AggregateException(failures));
    }

    private bool Owns(string resource)
    {
        lock (Gate) return Owners.TryGetValue(resource, out var owner) && owner == id;
    }

    private static void Cleanup(Action action, List<Exception> failures)
    {
        try { action(); }
        catch (Exception ex) { failures.Add(ex); }
    }
}
