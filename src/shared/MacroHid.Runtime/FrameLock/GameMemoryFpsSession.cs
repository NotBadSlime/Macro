using MacroHid.Core;

namespace MacroHid.Runtime;

public static class GameMemoryFpsSession
{
    private const string Resource = "game-memory-frames";
    private static readonly object Gate = new();
    private static IYsrFpsChannel? channel;
    private static Timer? timer;
    private static int originalFps;
    private static int targetFps;
    internal static Func<IYsrFpsChannel>? TestOpenChannel { get; set; }
    public static string? LastError { get; private set; }

    public static bool TryReadCurrent(out int fps)
    {
        lock (Gate)
        {
            try
            {
                if (channel is not null) fps = channel.Read();
                else { using var reader = Open(); fps = reader.Read(); }
                return fps > 0;
            }
            catch { fps = 0; return false; }
        }
    }

    public static bool TryApply(YsrFpsToggleStep step, out string? error)
    {
        lock (Gate)
        {
            if (step.Action == YsrFpsAction.Stop || (step.Action == YsrFpsAction.Toggle && channel is not null))
                return TryStop(out error);
            if (step.ToggleFps != 0 && step.ToggleFps is < 30 or > 1000) { error = "KY3 目标帧率必须为 30–1000，或 0（999 FPS）。"; return false; }
            if (!PlaybackEffectScope.TryAcquire(Resource, out error)) return false;
            try
            {
                if (channel is null)
                {
                    var opened = Open();
                    try
                    {
                        var baseline = opened.Read();
                        if (baseline <= 0) throw new InvalidOperationException("KY3 锁帧 当前帧率无效，未修改帧率。");
                        opened.Write(step.ToggleFps);
                        originalFps = baseline;
                        channel = opened;
                    }
                    catch { opened.Dispose(); throw; }
                }
                else channel.Write(step.ToggleFps);
                targetFps = step.ToggleFps;
                GameFpsDiagnostics.Log($"START target={targetFps} original={originalFps}");
                LastError = null;
                timer ??= new Timer(_ => Refresh(), null, 100, 100);
                error = null;
                return true;
            }
            catch (Exception ex) { GameFpsDiagnostics.Log($"ERROR {ex}"); error = LastError = Describe(ex); return false; }
        }
    }

    public static bool TryStop(out string? error)
        => Stop(out error, false);

    public static bool RestoreOnShutdown(out string? error) => Stop(out error, true);

    private static bool Stop(out string? error, bool shutdown)
    {
        if (!shutdown && !PlaybackEffectScope.CanRelease(Resource)) { error = "另一个宏正在控制 KY3 锁帧 帧率。"; return false; }
        lock (Gate)
        {
            timer?.Dispose();
            timer = null;
            if (channel is null) { error = null; return true; }
            try
            {
                channel.Restore(originalFps);
                channel.Dispose();
                channel = null;
                LastError = null;
                error = null;
                return true;
            }
            catch (Exception ex) { if (ex is ConfigConflictException) { channel?.Dispose(); channel = null; } GameFpsDiagnostics.Log($"RESTORE ERROR {ex}"); error = LastError = Describe(ex); return false; }
        }
    }

    public static void EndPlayback()
    {
        if (!TryStop(out var error)) throw new InvalidOperationException("KY3 锁帧 帧率恢复失败：" + error);
    }

    private static void Refresh()
    {
        lock (Gate)
        {
            if (channel is null || timer is null) return;
            try { if (channel is ConfigFpsChannel configuration) configuration.Check(); }
            catch (Exception ex) { LastError = Describe(ex); timer.Dispose(); timer = null; if (ex is ConfigConflictException) { channel?.Dispose(); channel = null; } UnlockerConfiguration.Report(FpsBackend.Ky3, LastError); }
        }
    }

    internal static void ResetForTests()
    {
        lock (Gate) { timer?.Dispose(); timer = null; channel?.Dispose(); channel = null; TestOpenChannel = null; LastError = null; }
    }

    private static IYsrFpsChannel Open() => TestOpenChannel?.Invoke() ?? UnlockerTargetChannel.Open(FpsBackend.Ky3);
    private static string Describe(Exception ex) => GameFpsDiagnostics.Describe(ex);

}
