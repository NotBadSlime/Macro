using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MacroHid.Runtime;
using MacroStudio.Services;

namespace MacroStudio.Controls;

public partial class LatencyCheckDialog : Window
{
    private const double BarAreaHeight = 112;
    private bool running;

    /// <summary>The "avoid noisy cores" choice; the owner persists it.</summary>
    public bool AvoidNoisyCores => AvoidNoisyCheck.IsChecked == true;

    public LatencyCheckDialog(bool avoidNoisyCores = true)
    {
        InitializeComponent();
        ThemedDialogChrome.Apply(this);
        Title = L("LatencyCheck");
        IntroText.Text = L("LatencyCheckIntro");
        WindowLabelText.Text = L("LatencyCheckWindow");
        StartButton.Content = L("LatencyCheckStart");
        AvoidNoisyCheck.Content = L("LatencyCheckAvoidNoisy");
        AvoidNoisyHelpText.Text = L("LatencyCheckAvoidNoisyHelp");
        CloseButton.Content = L("CloseWindow");
        AvoidNoisyCheck.IsChecked = avoidNoisyCores;
        AvoidNoisyCheck.Checked += (_, _) => ApplyAvoidance();
        AvoidNoisyCheck.Unchecked += (_, _) => ApplyAvoidance();
        if (SystemLatencyCheck.Last is { } report) Show(report);
        else SummaryText.Text = L("LatencyCheckNotRun");
    }

    private void ApplyAvoidance()
    {
        var mask = AvoidNoisyCheck.IsChecked == true ? SystemLatencyCheck.StableQuietMask : 0;
        SystemLatencyCheck.ApplyQuietCores(mask);
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (running) return;
        running = true;
        StartButton.IsEnabled = false;
        WindowBox.IsEnabled = false;
        var windowMs = int.Parse((WindowBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "200");
        var apply = AvoidNoisyCheck.IsChecked == true;
        SummaryText.Text = L("LatencyCheckRunning");
        AdviceText.Text = string.Empty;
        Progress.Value = 0;
        try
        {
            var report = await Task.Run(() => SystemLatencyCheck.Run(windowMs,
                (_, completed, total, value) =>
                {
                    if (value >= 0) Dispatcher.BeginInvoke(() => Progress.Value = (double)completed / Math.Max(1, total));
                }, apply));
            if (report is null)
            {
                SummaryText.Text = L("LatencyCheckUnavailable");
                return;
            }
            Show(report);
        }
        finally
        {
            Progress.Value = 1;
            running = false;
            StartButton.IsEnabled = true;
            WindowBox.IsEnabled = true;
        }
    }

    private void Show(SystemLatencyReport report)
    {
        var danger = (Brush)FindResource("Danger");
        var accent = (Brush)FindResource("Accent");
        var worst = Math.Max(1, report.Cores.Count == 0 ? 1 : report.Cores.Max(core => core.MaxStallUs));
        // Log scale from 10 us to the worst stall, so 50 us and 9 ms are both readable on one chart.
        double Height(long us)
        {
            var low = Math.Log10(10);
            var high = Math.Max(Math.Log10(worst), low + 1);
            return Math.Clamp((Math.Log10(Math.Max(10, us)) - low) / (high - low), 0.03, 1) * BarAreaHeight;
        }
        CoreBars.ItemsSource = report.Cores.Select(core => new
        {
            BarHeight = Height(core.MaxStallUs),
            Fill = core.Noisy ? danger : accent,
            Label = core.Processor.ToString(),
            Tip = LocalizationService.Format("LatencyCheckCoreTip", core.Processor, core.PhysicalCore, core.MaxStallUs,
                core.StallsOver50Us, core.StallsOver200Us, core.Noisy ? L("LatencyCheckNoisy") : L("LatencyCheckQuiet"))
        }).ToList();
        ScaleText.Text = LocalizationService.Format("LatencyCheckScale", worst, report.WindowMs);
        SummaryText.Text = Summarize(report);
        AdviceText.Text = Advise(report);
    }

    public static string Summarize(SystemLatencyReport report)
    {
        var noisy = report.NoisyCores;
        return noisy.Count == 0
            ? LocalizationService.Format("LatencyCheckSummaryClean", report.Cores.Count, report.WorstQuietStallUs)
            : LocalizationService.Format("LatencyCheckSummaryNoisy", noisy.Count, report.Cores.Count,
                string.Join(", ", noisy.Select(core => core.Processor)), report.WorstQuietStallUs);
    }

    private static string Advise(SystemLatencyReport report)
    {
        var noisy = report.NoisyCores;
        var avoided = SystemLatencyCheck.StableNoisy.Order().ToList();
        if (noisy.Count == 0 && avoided.Count == 0) return L("LatencyCheckAdviceClean");
        var lines = new List<string>();
        lines.Add(avoided.Count > 0
            ? LocalizationService.Format("LatencyCheckAdviceAvoided", string.Join(", ", avoided))
            : L("LatencyCheckAdviceNotYet"));
        if (noisy.Any(core => core.Processor == 0)) lines.Add(L("LatencyCheckAdviceCore0"));
        if (noisy.Any(core => core.Processor != 0)) lines.Add(L("LatencyCheckAdviceDevice"));
        if (noisy.Count > 0 && noisy.Max(core => core.MaxStallUs) >= 5000) lines.Add(L("LatencyCheckAdviceLong"));
        if (SystemLatencyCheck.StableQuietMask == 0) lines.Add(L("LatencyCheckAdviceTooFew"));
        return string.Join("\n", lines);
    }

    private static string L(string key) => LocalizationService.Get(key);
}
