using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using MacroHid.Runtime;
using MacroStudio.Services;

namespace MacroStudio.Controls;

/// <summary>高级设置: the no-GC playback rules (<see cref="PlaybackGcOptions"/>).</summary>
public partial class AdvancedSettingsDialog : Window
{
    private readonly RuntimePrecisionSettings source;

    public AdvancedSettingsDialog(RuntimePrecisionSettings settings)
    {
        InitializeComponent();
        DialogBody.MaxHeight = Math.Max(420, SystemParameters.WorkArea.Height - 90);
        ThemedDialogChrome.Apply(this);
        source = settings;
        ApplyLocalization();
        Show(settings.NoGc);
        foreach (var box in new[] { MaxSecondsBox, BudgetBox, SlackBox, PercentBox })
            box.TextChanged += (_, _) => Validate(out _);
        Validate(out _);
    }

    public RuntimePrecisionSettings Settings { get; private set; } = RuntimePrecisionSettings.Default;

    private void Show(PlaybackGcOptions o)
    {
        MaxSecondsBox.Text = o.MaxIterationSeconds.ToString(CultureInfo.InvariantCulture);
        BudgetBox.Text = o.BudgetMegabytes.ToString(CultureInfo.InvariantCulture);
        SlackBox.Text = o.RenewSlackMilliseconds.ToString("0.###", CultureInfo.InvariantCulture);
        PercentBox.Text = o.RenewAtBudgetPercent.ToString(CultureInfo.InvariantCulture);
        RenewAfterLossCheck.IsChecked = o.RenewAfterLoss;
        RenewWhileConditionsCheck.IsChecked = o.RenewWhileConditionsRun;
    }

    /// <summary>Checks every field as the user types; marks the box and disables 确定 while one is invalid.</summary>
    private bool Validate(out PlaybackGcOptions options)
    {
        var secondsOk = TryInt(MaxSecondsBox, 0, PlaybackGcOptions.UpperMaxIterationSeconds, out var seconds);
        var budgetOk = TryInt(BudgetBox, PlaybackGcOptions.MinBudgetMegabytes, PlaybackGcOptions.MaxBudgetMegabytes, out var budget);
        var slackOk = double.TryParse(SlackBox.Text.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var slack)
                      && slack >= 0 && slack <= PlaybackGcOptions.MaxRenewSlackMilliseconds
                      && Math.Round(slack, 3) == slack;
        var percentOk = TryInt(PercentBox, 1, 100, out var percent);
        Mark(MaxSecondsBox, MaxSecondsErrorText, secondsOk, LocalizationService.Format("RangeIntInvalid", 0, PlaybackGcOptions.UpperMaxIterationSeconds));
        Mark(BudgetBox, BudgetErrorText, budgetOk, LocalizationService.Format("RangeIntInvalid", PlaybackGcOptions.MinBudgetMegabytes, PlaybackGcOptions.MaxBudgetMegabytes));
        Mark(SlackBox, SlackErrorText, slackOk, LocalizationService.Format("HoldMillisecondsInvalid", PlaybackGcOptions.MaxRenewSlackMilliseconds));
        Mark(PercentBox, PercentErrorText, percentOk, LocalizationService.Format("RangeIntInvalid", 1, 100));
        var ok = secondsOk && budgetOk && slackOk && percentOk;
        ConfirmButton.IsEnabled = ok;
        options = new PlaybackGcOptions(seconds, budget, slack, percent,
            RenewAfterLossCheck.IsChecked == true, RenewWhileConditionsCheck.IsChecked == true);
        return ok;
    }

    private static bool TryInt(TextBox box, int min, int max, out int value) =>
        int.TryParse(box.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= min && value <= max;

    private static void Mark(TextBox box, TextBlock error, bool ok, string message)
    {
        error.Text = ok ? string.Empty : message;
        FieldState.SetHasError(box, !ok);
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e) => Show(PlaybackGcOptions.Default);

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        if (!Validate(out var options))
        {
            foreach (var box in new[] { MaxSecondsBox, BudgetBox, SlackBox, PercentBox })
                if (FieldState.GetHasError(box)) { box.Focus(); break; }
            return;
        }
        Settings = source with { NoGcPlayback = options.Normalize() };
        DialogResult = true;
    }

    private void ApplyLocalization()
    {
        Title = L("AdvancedSettings");
        NoGcSectionTitle.Text = L("AdvancedSectionNoGc");
        NoGcIntroText.Text = L("NoGcIntro");
        MaxSecondsLabelText.Text = L("NoGcMaxIterationSeconds");
        MaxSecondsUnitText.Text = L("Seconds");
        MaxSecondsHelpText.Text = L("NoGcMaxIterationSecondsHelp");
        BudgetLabelText.Text = L("NoGcBudget");
        BudgetHelpText.Text = L("NoGcBudgetHelp");
        RenewSectionTitle.Text = L("AdvancedSectionNoGcRenew");
        SlackLabelText.Text = L("NoGcRenewSlack");
        SlackUnitText.Text = L("Milliseconds");
        PercentLabelText.Text = L("NoGcRenewPercent");
        RenewAfterLossCheck.Content = L("NoGcRenewAfterLoss");
        RenewWhileConditionsCheck.Content = L("NoGcRenewWhileConditions");
        RenewHelpText.Text = L("NoGcRenewHelp");
        ResetButton.Content = L("RestoreDefaults");
        CancelButton.Content = L("Cancel");
        ConfirmButton.Content = L("Confirm");
    }

    private static string L(string key) => LocalizationService.Get(key);
}
