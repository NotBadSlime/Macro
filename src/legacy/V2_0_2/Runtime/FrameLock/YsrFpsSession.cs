using System.IO.MemoryMappedFiles;
using MacroHid.Core;

namespace MacroHid.Runtime;

internal interface IYsrFpsChannel : IDisposable
{
    int Read();
    void Write(int fps);
    void Restore(int fps) => Write(fps);
}

// Protocol from the user's fps_toggle.py: int32 FPS at byte 8 in the named section.
public static class YsrFpsSession
{
    private const string Resource = "ysr-frames";
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
        if (step.Backend == FpsBackend.Fufu) return FufuFpsSession.TryApply(step, out error);
        if (step.Backend == FpsBackend.Ky3) return GameMemoryFpsSession.TryApply(step, out error);
        lock (Gate)
        {
            if (step.Action == YsrFpsAction.Stop || (step.Action == YsrFpsAction.Toggle && channel is not null))
                return TryStop(out error);
            if (step.ToggleFps is < 1 or > 1000) { error = "YSR 帧率必须在 1 到 1000 之间。"; return false; }
            if (!PlaybackEffectScope.TryAcquire(Resource, out error)) return false;
            try
            {
                if (channel is null)
                {
                    var opened = Open();
                    try
                    {
                        var baseline = opened.Read();
                        if (baseline <= 0) throw new InvalidOperationException("YSR 当前帧率无效，未修改帧率。");
                        opened.Write(step.ToggleFps);
                        originalFps = baseline;
                        channel = opened;
                    }
                    catch { opened.Dispose(); throw; }
                }
                else channel.Write(step.ToggleFps);
                targetFps = step.ToggleFps;
                LastError = null;
                timer ??= new Timer(_ => Refresh(), null, 50, 50);
                error = null;
                return true;
            }
            catch (Exception ex) { error = LastError = Describe(ex); return false; }
        }
    }

    public static bool TryStop(out string? error)
    {
        if (!PlaybackEffectScope.CanRelease(Resource)) { error = "另一个宏正在控制 YSR 帧率。"; return false; }
        lock (Gate)
        {
            timer?.Dispose();
            timer = null;
            if (channel is null) { error = null; return true; }
            try
            {
                channel.Write(originalFps);
                channel.Dispose();
                channel = null;
                LastError = null;
                error = null;
                return true;
            }
            catch (Exception ex) { error = LastError = Describe(ex); return false; }
        }
    }

    public static void EndPlayback()
    {
        if (!TryStop(out var error)) throw new InvalidOperationException("YSR 帧率恢复失败：" + error);
    }

    private static void Refresh()
    {
        lock (Gate)
        {
            if (channel is null || timer is null) return;
            try { channel.Write(targetFps); }
            catch (Exception ex) { LastError = Describe(ex); timer.Dispose(); timer = null; }
        }
    }

    internal static void ResetForTests()
    {
        lock (Gate) { timer?.Dispose(); timer = null; channel?.Dispose(); channel = null; TestOpenChannel = null; LastError = null; }
    }

    private static IYsrFpsChannel Open() => TestOpenChannel?.Invoke() ?? new SharedChannel();
    private static string Describe(Exception ex) => ex is UnauthorizedAccessException
        ? "没有权限访问 YSR，请以与解帧器相同的权限运行 MacroStudio。"
        : ex is FileNotFoundException ? "未检测到 YSR，请先通过解帧器启动游戏。" : ex.Message;

    internal static IYsrFpsChannel OpenTestMapping(string name) => new SharedChannel(name);

    private sealed class SharedChannel : IYsrFpsChannel
    {
        private readonly MemoryMappedFile mapping;
        private readonly MemoryMappedViewAccessor view;
        public SharedChannel(string name = "6F83E7C5-A59D-B27D-3BBE-7D67BA1B4D5B")
        {
            mapping = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.ReadWrite);
            try { view = mapping.CreateViewAccessor(8, 4, MemoryMappedFileAccess.ReadWrite); }
            catch { mapping.Dispose(); throw; }
        }
        public int Read() => view.ReadInt32(0);
        public void Write(int fps) => view.Write(0, fps);
        public void Dispose() { view.Dispose(); mapping.Dispose(); }
    }
}
