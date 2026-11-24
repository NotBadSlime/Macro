using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MacroHid.Core;
using MacroStudio.Services;

namespace MacroStudio.Controls;

public partial class ActionPalettePanel : UserControl
{
    private const string ActionTemplateDragFormat = "MacroHID.ActionTemplate";
    private Point paletteDragStartPoint;
    private bool paletteDragStarted;
    private bool isReadOnly;
    private readonly (CollapsibleSection Section, UniformGrid Grid)[] searchSections;
    private bool[]? expansionBeforeSearch;

    public event Action<MacroActionTemplateKind>? ActionClicked;

    public ActionPalettePanel()
    {
        InitializeComponent();
        searchSections =
        [
            (BasicActionsSection, BasicCommandGrid),
            (AdvancedActionsSection, AdvancedCommandGrid),
            (YsrUnlockerSection, YsrCommandGrid),
            (LoopActionsSection, LoopCommandGrid)
        ];
        Loaded += ActionPalettePanel_Loaded;
        Unloaded += ActionPalettePanel_Unloaded;
    }

    public void ApplyLocalization()
    {
        AddTitleText.Text = L("Add");
        BasicActionsSection.Header = L("PaletteBasic");
        AdvancedActionsSection.Header = L("PaletteAdvanced");
        LoopActionsSection.Header = L("PaletteLoop");
        YsrUnlockerSection.Header = L("PaletteYsrUnlocker");
        AddDelayText.Text = L("AddDelay");
        AddKeyboardText.Text = L("AddKeyboard");
        AddMouseText.Text = L("AddMouseButton");
        AddMouseMoveText.Text = L("AddMouseMove");
        AddWheelText.Text = L("AddWheel");
        AddWindowActivateText.Text = L("AddWindowActivate");
        AddOcrExtractTextText.Text = L("AddOcrExtractText");
        AddOcrClickText.Text = L("AddOcrClick");
        AddTextActionText.Text = L("AddText");
        AddCommentText.Text = L("AddComment");
        AddMacroText.Text = L("AddMacro");
        AddPixelText.Text = L("AddPixel");
        AddTextWhenText.Text = L("AddTextWhen");
        AddNetLimitStartText.Text = L("AddNetLimitStart");
        AddNetLimitStopText.Text = L("AddNetLimitStop");
        AddNetLimiterStartText.Text = L("AddNetLimiterStart");
        AddNetLimiterStopText.Text = L("AddNetLimiterStop");
        AddClockShiftText.Text = L("AddClockShift");
        AddClockRestoreText.Text = L("AddClockRestore");
        AddWeakNetStartText.Text = L("AddWeakNetStart");
        AddWeakNetStopText.Text = L("AddWeakNetStop");
        AddFrameLockStartText.Text = L("AddFrameLockStart");
        AddFrameLockStopText.Text = L("AddFrameLockStop");
        AddYsrFpsToggleText.Text = L("AddYsrFpsStart");
        AddYsrFpsStopText.Text = L("AddYsrFpsStop");
        AddKy3FpsStartText.Text = L("AddKy3FpsStart");
        AddFufuFpsStartText.Text = L("AddFufuFpsStart");
        AddKy3FpsStopText.Text = L("AddKy3FpsStop");
        AddFufuFpsStopText.Text = L("AddFufuFpsStop");
        AddLoopText.Text = L("AddLoop");
        AddStopCurrentText.Text = L("AddStopCurrent");
        AddStopIterationText.Text = L("AddStopIteration");
        AddStopAllText.Text = L("AddStopAll");
        AddDelayHintText.Text = L("AddDelayHint");
        AddKeyboardHintText.Text = L("AddKeyboardHint");
        AddMouseHintText.Text = L("AddMouseButtonHint");
        AddMouseMoveHintText.Text = L("AddMouseMoveHint");
        AddWheelHintText.Text = L("AddWheelHint");
        AddWindowActivateHintText.Text = L("AddWindowActivateHint");
        AddOcrExtractTextHintText.Text = L("AddOcrExtractTextHint");
        AddOcrClickHintText.Text = L("AddOcrClickHint");
        AddTextHintText.Text = L("AddTextHint");
        AddCommentHintText.Text = L("AddCommentHint");
        AddMacroActionHintText.Text = L("AddMacroActionHint");
        AddPixelHintText.Text = L("AddPixelHint");
        AddTextWhenHintText.Text = L("AddTextWhenHint");
        AddNetLimitStartHintText.Text = L("AddNetLimitStartHint");
        AddNetLimitStopHintText.Text = L("AddNetLimitStopHint");
        AddNetLimiterStartHintText.Text = L("AddNetLimiterStartHint");
        AddNetLimiterStopHintText.Text = L("AddNetLimiterStopHint");
        AddClockShiftHintText.Text = L("AddClockShiftHint");
        AddClockRestoreHintText.Text = L("AddClockRestoreHint");
        AddWeakNetStartHintText.Text = L("AddWeakNetStartHint");
        AddWeakNetStopHintText.Text = L("AddWeakNetStopHint");
        AddFrameLockStartHintText.Text = L("AddFrameLockStartHint");
        AddFrameLockStopHintText.Text = L("AddFrameLockStopHint");
        AddYsrFpsToggleHintText.Text = L("AddYsrFpsStartHint");
        AddYsrFpsStopHintText.Text = L("AddYsrFpsStopHint");
        AddKy3FpsStartHintText.Text = L("AddKy3FpsStartHint");
        AddFufuFpsStartHintText.Text = L("AddFufuFpsStartHint");
        AddKy3FpsStopHintText.Text = L("AddKy3FpsStopHint");
        AddFufuFpsStopHintText.Text = L("AddFufuFpsStopHint");
        AddLoopHintText.Text = L("AddLoopHint");
        AddStopCurrentHintText.Text = L("AddStopCurrentHint");
        AddStopIterationHintText.Text = L("AddStopIterationHint");
        AddStopAllHintText.Text = L("AddStopAllHint");
        DragHint.Text = L(isReadOnly ? "MacroLockedReadOnly" : "ActionPaletteDragHint");
        AddMacroHintText.Text = L("AddMacroHint");
        ActionColorSettingsButton.ToolTip = L("ActionColorSettingsTitle");
        ActionSearchPlaceholder.Text = L("ActionPaletteSearch");
        ActionSearchEmptyText.Text = L("ActionPaletteSearchEmpty");
        ClearActionSearchButton.ToolTip = L("ClearActionPaletteSearch");
        AutomationProperties.SetName(ClearActionSearchButton, L("ClearActionPaletteSearch"));
        foreach (var (_, grid) in searchSections)
        {
            foreach (var button in grid.Children.OfType<Button>())
            {
                var labels = GetActionLabels(button);
                if (labels.FirstOrDefault() is { } title)
                    AutomationProperties.SetName(button, title.Text);
            }
        }
        UpdateActionSearch();
    }

    private static IEnumerable<TextBlock> GetActionLabels(Button action) =>
        (action.Content as Grid)?.Children.OfType<StackPanel>().FirstOrDefault()?.Children.OfType<TextBlock>() ?? [];

    private void ActionSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (searchSections is not null)
            UpdateActionSearch();
    }

    private void ActionSearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || ActionSearchBox.Text.Length == 0) return;
        ActionSearchBox.Clear();
        e.Handled = true;
    }

    private void ClearActionSearch_Click(object sender, RoutedEventArgs e)
    {
        ActionSearchBox.Clear();
        ActionSearchBox.Focus();
    }

    private void UpdateActionSearch()
    {
        var terms = ActionSearchBox.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var searching = terms.Length > 0;
        if (searching && expansionBeforeSearch is null)
            expansionBeforeSearch = searchSections.Select(group => group.Section.IsExpanded).ToArray();

        var anyVisible = false;
        for (var index = 0; index < searchSections.Length; index++)
        {
            var (section, grid) = searchSections[index];
            var sectionHasMatches = false;
            foreach (var button in grid.Children.OfType<Button>())
            {
                var sectionTitle = section.Header?.ToString();
                var searchableText = string.Join(" ", GetActionLabels(button).Select(label => label.Text).Append(sectionTitle).Append(button.Tag?.ToString()));
                var visible = terms.All(term => CultureInfo.CurrentUICulture.CompareInfo.IndexOf(searchableText, term,
                    CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0);
                button.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                sectionHasMatches |= visible;
            }

            section.Visibility = sectionHasMatches ? Visibility.Visible : Visibility.Collapsed;
            if (searching && sectionHasMatches)
                section.IsExpanded = true;
            else if (!searching && expansionBeforeSearch is not null)
                section.IsExpanded = expansionBeforeSearch[index];

            anyVisible |= sectionHasMatches;
        }

        if (!searching)
            expansionBeforeSearch = null;
        ClearActionSearchButton.Visibility = ActionSearchBox.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ActionSearchEmptyText.Visibility = searching && !anyVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ActionPalette_Click(object sender, RoutedEventArgs e)
    {
        if (paletteDragStarted)
        {
            paletteDragStarted = false;
            e.Handled = true;
            return;
        }

        if (sender is Button button && TryParseKind(button.Tag?.ToString(), out var kind))
        {
            ActionClicked?.Invoke(kind);
        }
    }

    private void ActionPalette_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        paletteDragStartPoint = e.GetPosition(this);
        paletteDragStarted = false;
    }

    private void ActionPalette_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (paletteDragStarted || e.LeftButton != MouseButtonState.Pressed || sender is not Button button)
            return;
        if (button.Tag?.ToString() is not { Length: > 0 } tag)
            return;

        var current = e.GetPosition(this);
        var moved = Math.Abs(current.X - paletteDragStartPoint.X) > SystemParameters.MinimumHorizontalDragDistance
            || Math.Abs(current.Y - paletteDragStartPoint.Y) > SystemParameters.MinimumVerticalDragDistance;
        if (!moved)
            return;

        paletteDragStarted = true;
        button.Opacity = 0.68;
        try
        {
            DragDrop.DoDragDrop(button, new DataObject(ActionTemplateDragFormat, tag), DragDropEffects.Copy);
        }
        finally
        {
            button.Opacity = 1;
        }

        e.Handled = true;
    }

    private void ActionPalette_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateAdaptiveColumns();
    }

    private void UpdateAdaptiveColumns()
    {
        var columns = ActualWidth >= 520 ? 2 : 1;
        BasicCommandGrid.Columns = columns;
        AdvancedCommandGrid.Columns = columns;
        YsrCommandGrid.Columns = columns;
        LoopCommandGrid.Columns = columns;
    }

    private static bool TryParseKind(string? tag, out MacroActionTemplateKind kind)
    {
        return Enum.TryParse(tag, ignoreCase: true, out kind);
    }

    private static string L(string key) => LocalizationService.Get(key);

    private void ActionPalettePanel_Loaded(object sender, RoutedEventArgs e)
    {
        ActionAppearanceService.AppearanceChanged -= OnAppearanceChanged;
        ActionAppearanceService.AppearanceChanged += OnAppearanceChanged;
        ApplyActionAppearances();
    }

    public void SetReadOnly(bool value)
    {
        isReadOnly = value;
        BasicCommandGrid.IsEnabled = !value;
        AdvancedCommandGrid.IsEnabled = !value;
        YsrCommandGrid.IsEnabled = !value;
        LoopCommandGrid.IsEnabled = !value;
        DragHint.Text = value ? L("MacroLockedReadOnly") : L("ActionPaletteDragHint");
    }

    private void ActionPalettePanel_Unloaded(object sender, RoutedEventArgs e)
    {
        ActionAppearanceService.AppearanceChanged -= OnAppearanceChanged;
    }

    private void OnAppearanceChanged()
    {
        if (Dispatcher.CheckAccess())
        {
            ApplyActionAppearances();
        }
        else
        {
            Dispatcher.Invoke(ApplyActionAppearances);
        }
    }

    private void ApplyActionAppearances()
    {
        ApplyButtonAppearance(AddDelayButton, MacroActionTemplateKind.Delay);
        ApplyButtonAppearance(AddKeyboardButton, MacroActionTemplateKind.Keyboard);
        ApplyButtonAppearance(AddMouseButton, MacroActionTemplateKind.MouseButton);
        ApplyButtonAppearance(AddMouseMoveButton, MacroActionTemplateKind.MouseMove);
        ApplyButtonAppearance(AddWheelButton, MacroActionTemplateKind.MouseWheel);
        ApplyButtonAppearance(AddWindowActivateButton, MacroActionTemplateKind.WindowActivate);
        ApplyButtonAppearance(AddOcrExtractTextButton, MacroActionTemplateKind.OcrExtractText);
        ApplyButtonAppearance(AddOcrClickButton, MacroActionTemplateKind.OcrClick);
        ApplyButtonAppearance(AddTextButton, MacroActionTemplateKind.Text);
        ApplyButtonAppearance(AddCommentButton, MacroActionTemplateKind.Comment);
        ApplyButtonAppearance(AddMacroButton, MacroActionTemplateKind.Macro);
        ApplyButtonAppearance(AddPixelButton, MacroActionTemplateKind.Pixel);
        ApplyButtonAppearance(AddTextWhenButton, MacroActionTemplateKind.TextWhen);
        ApplyButtonAppearance(AddNetLimitStartButton, MacroActionTemplateKind.NetLimitStart);
        ApplyButtonAppearance(AddNetLimitStopButton, MacroActionTemplateKind.NetLimitStop);
        ApplyButtonAppearance(AddNetLimiterStartButton, MacroActionTemplateKind.NetLimiterStart);
        ApplyButtonAppearance(AddNetLimiterStopButton, MacroActionTemplateKind.NetLimiterStop);
        ApplyButtonAppearance(AddClockShiftButton, MacroActionTemplateKind.ClockShift);
        ApplyButtonAppearance(AddClockRestoreButton, MacroActionTemplateKind.ClockRestore);
        ApplyButtonAppearance(AddWeakNetStartButton, MacroActionTemplateKind.WeakNetStart);
        ApplyButtonAppearance(AddWeakNetStopButton, MacroActionTemplateKind.WeakNetStop);
        ApplyButtonAppearance(AddFrameLockStartButton, MacroActionTemplateKind.FrameLockStart);
        ApplyButtonAppearance(AddFrameLockStopButton, MacroActionTemplateKind.FrameLockStop);
        ApplyButtonAppearance(AddYsrFpsToggleButton, MacroActionTemplateKind.YsrFpsStart);
        ApplyButtonAppearance(AddYsrFpsStopButton, MacroActionTemplateKind.YsrFpsStop);
        ApplyButtonAppearance(AddKy3FpsStartButton, MacroActionTemplateKind.Ky3FpsStart);
        ApplyButtonAppearance(AddFufuFpsStartButton, MacroActionTemplateKind.FufuFpsStart);
        ApplyButtonAppearance(AddKy3FpsStopButton, MacroActionTemplateKind.Ky3FpsStop);
        ApplyButtonAppearance(AddFufuFpsStopButton, MacroActionTemplateKind.FufuFpsStop);
        ApplyButtonAppearance(AddLoopButton, MacroActionTemplateKind.Loop);
        ApplyButtonAppearance(AddStopCurrentButton, MacroActionTemplateKind.StopCurrent);
        ApplyButtonAppearance(AddStopIterationButton, MacroActionTemplateKind.StopIteration);
        ApplyButtonAppearance(AddStopAllButton, MacroActionTemplateKind.StopAll);
    }

    private static void ApplyButtonAppearance(Button button, MacroActionTemplateKind kind)
    {
        var appearance = ActionAppearanceService.GetBrushes(kind);
        button.Background = appearance.Background;
        button.Foreground = appearance.Text;

        if (button.Content is not Grid grid)
        {
            return;
        }

        var iconBadge = grid.Children.OfType<Border>().FirstOrDefault();
        if (iconBadge?.Child is ActionGlyph icon)
        {
            icon.Foreground = appearance.Icon;
            if (appearance.Icon is SolidColorBrush iconBrush)
            {
                var color = iconBrush.Color;
                iconBadge.Background = new SolidColorBrush(Color.FromArgb(38, color.R, color.G, color.B));
                iconBadge.BorderBrush = new SolidColorBrush(Color.FromArgb(100, color.R, color.G, color.B));
            }
        }

        var labels = grid.Children.OfType<StackPanel>().FirstOrDefault()?.Children.OfType<TextBlock>().ToList() ?? [];
        for (var index = 0; index < labels.Count; index++)
        {
            labels[index].Foreground = appearance.Text;
            labels[index].Opacity = index == 0 ? 1 : 0.7;
        }
    }

    private void ActionColorSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        DialogOwnerService.ShowDialogSafe(new ActionAppearanceDialog(), this);
    }
}
