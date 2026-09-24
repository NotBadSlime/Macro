using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using MacroHid.Core;
using MacroHid.Runtime;
using MacroStudio.Services;

namespace MacroStudio.Controls;

public partial class CorePickerDialog : Window
{
    private readonly ObservableCollection<CoreChoiceRow> rows = [];
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
        SelectAllButton.Content = L("CoreSelectionSelectAll");
        SelectNoneButton.Content = L("CoreSelectionSelectNone");
        AutoSelectCountLabel.Text = L("CoreSelectionCount");
        AutoSelectButton.Content = L("CoreSelectionAutoSelect");
        SortByResultButton.Content = L("CoreSelectionSortByResult");
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
        if (CoreList.View is System.Windows.Controls.GridView view && view.Columns.Count >= 6)
        {
            view.Columns[1].Header = L("CoreLogicalHeader");
            view.Columns[2].Header = L("CorePhysicalHeader");
            view.Columns[3].Header = L("CoreKindHeader");
            view.Columns[4].Header = L("CoreSiblingHeader");
            view.Columns[5].Header = L("CoreResultHeader");
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
                core.PhysicalCoreId,
                string.Format(L("CoreProcessorText"), core.ProcessorNumber),
                string.Format(L("CorePhysicalText"), core.PhysicalCoreId),
                core.IsPerformanceCore ? L("CorePerformance") : L("CoreEfficiency"),
                string.Format(L("CoreSiblingText"), siblings[core.PhysicalCoreId]),
                defaults.Contains(core.ProcessorNumber),
                L("CoreResultNone")));
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

    private void SelectAllButton_Click(object sender, RoutedEventArgs e)
    {
        SetAllChecked(true);
    }

    private void SelectNoneButton_Click(object sender, RoutedEventArgs e)
    {
        SetAllChecked(false);
    }

    private void SetAllChecked(bool isChecked)
    {
        foreach (var row in rows)
        {
            row.IsChecked = isChecked;
        }
    }

    private void SetTestingEnabled(bool enabled)
    {
        TestButton.IsEnabled = enabled;
        ConfirmButton.IsEnabled = enabled;
        SelectAllButton.IsEnabled = enabled;
        SelectNoneButton.IsEnabled = enabled;
        AutoSelectButton.IsEnabled = enabled;
        SortByResultButton.IsEnabled = enabled;
        AutoSelectCountBox.IsEnabled = enabled;
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

        foreach (var row in rows)
        {
            row.ResultText = L("CoreResultNone");
            row.MaxLateUs = null;
        }

        SetTestingEnabled(false);
        ShowResult(L("CoreSelectionTesting"));
        CoreMeasureResult result;
        try
        {
            result = await Task.Run(() => NativePlaybackWarmup.MeasureCheckedCores(
                mask,
                (processor, completed, total, maxLate) =>
                    Dispatcher.Invoke(() => ReportCoreProgress(processor, completed, total, maxLate))));
        }
        catch (Exception ex)
        {
            result = default;
            ShowResult(ex.Message);
            SetTestingEnabled(true);
            return;
        }

        if (!IsLoaded)
        {
            return;
        }

        LastMeasuredMask = mask;
        LastMeasureSucceeded = result.Measured;
        ApplyMeasuredRows(result);
        SortByResult();
        ShowResult(result.Measured ? FormatResult(result) : L("CoreSelectionTestFailed"));
        SetTestingEnabled(true);
    }

    private void ReportCoreProgress(int processor, int completed, int total, long maxLate)
    {
        var row = rows.FirstOrDefault(item => item.ProcessorNumber == processor);
        if (maxLate < 0)
        {
            ShowResult(string.Format(L("CoreSelectionTestingCore"), processor, completed, total));
            if (row is not null)
            {
                row.ResultText = L("CoreResultPending");
            }

            return;
        }

        if (row is null || maxLate == long.MaxValue)
        {
            return;
        }

        row.MaxLateUs = maxLate;
        row.ResultText = string.Format(L("CoreResultLate"), maxLate);
    }

    private void ApplyMeasuredRows(CoreMeasureResult result)
    {
        var playback = new HashSet<int>();
        if (result.PrimaryProcessor >= 0)
        {
            playback.Add(result.PrimaryProcessor);
        }

        if (result.SecondaryProcessor >= 0)
        {
            playback.Add(result.SecondaryProcessor);
        }

        foreach (var sample in result.Samples)
        {
            var row = rows.FirstOrDefault(item => item.ProcessorNumber == sample.ProcessorNumber);
            if (row is null || sample.MaxLateUs < 0 || sample.MaxLateUs == long.MaxValue)
            {
                continue;
            }

            row.MaxLateUs = sample.MaxLateUs;
            row.ResultText = playback.Contains(sample.ProcessorNumber)
                ? string.Format(L("CoreResultPlayback"), sample.MaxLateUs)
                : string.Format(L("CoreResultLate"), sample.MaxLateUs);
        }
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

    private void SortByResultButton_Click(object sender, RoutedEventArgs e)
    {
        SortByResult();
        ShowResult(L("CoreSelectionSorted"));
    }

    private void AutoSelectButton_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(AutoSelectCountBox.Text.Trim(), out var count) || count < 1)
        {
            ShowResult(L("CoreSelectionCountInvalid"));
            return;
        }

        var measured = rows
            .Where(row => row.MaxLateUs is long late && late >= 0 && late != long.MaxValue)
            .Select(row => (row.ProcessorNumber, row.PhysicalCoreId, row.MaxLateUs!.Value))
            .ToArray();
        if (measured.Length == 0)
        {
            ShowResult(L("CoreSelectionNeedResults"));
            return;
        }

        var chosen = LogicalProcessorInventory.SelectByMeasuredLatency(measured, count).ToHashSet();
        foreach (var row in rows)
        {
            row.IsChecked = chosen.Contains(row.ProcessorNumber);
        }

        SortByResult();
        ShowResult(string.Format(L("CoreSelectionAutoSelected"), chosen.Count));
    }

    private void SortByResult()
    {
        var ordered = rows
            .OrderBy(row => row.MaxLateUs is null)
            .ThenBy(row => row.MaxLateUs ?? long.MaxValue)
            .ThenBy(row => row.ProcessorNumber)
            .ToList();
        rows.Clear();
        foreach (var row in ordered)
        {
            rows.Add(row);
        }
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

    private sealed class CoreChoiceRow : INotifyPropertyChanged
    {
        private bool isChecked;
        private string resultText;
        private long? maxLateUs;

        public CoreChoiceRow(int processorNumber, int physicalCoreId, string processorText, string physicalText, string kindText, string siblingText, bool isChecked, string resultText)
        {
            ProcessorNumber = processorNumber;
            PhysicalCoreId = physicalCoreId;
            ProcessorText = processorText;
            PhysicalText = physicalText;
            KindText = kindText;
            SiblingText = siblingText;
            this.isChecked = isChecked;
            this.resultText = resultText;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public int ProcessorNumber { get; }
        public int PhysicalCoreId { get; }
        public string ProcessorText { get; }
        public string PhysicalText { get; }
        public string KindText { get; }
        public string SiblingText { get; }

        public bool IsChecked
        {
            get => isChecked;
            set => SetField(ref isChecked, value);
        }

        public string ResultText
        {
            get => resultText;
            set => SetField(ref resultText, value);
        }

        public long? MaxLateUs
        {
            get => maxLateUs;
            set => maxLateUs = value;
        }

        private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return;
            }

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
