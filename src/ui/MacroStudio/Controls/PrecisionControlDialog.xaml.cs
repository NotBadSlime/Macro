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
