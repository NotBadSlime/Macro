using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MacroStudio.Controls;
using MacroStudio.Services;

namespace MacroStudio;

/// <summary>
/// Development aid: <c>MacroStudio.exe --ui-snapshot &lt;dir&gt; [lang]</c> renders the settings dialogs and the themed
/// message dialog in the light and dark theme to PNG files, then exits. Nothing is saved, no hook or engine starts.
/// </summary>
internal static class UiSnapshot
{
    public static bool TryRun(App app, string[] args)
    {
        var at = Array.IndexOf(args, "--ui-snapshot");
        if (at < 0 || at + 1 >= args.Length) return false;
        var directory = Path.GetFullPath(args[at + 1]);
        LocalizationService.Initialize();
        if (at + 2 < args.Length) LocalizationService.SetLanguage(args[at + 2], persist: false);
        Directory.CreateDirectory(directory);
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            try
            {
                // Real data for the latency chart (no system change; quiet-core avoidance is not applied here).
                MacroHid.Runtime.SystemLatencyCheck.Run(60, applyQuietCores: false);
                foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
                {
                    ThemeService.ApplyTheme(theme);
                    var suffix = theme == AppTheme.Dark ? "dark" : "light";
                    var settings = RuntimePrecisionSettings.Default;
                    Render(new LatencyCheckDialog(), Path.Combine(directory, $"latency-{suffix}.png"));
                    Render(new MacroBasicControlDialog(settings), Path.Combine(directory, $"basic-{suffix}.png"));
                    var invalid = new MacroBasicControlDialog(settings);
                    invalid.ZeroGapHoldBox.Text = "-3";
                    Render(invalid, Path.Combine(directory, $"basic-invalid-{suffix}.png"));
                    Render(new AdvancedSettingsDialog(settings), Path.Combine(directory, $"advanced-{suffix}.png"));
                    Render(new PrecisionControlDialog(settings with { Precision = MacroHid.Core.PrecisionMode.UltraLowJitter }),
                        Path.Combine(directory, $"precision-{suffix}.png"));
                    Render(new PlaybackVersionDialog(settings), Path.Combine(directory, $"version-{suffix}.png"));
                    Render(new PlaybackVersionDialog(settings with { PlaybackVersion = "1.4.0" }),
                        Path.Combine(directory, $"version-legacy-{suffix}.png"));
                    Render(ThemedMessageDialog.Create(null, LocalizationService.Get("SnapshotSampleMessage"),
                            LocalizationService.Get("MacroBasicControl"), MessageBoxButton.OKCancel, MessageBoxImage.Warning),
                        Path.Combine(directory, $"message-{suffix}.png"));
                    var palette = new ActionPalettePanel();
                    palette.ApplyLocalization();
                    Render(new Window { Width = 400, Height = 760, Content = palette, WindowStyle = WindowStyle.None,
                            Background = (Brush)Application.Current.FindResource("InspectorBackground") },
                        Path.Combine(directory, $"palette-{suffix}.png"));
                }
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(directory, "error.txt"), ex.ToString());
            }
            finally
            {
                app.Shutdown();
            }
        });
        return true;
    }

    private static void Render(Window window, string path)
    {
        window.ShowActivated = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -20000;
        window.Top = -20000;
        window.Show();
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        var content = (FrameworkElement)window.Content;
        var dpi = VisualTreeHelper.GetDpi(window);
        var width = (int)Math.Ceiling(content.ActualWidth * dpi.DpiScaleX);
        var height = (int)Math.Ceiling(content.ActualHeight * dpi.DpiScaleY);
        var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var backdrop = new DrawingVisual();
        using (var context = backdrop.RenderOpen())
        {
            // A neutral page behind the rounded transparent dialog, like the app window would be.
            context.DrawRectangle((Brush)Application.Current.FindResource("PageBackground"), null,
                new Rect(0, 0, content.ActualWidth, content.ActualHeight));
        }
        bitmap.Render(backdrop);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(path)) encoder.Save(file);
        window.Close();
    }
}
