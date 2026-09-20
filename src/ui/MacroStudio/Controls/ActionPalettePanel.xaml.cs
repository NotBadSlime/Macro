using System.Windows;
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

    public event Action<MacroActionTemplateKind>? ActionClicked;

    public ActionPalettePanel()
    {
        InitializeComponent();
        Loaded += ActionPalettePanel_Loaded;
        Unloaded += ActionPalettePanel_Unloaded;
    }

    public void ApplyLocalization()
    {
        AddTitleText.Text = L("Add");
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
        AddLoopHintText.Text = L("AddLoopHint");
        AddStopCurrentHintText.Text = L("AddStopCurrentHint");
        AddStopIterationHintText.Text = L("AddStopIterationHint");
        AddStopAllHintText.Text = L("AddStopAllHint");
        DragHint.Text = L("ActionPaletteDragHint");
        AddMacroHintText.Text = L("AddMacroHint");
        ActionColorSettingsButton.ToolTip = L("ActionColorSettingsTitle");
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
        ActionCommandGrid.Columns = ActualWidth >= 520 ? 2 : 1;
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
        ActionCommandGrid.IsEnabled = !value;
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
        if (iconBadge?.Child is TextBlock icon)
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
