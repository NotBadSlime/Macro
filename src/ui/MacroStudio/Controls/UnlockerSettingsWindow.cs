using System.IO;
using System.Windows;
using System.Windows.Controls;
using MacroHid.Core;
using MacroHid.Runtime;
using Microsoft.Win32;
using MacroStudio.Services;

namespace MacroStudio.Controls;

public sealed class UnlockerSettingsWindow : Window
{
    private readonly TextBox fufu = new(), ky3 = new();
    private readonly CheckBox slow = new() { Content = "允许 KY3 原版 2 秒备用模式（只在无法使用补丁时选择）" };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
    public UnlockerSettingsWindow()
    {
        Title = "解帧器设置 · 测试版"; Width = 730; Height = 540; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "PageBackground");
        SetResourceReference(ForegroundProperty, "PrimaryText");
        var saved = UnlockerConfiguration.Load(); fufu.Text = saved.FufuDirectory ?? ""; ky3.Text = saved.Ky3Directory ?? ""; slow.IsChecked = saved.AllowSlowKy3;
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "选择启动器或插件所在文件夹。设置仅保存在本机，不随宏文件传播。", TextWrapping = TextWrapping.Wrap });
        AddDirectory(panel, "Fufu 插件目录", fufu, FpsBackend.Fufu);
        AddDirectory(panel, "KY3 插件目录", ky3, FpsBackend.Ky3);
        panel.Children.Add(slow);
        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        Button(buttons, "保存设置", Save);
        Button(buttons, "安装 KY3 20 毫秒补丁", () => Patch(false));
        Button(buttons, "恢复 KY3 原 DLL", () => Patch(true));
        Button(buttons, "处理异常退出恢复记录", Recover);
        panel.Children.Add(buttons);
        panel.Children.Add(new TextBlock { Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap,
            Text = "安装或恢复补丁前必须退出游戏；操作完成后重新启动游戏。补丁保留原 DLL 备份。未知版本不修改。\nFufu 原版配置通常需约 100–200 毫秒加载，再等待游戏回调；需进入游戏世界验证。KY3 20 毫秒是检查间隔，不代表实际帧率响应时间。\n两种配置通道都在等待解帧器应用，短时间切换可能被合并。请先完成本机游戏内实测。" });
        panel.Children.Add(status); Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
    private void AddDirectory(Panel parent, string label, TextBox box, FpsBackend backend)
    {
        parent.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 15, 0, 4) });
        box.MinWidth = 430; parent.Children.Add(box);
        var row = new WrapPanel();
        Button(row, "自动识别", () =>
        {
            var candidates = UnlockerConfiguration.DetectDirectories(backend);
            if (candidates.Length != 1) throw new InvalidOperationException($"发现 {candidates.Length} 个候选，请使用选择文件夹明确安装位置。");
            box.Text = candidates[0]; status.Text = "已识别目录，请保存并校验。";
        });
        Button(row, "选择文件夹", () =>
        {
            var picker = new OpenFolderDialog { Title = label };
            if (DialogOwnerService.ShowDialogSafe(picker, this) != true) return;
            var candidates = UnlockerConfiguration.FindDirectories(backend, new[] { picker.FolderName });
            if (candidates.Length != 1) throw new InvalidOperationException("未找到唯一的插件 DLL 和配置文件，请选择启动器根目录或插件目录。");
            box.Text = candidates[0];
        });
        parent.Children.Add(row);
    }
    private void Button(Panel panel, string label, Action action)
    {
        var button = new Button { Content = label, Margin = new Thickness(0, 5, 8, 0), Padding = new Thickness(9, 5, 9, 5) };
        button.Click += (_, _) => { try { action(); } catch (Exception ex) { status.Text = ex.Message; AppLog.Error("Unlocker settings", ex.Message, ex); } };
        panel.Children.Add(button);
    }
    private string? DirectoryFor(TextBox box, FpsBackend backend)
    {
        if (string.IsNullOrWhiteSpace(box.Text)) return null;
        var paths = UnlockerConfiguration.FindDirectories(backend, new[] { box.Text.Trim() });
        if (paths.Length != 1) throw new InvalidOperationException($"{backend} 路径缺少插件 DLL 或配置文件。");
        return paths[0];
    }
    private void Save()
    {
        string? fufuDirectory = DirectoryFor(fufu, FpsBackend.Fufu), ky3Directory = DirectoryFor(ky3, FpsBackend.Ky3);
        if (fufuDirectory is not null)
        {
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(fufuDirectory, UnlockerConfiguration.PluginName(FpsBackend.Fufu)))));
            if (hash != "58240C6B54A074AE6525C314B79A995ED6F614AB3626EF527EF9B88250DBBE6B") throw new InvalidOperationException("Fufu DLL 版本不受支持。");
            UnlockerConfiguration.ReadConfigurationTarget(FpsBackend.Fufu, fufuDirectory);
        }
        if (ky3Directory is not null && Ky3ReloadPatch.Inspect(Path.Combine(ky3Directory, UnlockerConfiguration.PluginName(FpsBackend.Ky3))) == Ky3PatchState.Unsupported)
            throw new InvalidOperationException("KY3 DLL 版本不受支持，拒绝套用补丁。");
        if (ky3Directory is not null) UnlockerConfiguration.ReadConfigurationTarget(FpsBackend.Ky3, ky3Directory);
        UnlockerConfiguration.Save(new(fufuDirectory, ky3Directory, slow.IsChecked == true));
        status.Text = "设置已保存。" + (ky3Directory is null ? "" : " KY3 状态：" + Ky3ReloadPatch.Inspect(Path.Combine(ky3Directory, UnlockerConfiguration.PluginName(FpsBackend.Ky3))));
    }
    private void Patch(bool restore)
    {
        string directory = DirectoryFor(ky3, FpsBackend.Ky3) ?? throw new InvalidOperationException("请先选择 KY3 目录。");
        Save();
        string dll = Path.Combine(directory, UnlockerConfiguration.PluginName(FpsBackend.Ky3));
        if (restore) Ky3ReloadPatch.Restore(dll); else Ky3ReloadPatch.Install(dll);
        Save(); status.Text = restore ? "原 DLL 已恢复，请重新启动游戏。" : "20 毫秒补丁已安装，原文件备份已保留。请重新启动游戏后实测。";
    }
    private void Recover()
    {
        var messages = UnlockerConfiguration.RecoverPending();
        status.Text = messages.Count == 0 ? "没有待恢复记录。" : string.Join("\n", messages);
    }
}
