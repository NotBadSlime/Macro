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
        PrecisionGenerationBox.SelectionChanged += (_, _) => UpdateGenerationDescription();
        AffinityMaskBox.Text = settings.AffinityMask;
        foreach (var item in PrecisionModeBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), ToPrecisionModeText(settings.Precision), StringComparison.Ordinal))
            {
                PrecisionModeBox.SelectedItem = item;
                break;
            }
        }

        // Always lands on an item: an unknown or missing value is already the latest after Normalize.
        var generationTag = ToGenerationTag(PrecisionGenerations.Normalize(settings.Generation));
        PrecisionGenerationBox.SelectedItem = PrecisionGenerationBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), generationTag, StringComparison.Ordinal))
            ?? PrecisionGenerationBox.Items[0];
        UpdateGenerationDescription();
    }

    private void UpdateGenerationDescription()
    {
        PrecisionGenerationDescription.Text = SelectedGeneration() switch
        {
            PrecisionGeneration.V2_0_2 => L("PrecisionGenerationDesc202"),
            PrecisionGeneration.V2_0 => L("PrecisionGenerationDesc20"),
            PrecisionGeneration.V1_6 => L("PrecisionGenerationDesc16"),
            _ => L("PrecisionGenerationDescLatest")
        };
    }

    private PrecisionGeneration SelectedGeneration()
    {
        return (PrecisionGenerationBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
        {
            "v2_0_2" => PrecisionGeneration.V2_0_2,
            "v2_0" => PrecisionGeneration.V2_0,
            "v1_6" => PrecisionGeneration.V1_6,
            _ => PrecisionGeneration.Latest
        };
    }

    private static string ToGenerationTag(PrecisionGeneration generation) => generation switch
    {
        PrecisionGeneration.V2_0_2 => "v2_0_2",
        PrecisionGeneration.V2_0 => "v2_0",
        PrecisionGeneration.V1_6 => "v1_6",
        _ => "latest"
    };

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
        PrecisionGenerationLabelText.Text = L("PrecisionGeneration");
        PrecisionGenerationHelpText.Text = L("PrecisionGenerationHelp");
        PrecisionGenerationBox.ToolTip = L("PrecisionGenerationHelp");
        foreach (var item in PrecisionGenerationBox.Items.OfType<ComboBoxItem>())
        {
            item.Content = item.Tag?.ToString() switch
            {
                "latest" => L("PrecisionGenerationLatest"),
                "v2_0_2" => L("PrecisionGenerationV202"),
                "v2_0" => L("PrecisionGenerationV20"),
                "v1_6" => L("PrecisionGenerationV16"),
                _ => item.Content
            };
        }
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
                Generation = SelectedGeneration(),
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
