using System.IO;
using System.Windows;
using System.Windows.Threading;
using MacroHid.Runtime;
using MacroStudio.Services;

namespace MacroStudio;

public partial class App : Application
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MacroHID", "crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        Log("=== App Starting ===");

        try
        {
            ThemeService.Initialize();
            Log("ThemeService OK");
            var runtimePrecision = RuntimePrecisionSettingsStore.Load();
            NativePlaybackWarmup.QueueWarmUpForPrecision(
                runtimePrecision.Precision,
                runtimePrecision.AffinityMask);
        }
        catch (Exception ex)
        {
            Log($"ThemeService FAILED: {ex.Message}");
            DialogOwnerService.MessageBoxSafe(null, $"Theme init failed:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        NativePlaybackWarmup.Shutdown();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var inner = e.Exception;
        while (inner.InnerException != null) inner = inner.InnerException;
        Log($"[UI ERROR] {inner.GetType().Name}: {inner.Message}\nStack (first 5):\n{string.Join("\n", inner.StackTrace?.Split('\n').Take(5) ?? [])}");
        DialogOwnerService.MessageBoxSafe(null, $"UI Error:\n{inner.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception;
        Log($"[DOMAIN ERROR] {ex?.GetType().Name}: {ex?.Message}");
    }

    public static void Log(string msg)
    {
        try
        {
            var dir = Path.GetDirectoryName(LogPath)!;
            Directory.CreateDirectory(dir);
            File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
        }
        catch { }
    }
}
