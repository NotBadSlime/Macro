using System.IO;
using System.IO.MemoryMappedFiles;

namespace SkirkFps;
public sealed class IpcController : IDisposable
{
    public const string MappingName = "6F83E7C5-A59D-B27D-3BBE-7D67BA1B4D5B";
    private readonly string name;
    private readonly object gate = new();
    private MemoryMappedFile? mapping;
    private MemoryMappedViewAccessor? view;
    private Timer? refresh;
    private int baseline;
    private int target;
    public string? Error { get; private set; }
    public bool Active { get { lock (gate) return view is not null; } }
    public IpcController(string name = MappingName) => this.name = name;
    public int? ReadCurrent()
    {
        lock (gate)
        {
            try
            {
                if (view is not null) return view.ReadInt32(0);
                using var map = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
                using var accessor = map.CreateViewAccessor(8, 4, MemoryMappedFileAccess.Read);
                return accessor.ReadInt32(0);
            }
            catch { return null; }
        }
    }
    public void SetTarget(int fps)
    {
        if (fps is < 1 or > 1000) throw new InvalidOperationException("目标帧率必须是 1–1000 的整数。");
        lock (gate)
        {
            if (view is null)
            {
                MemoryMappedFile? opened = null;
                MemoryMappedViewAccessor? accessor = null;
                try
                {
                    opened = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.ReadWrite);
                    accessor = opened.CreateViewAccessor(8, 4, MemoryMappedFileAccess.ReadWrite);
                    var original = accessor.ReadInt32(0);
                    if (original <= 0) throw new InvalidOperationException("YSR 当前帧率无效，尚未修改。");
                    accessor.Write(0, fps);
                    baseline = original;
                    mapping = opened; view = accessor;
                }
                catch (Exception ex) { accessor?.Dispose(); opened?.Dispose(); throw Explain(ex); }
            }
            else view.Write(0, fps);
            target = fps;
            Error = null;
            refresh ??= new Timer(_ => Rewrite(), null, 50, 50);
        }
    }
    private void Rewrite()
    {
        lock (gate)
        {
            if (view is null || refresh is null) return;
            try { view.Write(0, target); }
            catch (Exception ex) { Error = Explain(ex).Message; refresh.Dispose(); refresh = null; }
        }
    }
    public void Restore()
    {
        lock (gate)
        {
            refresh?.Dispose(); refresh = null;
            if (view is null) return;
            view.Write(0, baseline);
            view.Dispose(); mapping?.Dispose(); view = null; mapping = null; Error = null;
        }
    }
    private static Exception Explain(Exception ex) => new InvalidOperationException(ex switch
    {
        FileNotFoundException => "未检测到 YSR。请先通过解帧器启动游戏。",
        UnauthorizedAccessException => "YSR 访问被拒绝，请以与解帧器相同的权限运行本程序。",
        _ => ex.Message
    }, ex);
    public void Dispose() => Restore();
}
