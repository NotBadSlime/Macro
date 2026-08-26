using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MacroHid.Core;
using MacroStudio.Services;

namespace MacroStudio.Controls;

public partial class ActionAppearanceDialog : Window
{
    public ActionAppearanceDialog()
    {
        InitializeComponent();
        ThemedDialogChrome.Apply(this);
        ApplyLocalization();
        ActionTypeBox.ItemsSource = ActionAppearanceService.EditableKinds
            .Select(kind => new ActionKindOption(kind, GetKindName(kind)))
            .ToList();
        ActionTypeBox.SelectedIndex = 0;
    }

    private MacroActionTemplateKind SelectedKind =>
        ActionTypeBox.SelectedItem is ActionKindOption option ? option.Kind : MacroActionTemplateKind.Delay;

    private void ApplyLocalization()
    {
        Title = L("ActionColorSettingsTitle");
        ActionTypeLabel.Text = L("ActionColorType");
        TextColorLabel.Text = L("ActionTextColor");
        IconColorLabel.Text = L("ActionIconColor");
        BackgroundColorLabel.Text = L("ActionBackgroundColor");
        PreviewText.Text = L("ActionColorPreview");
        ResetButton.Content = L("RestoreDefaults");
        CloseButton.Content = L("Done");
    }

    private void ActionTypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshPreview();

    private void TextColorButton_Click(object sender, RoutedEventArgs e) => PickColor(ActionAppearanceRole.Text);

    private void IconColorButton_Click(object sender, RoutedEventArgs e) => PickColor(ActionAppearanceRole.Icon);

    private void BackgroundColorButton_Click(object sender, RoutedEventArgs e) => PickColor(ActionAppearanceRole.Background);

    private void PickColor(ActionAppearanceRole role)
    {
        var dialog = new RgbColorPickerDialog(ActionAppearanceService.GetResolvedColor(SelectedKind, role))
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        ActionAppearanceService.SetColor(SelectedKind, role, dialog.SelectedColor);
        RefreshPreview();
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        ActionAppearanceService.Reset(SelectedKind);
        RefreshPreview();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void RefreshPreview()
    {
        if (ActionTypeBox.SelectedItem is not ActionKindOption)
        {
            return;
        }

        var text = ActionAppearanceService.GetResolvedColor(SelectedKind, ActionAppearanceRole.Text);
        var icon = ActionAppearanceService.GetResolvedColor(SelectedKind, ActionAppearanceRole.Icon);
        var background = ActionAppearanceService.GetResolvedColor(SelectedKind, ActionAppearanceRole.Background);
        TextColorSwatch.Background = new SolidColorBrush(text);
        IconColorSwatch.Background = new SolidColorBrush(icon);
        BackgroundColorSwatch.Background = new SolidColorBrush(background);
        TextColorHex.Text = ToHex(text);
        IconColorHex.Text = ToHex(icon);
        BackgroundColorHex.Text = ToHex(background);
        PreviewText.Foreground = new SolidColorBrush(text);
        PreviewIcon.Foreground = new SolidColorBrush(icon);
        PreviewIconBadge.Background = new SolidColorBrush(Color.FromArgb(38, icon.R, icon.G, icon.B));
        PreviewIconBadge.BorderBrush = new SolidColorBrush(Color.FromArgb(100, icon.R, icon.G, icon.B));
        PreviewCard.Background = new SolidColorBrush(background);
    }

    private static string GetKindName(MacroActionTemplateKind kind)
    {
        return kind switch
        {
            MacroActionTemplateKind.StopCurrent => L("AddStopCurrent"),
            MacroActionTemplateKind.StopIteration => L("AddStopIteration"),
            MacroActionTemplateKind.StopAll => L("AddStopAll"),
            _ => L($"Template{kind}")
        };
    }

    private static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static string L(string key) => LocalizationService.Get(key);

    private sealed record ActionKindOption(MacroActionTemplateKind Kind, string Name);
}
