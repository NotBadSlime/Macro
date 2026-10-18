using System.Windows;
using System.Windows.Controls;
using MacroHid.Core;
using MacroHid.Runtime;
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
        AffinityMaskBox.TextChanged += (_, _) => ValidateAffinityMask();
        StartLeadBox.Text = settings.StartLeadMicroseconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        StartLeadBox.TextChanged += (_, _) => ValidateAffinityMask();
        CompensateSendInputCheck.IsChecked = settings.CompensateSendInputCost;
        RawInputHotkeysCheck.IsChecked = settings.HotkeyInput == HotkeyInputMode.RawInput;
        avoidNoisyCores = settings.AvoidNoisyCores;
        foreach (var item in PrecisionModeBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), ToPrecisionModeText(settings.Precision), StringComparison.Ordinal))
            {
                PrecisionModeBox.SelectedItem = item;
                break;
            }
        }

        UpdateProfileDescription();
        ValidateAffinityMask();
        // Short screens: keep the dialog inside the work area; the cards scroll.
        BodyScroll.MaxHeight = Math.Max(320, SystemParameters.WorkArea.Height - 200);
    }

    private void UpdateProfileDescription()
    {
        var mode = SelectedPrecision();
        var policy = PlaybackPerformancePolicy.For(mode);
        var impact = mode switch {
            PrecisionMode.Balanced => L("PrecisionImpactLow"),
            PrecisionMode.UltraLowJitter => L("PrecisionImpactExtreme"),
            _ => L("PrecisionImpactBalanced") };
        ProfileDescription.Text = LocalizationService.Format("PrecisionProfileTargets",
            policy.TargetStartupMicroseconds, policy.TargetJitterMicroseconds, policy.FinalSpinMicroseconds) + "\n" + impact;
        // The core mask only applies to 极限; dim it for the other profiles instead of hiding it.
        var extreme = mode == PrecisionMode.UltraLowJitter;
        AffinityMaskBox.IsEnabled = extreme;
        ChooseCoresButton.IsEnabled = extreme;
    }

    private void ValidateAffinityMask()
    {
        var error = string.Empty;
        try { PlaybackAffinityMask.NormalizeOrThrow(AffinityMaskBox.Text); }
        catch (Exception ex) { error = ex.Message; }
        AffinityMaskErrorText.Text = error;
        FieldState.SetHasError(AffinityMaskBox, error.Length > 0);
        var leadOk = TryParseStartLead(out _);
        StartLeadErrorText.Text = leadOk ? string.Empty
            : LocalizationService.Format("StartLeadInvalid", RuntimePrecisionSettings.MaxStartLeadMicroseconds);
        FieldState.SetHasError(StartLeadBox, !leadOk);
        ConfirmButton.IsEnabled = error.Length == 0 && leadOk;
    }

    private bool TryParseStartLead(out int microseconds) =>
        int.TryParse(StartLeadBox.Text.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out microseconds)
        && microseconds <= RuntimePrecisionSettings.MaxStartLeadMicroseconds;

    public Func<string, string?>? PickCores { get; }

    public RuntimePrecisionSettings Settings { get; private set; } = RuntimePrecisionSettings.Default;

    private void ApplyLocalization()
    {
        Title = L("PrecisionControl");
        PrecisionModeLabelText.Text = L("PrecisionMode");
        HotkeySectionTitle.Text = L("PrecisionHotkeySection");
        CoreSectionTitle.Text = L("PrecisionCoreSection");
        AffinityMaskLabelText.Text = L("AffinityMask");
        AffinityMaskHelpText.Text = L("AffinityMaskHelp");
        PrecisionNextRunNoteText.Text = L("PrecisionNextRunNote");
        StartLeadLabelText.Text = L("StartLead");
        StartLeadUnitText.Text = L("Microseconds");
        StartLeadHelpText.Text = L("StartLeadHelp");
        CompensateSendInputCheck.Content = L("CompensateSendInput");
        CompensateSendInputHelpText.Text = L("CompensateSendInputHelp");
        RawInputHotkeysCheck.Content = L("RawInputHotkeys");
        RawInputHotkeysHelpText.Text = L("RawInputHotkeysHelp");
        LatencyCheckLabelText.Text = L("LatencyCheck");
        LatencyCheckHelpText.Text = L("LatencyCheckHelp");
        RunLatencyCheckButton.Content = L("LatencyCheckRun");
        LatencyCheckSummaryText.Text = SystemLatencyCheck.Last is { } report ? LatencyCheckDialog.Summarize(report) : L("LatencyCheckNotRun");
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

    private bool avoidNoisyCores;

    private void RunLatencyCheckButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new LatencyCheckDialog(avoidNoisyCores);
        DialogOwnerService.ShowDialogSafe(dialog, this);
        avoidNoisyCores = dialog.AvoidNoisyCores;
        if (SystemLatencyCheck.Last is { } report) LatencyCheckSummaryText.Text = LatencyCheckDialog.Summarize(report);
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
        ValidateAffinityMask();
        if (!ConfirmButton.IsEnabled)
        {
            AffinityMaskBox.Focus();
            return;
        }

        var mask = PlaybackAffinityMask.NormalizeOrThrow(AffinityMaskBox.Text);
        TryParseStartLead(out var lead);
        Settings = source with
        {
            Precision = SelectedPrecision(),
            AffinityMask = mask,
            StartLeadMicroseconds = lead,
            CompensateSendInputCost = CompensateSendInputCheck.IsChecked == true,
            HotkeyInput = RawInputHotkeysCheck.IsChecked == true ? HotkeyInputMode.RawInput : HotkeyInputMode.LowLevelHook,
            AvoidNoisyCores = avoidNoisyCores
        };
        DialogResult = true;
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
