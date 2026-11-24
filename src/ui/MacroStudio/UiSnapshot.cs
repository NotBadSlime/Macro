using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MacroHid.Core;
using MacroStudio.Controls;
using MacroStudio.Services;

namespace MacroStudio;

/// <summary>
/// Development aid: <c>MacroStudio.exe --ui-snapshot &lt;dir&gt; [lang]</c> renders the settings dialogs and the themed
/// message dialog, and the workspace in both themes. The workspace uses a temporary example library and no hooks.
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
                var studioLibrary = CreateStudioLibrary(directory);
                // Real data for the latency chart (no system change; quiet-core avoidance is not applied here).
                MacroHid.Runtime.SystemLatencyCheck.Run(60, applyQuietCores: false);
                foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
                {
                    ThemeService.ApplyTheme(theme);
                    var suffix = theme == AppTheme.Dark ? "dark" : "light";
                    Render(new MainWindow(studioLibrary, isWorkspacePreview: true) { Width = 1380, Height = 846 },
                        Path.Combine(directory, $"workspace-{suffix}.png"));
                    if (theme == AppTheme.Dark)
                    {
                        Render(new MainWindow(studioLibrary, isWorkspacePreview: true) { Width = 1040, Height = 660 },
                            Path.Combine(directory, "workspace-compact-dark.png"), CheckCompactWorkspace);
                        Render(new MainWindow(studioLibrary, isWorkspacePreview: true) { Width = 1280, Height = 790 },
                            Path.Combine(directory, "command-search-dark.png"), DemonstrateCommandSearch);
                    }
                    var settings = RuntimePrecisionSettings.Default;
                    Render(new LatencyCheckDialog(), Path.Combine(directory, $"latency-{suffix}.png"));
                    Render(new MacroBasicControlDialog(settings), Path.Combine(directory, $"basic-{suffix}.png"));
                    var invalid = new MacroBasicControlDialog(settings);
                    invalid.ZeroGapHoldBox.Text = "-3";
                    Render(invalid, Path.Combine(directory, $"basic-invalid-{suffix}.png"));
                    Render(new AdvancedSettingsDialog(settings), Path.Combine(directory, $"advanced-{suffix}.png"));
                    Render(new GlobalCommandDialog(), Path.Combine(directory, $"global-command-{suffix}.png"));
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
                    var playback = new PlaybackPanel();
                    playback.ApplyLocalization();
                    playback.SetPlaybackControls(MacroHid.Core.PlaybackSettings.Default with
                    {
                        Mode = MacroHid.Core.PlaybackMode.ToggleLoop,
                        RunAsDaemon = true
                    });
                    Render(new Window { Width = 400, Height = 400, Content = playback, WindowStyle = WindowStyle.None,
                            Background = (Brush)Application.Current.FindResource("InspectorBackground") },
                        Path.Combine(directory, $"playback-{suffix}.png"));
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

    private static MacroLibraryStore CreateStudioLibrary(string directory)
    {
        var store = new MacroLibraryStore(Path.Combine(directory, ".workspace-example"));
        if (store.Load().Items.Count > 0) return store;
        var simplified = LocalizationService.CurrentCulture.Name == "zh-CN";
        var traditional = LocalizationService.CurrentCulture.Name == "zh-TW";
        var document = McrxParser.Parse("""
            { "version": 1, "name": "Capture workspace", "steps": [
                { "type": "key.tap", "key": "PrintScreen", "holdMs": 35 },
                { "type": "wait", "ms": 240 },
                { "type": "key.tap", "key": "V", "modifiers": ["LeftCtrl"], "holdMs": 32 },
                { "type": "wait", "ms": 120 },
                { "type": "key.tap", "key": "S", "modifiers": ["LeftCtrl"], "holdMs": 32 }
            ] }
            """);
        var first = store.CreateMacro(document with
        {
            Name = simplified ? "工作区截图" : traditional ? "工作區截圖" : "Capture workspace"
        });
        store.CreateMacro(simplified ? "窗口排列" : traditional ? "視窗排列" : "Arrange windows");
        store.CreateMacro(simplified ? "素材归档" : traditional ? "素材封存" : "Archive assets");
        store.SetSelected(first.Id);
        return store;
    }

    private static void CheckCompactWorkspace(Window window)
    {
        var main = (MainWindow)window;
        main.PlaybackRightToolButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        main.UpdateLayout();
        var rightDock = main.RightDockContent;
        var end = rightDock.TranslatePoint(new Point(rightDock.ActualWidth, 0), main.AppShellRoot);
        if (end.X > main.AppShellRoot.ActualWidth - main.DockHost.Margin.Right + 1 || rightDock.ActualWidth <= 0)
        {
            throw new InvalidOperationException("The inspector is clipped in the compact workspace.");
        }
    }

    private static void DemonstrateCommandSearch(Window window)
    {
        var main = (MainWindow)window;
        main.OpenCommandPalette();
        var query = (TextBox)main.CommandPalette.FindName("QueryBox");
        query.Text = "json";
        var results = (ListBox)main.CommandPalette.FindName("CommandList");
        if (results.Items.Count != 1)
        {
            throw new InvalidOperationException("Command search did not find the JSON editor.");
        }
        query.Clear();
    }

    private static void Render(Window window, string path, Action<Window>? prepare = null)
    {
        window.ShowActivated = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -20000;
        window.Top = -20000;
        window.Show();
        window.UpdateLayout();
        prepare?.Invoke(window);
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
