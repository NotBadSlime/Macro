using System.Windows;
using System.Windows.Threading;
using MacroHid.Runtime;
using MacroStudio.Services;

namespace MacroStudio;

public partial class App : Application
{
    private bool dispatcherErrorDialogPending;

    protected override void OnStartup(StartupEventArgs e)
    {
        AppLog.Initialize();
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        base.OnStartup(e);

        Log("=== App Starting ===");
        UnlockerConfiguration.Faulted += (_, message) => Dispatcher.BeginInvoke(new Action(() =>
            DialogOwnerService.MessageBoxSafe(MainWindow, message, "解帧器配置控制已停止", MessageBoxButton.OK, MessageBoxImage.Warning)));
        var recovered = UnlockerConfiguration.RecoverPending();
        if (recovered.Count > 0) Dispatcher.BeginInvoke(new Action(() =>
            DialogOwnerService.MessageBoxSafe(MainWindow, string.Join("\n", recovered), "解帧器配置恢复", MessageBoxButton.OK, MessageBoxImage.Information)));

        try
        {
            ThemeService.Initialize();
            Log("ThemeService OK");
            var runtimePrecision = RuntimePrecisionSettingsStore.Load();
            NativePlaybackWarmup.QueueWarmUpForPrecision(
                runtimePrecision.Precision,
                runtimePrecision.AffinityMask,
                runtimePrecision.Generation);
            PlaybackPathWarmup.QueueRun();
        }
        catch (Exception ex)
        {
            AppLog.Error("Startup", "ThemeService failed", ex);
            DialogOwnerService.MessageBoxSafe(null, $"Theme init failed:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log($"=== App Exiting ({e.ApplicationExitCode}) ===");
        RestoreUnlockerTargets();
        PlaybackInputSafetyRegistry.ReleaseAllActiveInputs();
        NativePlaybackWarmup.Shutdown();
        AppLog.Flush(TimeSpan.FromSeconds(2));
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        PlaybackInputSafetyRegistry.ReleaseAllActiveInputs();
        var inner = e.Exception;
        while (inner.InnerException != null) inner = inner.InnerException;
        AppLog.Error("UI", "Unhandled dispatcher exception", e.Exception);
        if (dispatcherErrorDialogPending) return;

        dispatcherErrorDialogPending = true;
        try
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    DialogOwnerService.MessageBoxSafe(null, $"UI Error:\n{inner.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                catch (Exception dialogException)
                {
                    Log($"[UI ERROR DIALOG FAILED] {dialogException.GetType().Name}: {dialogException.Message}");
                }
                finally
                {
                    dispatcherErrorDialogPending = false;
                }
            }), DispatcherPriority.ContextIdle);
        }
        catch (Exception dispatchException)
        {
            dispatcherErrorDialogPending = false;
            Log($"[UI ERROR DISPATCH FAILED] {dispatchException.GetType().Name}: {dispatchException.Message}");
        }
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        PlaybackInputSafetyRegistry.ReleaseAllActiveInputs();
        var ex = e.ExceptionObject as Exception;
        AppLog.Error("App", $"Unhandled exception; terminating={e.IsTerminating}", ex);
    }

    private static void OnProcessExit(object? sender, EventArgs e)
    {
        RestoreUnlockerTargets();
        PlaybackInputSafetyRegistry.ReleaseAllActiveInputs();
    }

    private static void RestoreUnlockerTargets()
    {
        try { if (!FufuFpsSession.RestoreOnShutdown(out var fufuError)) AppLog.Error("FPS", "EXIT RESTORE " + fufuError); }
        catch (Exception ex) { AppLog.Error("FPS", "EXIT RESTORE Fufu", ex); }
        try { if (!GameMemoryFpsSession.RestoreOnShutdown(out var ky3Error)) AppLog.Error("FPS", "EXIT RESTORE " + ky3Error); }
        catch (Exception ex) { AppLog.Error("FPS", "EXIT RESTORE KY3", ex); }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        PlaybackInputSafetyRegistry.ReleaseAllActiveInputs();
        AppLog.Error("Task", "Unobserved task exception", e.Exception);
        e.SetObserved();
    }

    public static void Log(string msg)
    {
        if (msg.Contains("ERROR", StringComparison.OrdinalIgnoreCase) || msg.Contains("FAILED", StringComparison.OrdinalIgnoreCase))
            AppLog.Error("App", msg);
        else
            AppLog.Info("App", msg);
    }
}
