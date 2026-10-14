using System.Windows;

namespace SkirkFps;
public partial class App : Application
{
    private Mutex? instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        instance = new Mutex(true, "Local\\SkirkFPS.SingleInstance", out var first);
        if (!first) { MessageBox.Show("YSRExtend 已在运行，请使用现有窗口。", "YSRExtend"); Shutdown(); return; }
        DispatcherUnhandledException += (_, args) => { args.Handled = true; MessageBox.Show(args.Exception.Message, "YSRExtend"); };
        base.OnStartup(e);
    }
    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
}
