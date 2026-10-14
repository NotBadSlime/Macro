using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace SkirkFps;
public partial class MainWindow : Window
{
    private const int RestoreId = 9000;
    private readonly IpcController controller = new();
    private readonly ObservableCollection<FpsRule> rules = [];
    private readonly Dictionary<int, FpsRule> registered = [];
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly string configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SkirkFps", "settings.json");
    private FpsRule restoreRule = new() { VirtualKey = 0x7B, Modifiers = 6 };
    private FpsRule? capturing;
    private HotkeyRegistry? hotkeys;
    private HwndSource? source;
    private bool listening;

    public MainWindow()
    {
        InitializeComponent();
        try
        {
            var settings = File.Exists(configPath) ? Settings.ParseJson(File.ReadAllText(configPath)) : new Settings();
            if (settings.Rules is null || settings.Restore is null || settings.Rules.Count > 64 || settings.Rules.Any(r => r is null)) throw new IOException("配置格式无效");
            foreach (var rule in settings.Rules) rules.Add(rule);
            restoreRule = settings.Restore;
        }
        catch (Exception ex)
        {
            foreach (var rule in new Settings().Rules) rules.Add(rule);
            StatusText.Text = "配置未能读取，原文件仍保留：" + ex.Message;
        }
        RulesList.ItemsSource = rules;
        RestoreKeyButton.Content = restoreRule.HotkeyText;
        timer.Tick += (_, _) => RefreshState();
        timer.Start();
        RefreshState();
    }
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        source?.AddHook(WindowProc);
    }
    private nint WindowProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg != 0x0312 || !listening || capturing is not null) return 0;
        handled = true;
        try
        {
            var id = wParam.ToInt32();
            if (id == RestoreId) Restore();
            else if (registered.TryGetValue(id, out var rule) && rule.Enabled) Apply(rule);
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        return 0;
    }
    private int ParseFps(FpsRule rule)
    {
        return RuleValidation.ParseFps(rule);
    }
    private void Listen_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (listening) { StopListening(); StatusText.Text = "监听已停止，原帧率已恢复。"; return; }
            capturing = null;
            var enabled = RuleValidation.Validate(rules, restoreRule);
            hotkeys = new HotkeyRegistry(new WindowInteropHelper(this).Handle);
            try
            {
                registered.Clear();
                for (var i = 0; i < enabled.Count; i++) { hotkeys.Register(i + 1, enabled[i]); registered[i + 1] = enabled[i]; }
                hotkeys.Register(RestoreId, restoreRule);
            }
            catch { hotkeys.Dispose(); hotkeys = null; registered.Clear(); throw; }
            listening = true;
            ListenButton.Content = "停止监听";
            Save();
            StatusText.Text = "正在监听。快捷键将设为对应帧率；恢复快捷键可恢复原值。";
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private void StopListening()
    {
        hotkeys?.Dispose(); hotkeys = null; registered.Clear(); listening = false;
        ListenButton.Content = "开始监听";
        controller.Restore();
        RefreshState();
    }
    private void Apply(FpsRule rule)
    {
        controller.SetTarget(ParseFps(rule));
        StatusText.Text = $"已设置 {rule.Fps} FPS · 原帧率已备份";
        Save(); RefreshState();
    }
    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        try { if ((sender as FrameworkElement)?.DataContext is FpsRule rule) Apply(rule); }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private void Restore()
    {
        controller.Restore(); StatusText.Text = "已恢复原帧率。"; RefreshState();
    }
    private void Restore_Click(object sender, RoutedEventArgs e)
    { try { Restore(); } catch (Exception ex) { StatusText.Text = ex.Message; } }
    private void BeginCapture(FpsRule rule)
    {
        try { StopListening(); capturing = rule; Focus(); StatusText.Text = "按下单键或组合键，例如 F1、Ctrl + F1；Esc 取消。"; }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private void Capture_Click(object sender, RoutedEventArgs e)
    { if ((sender as FrameworkElement)?.DataContext is FpsRule rule) BeginCapture(rule); }
    private void RestoreKey_Click(object sender, RoutedEventArgs e) => BeginCapture(restoreRule);
    private void Capture_KeyDown(object sender, KeyEventArgs e)
    {
        if (capturing is null) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { capturing = null; StatusText.Text = "已取消快捷键录入。"; return; }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        var vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk is <= 0 or > 254) { StatusText.Text = "该键不支持，请换一个键。"; return; }
        var mods = Keyboard.Modifiers;
        uint native = ((mods & ModifierKeys.Alt) != 0 ? 1u : 0) | ((mods & ModifierKeys.Control) != 0 ? 2u : 0) | ((mods & ModifierKeys.Shift) != 0 ? 4u : 0) |
            (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin) ? 8u : 0);
        capturing.Modifiers = native; capturing.VirtualKey = vk;
        StatusText.Text = "已录入 " + capturing.HotkeyText + "，开启监听后生效。";
        capturing = null; RestoreKeyButton.Content = restoreRule.HotkeyText;
        try { Save(); } catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (rules.Count >= 64) { StatusText.Text = "最多支持 64 条规则。"; return; }
        try { StopListening(); var rule = new FpsRule { Enabled = false }; rules.Add(rule); Save(); BeginCapture(rule); }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        try { StopListening(); capturing = null; if ((sender as FrameworkElement)?.DataContext is FpsRule rule) rules.Remove(rule); Save(); }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private void Enabled_Click(object sender, RoutedEventArgs e)
    { try { StopListening(); Save(); StatusText.Text = "规则已更新，重新开启监听后生效。"; } catch (Exception ex) { StatusText.Text = ex.Message; } }
    private void Edit_Focus(object sender, KeyboardFocusChangedEventArgs e)
    { try { if (listening) { StopListening(); StatusText.Text = "编辑期间已暂停监听；修改后重新开启。"; } } catch (Exception ex) { StatusText.Text = ex.Message; } }
    private void Edit_LostFocus(object sender, RoutedEventArgs e)
    { try { Save(); } catch (Exception ex) { StatusText.Text = ex.Message; } }
    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        var json = JsonSerializer.Serialize(new Settings { Rules = rules.ToList(), Restore = restoreRule }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(configPath + ".tmp", json);
        File.Move(configPath + ".tmp", configPath, true);
    }
    private void RefreshState()
    {
        var current = controller.ReadCurrent();
        ConnectionText.Text = current is > 0 ? controller.Active ? "YSR 已连接 · 正在保持目标帧率" : "YSR 已连接 · 待命" : "等待 YSR · 先通过解帧器启动游戏";
        CurrentFpsText.Text = current is > 0 ? current.Value.ToString() : "—";
        if (controller.Error is { } error) StatusText.Text = error;
    }
    protected override void OnClosing(CancelEventArgs e)
    {
        try { StopListening(); Save(); }
        catch (Exception ex) { e.Cancel = true; MessageBox.Show(this, "退出未完成：" + ex.Message + "\n请恢复原帧率后再关闭。", "YSRExtend"); return; }
        timer.Stop(); source?.RemoveHook(WindowProc); controller.Dispose();
        base.OnClosing(e);
    }
    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ClickCount == 2) { WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; return; }
        try { DragMove(); } catch (InvalidOperationException) { }
    }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
