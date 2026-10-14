using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkirkFps;

internal static class Program
{
    private static void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS " + name); }
    private static void Reject(Action action, string name) { try { action(); } catch (InvalidOperationException) { Console.WriteLine("PASS " + name); return; } throw new Exception(name); }
    [STAThread] private static int Main(string[] args)
    {
        try
        {
            var settings = new Settings();
            Check(settings.Rules.Select(r => r.Fps).SequenceEqual(new[] { "117", "217", "384" }), "default FPS list");
            Check(settings.Rules.Select(r => r.VirtualKey).SequenceEqual(new[] { 0x70, 0x71, 0x72 }), "F1 F2 F3 defaults");
            Check(RuleValidation.Validate(settings.Rules, settings.Restore).Count == 3, "valid bindings");
            var combo = new FpsRule { VirtualKey = 0x70, Modifiers = 3 };
            Check(combo.HotkeyText == "Ctrl + Alt + F1", "modifier combination display");
            var parsed = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings))!;
            Check(parsed.Rules.Count == 3 && parsed.Restore.Modifiers == 6, "configuration round trip");
            var commented = Settings.ParseJson("""
                // 快捷键配置
                {
                  "Rules": [
                    { "VirtualKey": 112, "Fps": "117", "Enabled": true }, // F1
                    /* 高帧率 */ { "VirtualKey": 113, "Fps": "217", "Enabled": true },
                    // { "VirtualKey": 114, "Fps": "384" }
                  ],
                  "Restore": { "VirtualKey": 123, "Modifiers": 6 },
                }
                """);
            Check(commented.Rules.Count == 2 && commented.Rules[1].Fps == "217", "line and block comments skipped");
            Check(commented.Restore.Modifiers == 6, "trailing commas accepted with comments");
            Check(Settings.ParseJson("""{"Rules":[{"Fps":"https://example.com/a//b"}]}""").Rules[0].Fps == "https://example.com/a//b", "slashes inside strings preserved");
            try { Settings.ParseJson("{\n // comment\n \"Rules\": [broken] }"); throw new Exception("invalid JSON accepted"); }
            catch (JsonException ex) { Check(ex.LineNumber == 2, "malformed JSON still reports original line"); }
            settings.Rules.Add(new() { VirtualKey = 0x70, Fps = "90" });
            Reject(() => RuleValidation.Validate(settings.Rules, settings.Restore), "duplicate key rejected");
            settings.Rules[^1].Enabled = false;
            Check(RuleValidation.Validate(settings.Rules, settings.Restore).Count == 3, "disabled duplicate ignored");
            Reject(() => RuleValidation.Validate([new() { VirtualKey = settings.Restore.VirtualKey, Modifiers = settings.Restore.Modifiers }], settings.Restore), "restore binding conflict rejected");
            foreach (var invalid in new[] { "0", "1001", "117.5", "abc" }) Reject(() => RuleValidation.ParseFps(new() { Fps = invalid }), "invalid FPS " + invalid);
            Check(RuleValidation.ParseFps(new() { Fps = "1" }) == 1 && RuleValidation.ParseFps(new() { Fps = "1000" }) == 1000, "FPS boundaries");
            using (var missing = new IpcController("SkirkFPS-Missing-" + Guid.NewGuid()))
            { Check(missing.ReadCurrent() is null, "missing IPC status"); Reject(() => missing.SetTarget(117), "missing IPC actionable failure"); }
            var name = "SkirkFPS-Test-" + Guid.NewGuid();
            using var mapping = MemoryMappedFile.CreateNew(name, 32);
            using var view = mapping.CreateViewAccessor();
            view.Write(0, 123456); view.Write(8, 151);
            using (var ipc = new IpcController(name))
            {
                Check(ipc.ReadCurrent() == 151, "reads IPC offset 8");
                ipc.SetTarget(117); Check(view.ReadInt32(8) == 117, "apply target");
                ipc.SetTarget(217); Check(view.ReadInt32(8) == 217, "switch target");
                view.Write(8, 151); Check(SpinWait.SpinUntil(() => view.ReadInt32(8) == 217, 2000), "target refreshed every 50ms");
                ipc.Restore(); Check(view.ReadInt32(8) == 151, "first baseline retained");
                ipc.Restore(); Check(view.ReadInt32(8) == 151, "restore is idempotent");
                Check(view.ReadInt32(0) == 123456, "other IPC fields untouched");
                ipc.SetTarget(384);
            }
            Check(view.ReadInt32(8) == 151, "dispose restores original FPS");
            if (args.Length > 0)
            {
                var app = new SkirkFps.App(); app.InitializeComponent();
                var tooltip = new System.Windows.Controls.ToolTip { Content = "1–1000 的整数", Style = (Style)app.FindResource(typeof(System.Windows.Controls.ToolTip)) };
                tooltip.Measure(new Size(340, 100)); tooltip.Arrange(new Rect(tooltip.DesiredSize)); tooltip.UpdateLayout();
                Check(((SolidColorBrush)tooltip.Background).Color == Color.FromRgb(0x29, 0x26, 0x30), "tooltip dark background");
                Check(((SolidColorBrush)tooltip.Foreground).Color == Color.FromRgb(0xF0, 0xEE, 0xF8), "tooltip readable foreground");
                var tipBitmap = new RenderTargetBitmap((int)Math.Ceiling(tooltip.ActualWidth), (int)Math.Ceiling(tooltip.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                tipBitmap.Render(tooltip);
                var tipEncoder = new PngBitmapEncoder(); tipEncoder.Frames.Add(BitmapFrame.Create(tipBitmap));
                using (var tipFile = File.Create(args[0] + ".tooltip.png")) tipEncoder.Save(tipFile);
                var window = new MainWindow();
                window.Width = 820; window.Height = 720;
                var content = (FrameworkElement)window.Content;
                content.Measure(new Size(820, 720)); content.Arrange(new Rect(0, 0, 820, 720)); content.UpdateLayout();
                content.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                content.UpdateLayout();
                var bitmap = new RenderTargetBitmap(820, 720, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(content);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(args[0]); encoder.Save(file);
                Console.WriteLine("UI preview: " + args[0]);
            }
            Console.WriteLine("ALL SKIRK FPS TESTS PASSED"); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
