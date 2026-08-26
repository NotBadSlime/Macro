using System.Windows;
using System.Windows.Controls;
using MacroHid.Converter;
using MacroStudio.Services;

namespace MacroStudio.Controls;

public enum ExportPackagingMode
{
    TwoFolders,
    Zip
}

public sealed class ExportWizardResult
{
    public MacroConversionFormat Format { get; init; }
    public ExportPackagingMode? Packaging { get; init; }
}

public partial class ExportWizardDialog : Window
{
    private readonly bool showPackagingStep;
    private int step;

    public ExportWizardResult? Result { get; private set; }

    public ExportWizardDialog(bool showPackagingStep)
    {
        InitializeComponent();
        ThemedDialogChrome.Apply(this);
        this.showPackagingStep = showPackagingStep;
        ApplyLocalization();
        PopulateFormats();
        ShowStep(0);
    }

    private void ApplyLocalization()
    {
        Title = L("ExportWizardTitle");
        FormatStepLabel.Text = L("ExportFormat");
        PackagingStepLabel.Text = L("ExportPackagingTitle");
        TwoFoldersRadio.Content = L("ExportPackagingTwoFolders");
        ZipRadio.Content = L("ExportPackagingZip");
        WeakFormatWarningText.Text = L("ExportWeakFormatWarning");
        CancelButton.Content = L("Cancel");
        UpdatePrimaryButtonText();
    }

    private void PopulateFormats()
    {
        var formats = MacroConversionService.GetFormats()
            .Where(item => item.CanExport)
            .ToList();
        FormatList.ItemsSource = formats;

        var preferred = formats.FirstOrDefault(item => item.Format == MacroConversionFormat.MacroHidMcrx)
            ?? formats.FirstOrDefault();
        if (preferred is not null)
        {
            FormatList.SelectedItem = preferred;
        }

        UpdateWeakFormatWarning();
    }

    private void ShowStep(int nextStep)
    {
        step = nextStep;
        FormatPanel.Visibility = step == 0 ? Visibility.Visible : Visibility.Collapsed;
        PackagingPanel.Visibility = step == 1 && showPackagingStep
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdatePrimaryButtonText();
    }

    private void UpdatePrimaryButtonText()
    {
        var needsNext = showPackagingStep && step == 0;
        PrimaryButton.Content = needsNext ? L("ExportNext") : L("ExportMacro");
    }

    private void FormatList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateWeakFormatWarning();

    private void UpdateWeakFormatWarning()
    {
        var format = GetSelectedFormat();
        WeakFormatWarningText.Visibility = format != MacroConversionFormat.MacroHidMcrx
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private MacroConversionFormat GetSelectedFormat()
    {
        return FormatList.SelectedItem is MacroFormatInfo info
            ? info.Format
            : MacroConversionFormat.MacroHidMcrx;
    }

    private ExportPackagingMode GetSelectedPackaging()
    {
        return ZipRadio.IsChecked == true
            ? ExportPackagingMode.Zip
            : ExportPackagingMode.TwoFolders;
    }

    private void PrimaryButton_Click(object sender, RoutedEventArgs e)
    {
        if (showPackagingStep && step == 0)
        {
            ShowStep(1);
            return;
        }

        Result = new ExportWizardResult
        {
            Format = GetSelectedFormat(),
            Packaging = showPackagingStep ? GetSelectedPackaging() : null
        };
        DialogResult = true;
    }

    private static string L(string key) => LocalizationService.Get(key);
}
