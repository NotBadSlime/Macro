using System.Windows;
using System.Windows.Controls;
using MacroHid.Core;
using MacroStudio.Services;

namespace MacroStudio.Controls;

public partial class PrecisionControlDialog : Window
{
    private readonly RuntimePrecisionSettings source;

    public PrecisionControlDialog(RuntimePrecisionSettings settings, Func<string, string?>? pickCores = null)
    {
        InitializeComponent();
        ThemedDialogChrome.Apply(this);
        source = settings;
        PickCores = pickCores;
        ApplyLocalization();
        PrecisionModeBox.SelectionChanged += (_, _) => UpdateProfileDescription();
        AffinityMaskBox.Text = settings.AffinityMask;
        foreach (var item in PrecisionModeBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), ToPrecisionModeText(settings.Precision), StringComparison.Ordinal))
            {
                PrecisionModeBox.SelectedItem = item;
                break;
            }
        }
    }

    private void UpdateProfileDescription()
    {
        var mode = SelectedPrecision();
        var policy = PlaybackPerformancePolicy.For(mode);
        var impact = mode switch {
            PrecisionMode.Balanced => "CPU 影响最低；适合 CPU 紧张、普通自动化。托管预编译。",
            PrecisionMode.UltraLowJitter => "CPU 影响最高；适合 1–2ms 节奏。两个原生常驻线程共享辅助抢占。",
            _ => "CPU 影响中等；适合大多数游戏与混合宏。原生预编译。" };
        ProfileDescription.Text = $"响应目标：首次提交 P99 ≤ {policy.TargetStartupMicroseconds}µs；迟到 P99.9 ≤ {policy.TargetJitterMicroseconds}µs。\n{impact}\n最终自旋窗口：{policy.FinalSpinMicroseconds}µs；空闲时事件等待。";
    }

    public Func<string, string?>? PickCores { get; }

    public RuntimePrecisionSettings Settings { get; private set; } = RuntimePrecisionSettings.Default;

    private void ApplyLocalization()
    {
        Title = L("PrecisionControl");
        PrecisionModeLabelText.Text = L("PrecisionMode");
        AffinityMaskLabelText.Text = L("AffinityMask");
        AffinityMaskHelpText.Text = L("AffinityMaskHelp");
        AffinityMaskBox.ToolTip = L("AffinityMaskHelp");
        ChooseCoresButton.Content = L("ChooseCores");
        CancelButton.Content = L("Cancel");
        ConfirmButton.Content = L("Confirm");
        foreach (var item in PrecisionModeBox.Items.OfType<ComboBoxItem>())
        {
            item.Content = item.Tag?.ToString() switch
            {
                "balanced" => L("PrecisionBalanced"),
                "extremeDuringPlayback" => L("PrecisionExtremeDuringPlayback"),
                "ultraLowJitter" => L("PrecisionUltraLowJitter"),
                _ => item.Content
            };
        }
    }

    private void ChooseCoresButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = PickCores?.Invoke(AffinityMaskBox.Text.Trim());
        if (selected is not null)
        {
            AffinityMaskBox.Text = selected;
        }
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var mask = PlaybackAffinityMask.NormalizeOrThrow(AffinityMaskBox.Text);
            Settings = source with
            {
                Precision = SelectedPrecision(),
                AffinityMask = mask
            };
            DialogResult = true;
        }
        catch (Exception ex)
        {
            DialogOwnerService.MessageBoxSafe(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private PrecisionMode SelectedPrecision()
    {
        var value = (PrecisionModeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "extremeDuringPlayback";
        return value switch
        {
            "balanced" => PrecisionMode.Balanced,
            "ultraLowJitter" => PrecisionMode.UltraLowJitter,
            _ => PrecisionMode.ExtremeDuringPlayback
        };
    }

    private static string ToPrecisionModeText(PrecisionMode mode) => mode switch
    {
        PrecisionMode.Balanced => "balanced",
        PrecisionMode.UltraLowJitter => "ultraLowJitter",
        _ => "extremeDuringPlayback"
    };

    private static string L(string key) => LocalizationService.Get(key);
}
