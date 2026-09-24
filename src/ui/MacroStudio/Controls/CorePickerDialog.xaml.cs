using System.Windows;
using MacroHid.Core;
using MacroHid.Runtime;
using MacroStudio.Services;

namespace MacroStudio.Controls;

public partial class CorePickerDialog : Window
{
    private readonly List<CoreChoiceRow> rows = [];
    private readonly string? currentMask;
    private readonly Func<bool>? measureBlocked;

    public CorePickerDialog(string? currentMask = null, Func<bool>? measureBlocked = null)
    {
        this.currentMask = currentMask;
        this.measureBlocked = measureBlocked;
        InitializeComponent();
        ThemedDialogChrome.Apply(this);
        Title = L("ChooseCoresTitle");
        HelpText.Text = L("ChooseCoresHelp");
        TestButton.Content = L("CoreSelectionTest");
        CancelButton.Content = L("Cancel");
        ConfirmButton.Content = L("Confirm");
        Loaded += (_, _) => LoadCores();
    }

    public string SelectedMask { get; private set; } = string.Empty;

    public bool LastMeasureSucceeded { get; private set; }

    public string LastMeasuredMask { get; private set; } = string.Empty;

    public string LastResultText { get; private set; } = string.Empty;

    private void LoadCores()
    {
        if (CoreList.View is System.Windows.Controls.GridView view && view.Columns.Count >= 5)
        {
            view.Columns[1].Header = L("CoreLogicalHeader");
            view.Columns[2].Header = L("CorePhysicalHeader");
            view.Columns[3].Header = L("CoreKindHeader");
            view.Columns[4].Header = L("CoreSiblingHeader");
        }

        var cores = LogicalProcessorInventory.Query();
        var saved = CheckedProcessors(currentMask);
        var defaults = saved.Count > 0
            ? saved
            : LogicalProcessorInventory.DefaultSelection(cores).ToHashSet();
        var siblings = cores
            .GroupBy(core => core.PhysicalCoreId)
            .ToDictionary(
                group => group.Key,
                group => string.Join(", ", group.Select(core => core.ProcessorNumber.ToString())));
        rows.Clear();
        foreach (var core in cores)
        {
            rows.Add(new CoreChoiceRow(
                core.ProcessorNumber,
                string.Format(L("CoreProcessorText"), core.ProcessorNumber),
                string.Format(L("CorePhysicalText"), core.PhysicalCoreId),
                core.IsPerformanceCore ? L("CorePerformance") : L("CoreEfficiency"),
                string.Format(L("CoreSiblingText"), siblings[core.PhysicalCoreId]),
                defaults.Contains(core.ProcessorNumber)));
        }

        CoreList.ItemsSource = rows;
    }

    private static HashSet<int> CheckedProcessors(string? maskText)
    {
        var selected = new HashSet<int>();
        if (!PlaybackAffinityMask.TryParse(maskText, out var mask) || mask == 0)
        {
            return selected;
        }

        for (var bit = 0; bit < 64; bit++)
        {
            if ((mask & (1UL << bit)) != 0)
            {
                selected.Add(bit);
            }
        }

        return selected;
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        if (measureBlocked?.Invoke() == true)
        {
            ShowResult(L("CoreSelectionBusy"));
            return;
        }

        if (!TryReadCheckedMask(out var mask))
        {
            DialogOwnerService.MessageBoxSafe(this, L("CoreSelectionNone"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SelectedMask = mask;
        DialogResult = true;
    }

    private async void TestButton_Click(object sender, RoutedEventArgs e)
    {
        if (measureBlocked?.Invoke() == true)
        {
            ShowResult(L("CoreSelectionBusy"));
            return;
        }

        if (!TryReadCheckedMask(out var mask))
        {
            ShowResult(L("CoreSelectionNone"));
            return;
        }

        TestButton.IsEnabled = false;
        ConfirmButton.IsEnabled = false;
        ShowResult(L("CoreSelectionTesting"));
        CoreMeasureResult result;
        try
        {
            result = await Task.Run(() => NativePlaybackWarmup.MeasureCheckedCores(mask));
        }
        catch (Exception ex)
        {
            result = default;
            ShowResult(ex.Message);
            TestButton.IsEnabled = true;
            ConfirmButton.IsEnabled = true;
            return;
        }

        if (!IsLoaded)
        {
            return;
        }

        LastMeasuredMask = mask;
        LastMeasureSucceeded = result.Measured;
        ShowResult(result.Measured ? FormatResult(result) : L("CoreSelectionTestFailed"));
        TestButton.IsEnabled = true;
        ConfirmButton.IsEnabled = true;
    }

    private bool TryReadCheckedMask(out string mask)
    {
        var selected = rows.Where(row => row.IsChecked).Select(row => row.ProcessorNumber).ToArray();
        if (selected.Length == 0)
        {
            mask = string.Empty;
            return false;
        }

        mask = LogicalProcessorInventory.MaskFromProcessors(selected);
        return true;
    }

    private void ShowResult(string text)
    {
        LastResultText = text;
        ResultText.Text = text;
    }

    private static string FormatResult(CoreMeasureResult result)
    {
        if (result.SecondaryProcessor < 0 || result.SecondaryMaxLateUs == long.MaxValue)
        {
            return string.Format(L("CoreSelectionTestResultSingle"), result.PrimaryProcessor, result.PrimaryMaxLateUs);
        }

        return string.Format(
            L("CoreSelectionTestResult"),
            result.PrimaryProcessor,
            result.PrimaryMaxLateUs,
            result.SecondaryProcessor,
            result.SecondaryMaxLateUs);
    }

    private static string L(string key) => LocalizationService.Get(key);

    private sealed class CoreChoiceRow
    {
        public CoreChoiceRow(int processorNumber, string processorText, string physicalText, string kindText, string siblingText, bool isChecked)
        {
            ProcessorNumber = processorNumber;
            ProcessorText = processorText;
            PhysicalText = physicalText;
            KindText = kindText;
            SiblingText = siblingText;
            IsChecked = isChecked;
        }

        public int ProcessorNumber { get; }
        public string ProcessorText { get; }
        public string PhysicalText { get; }
        public string KindText { get; }
        public string SiblingText { get; }
        public bool IsChecked { get; set; }
    }
}
