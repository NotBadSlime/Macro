using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MacroHid.Core;

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
    }

    public void ApplyLocalization()
    {
        AddTitleText.Text = L("Add");
        AddDelayText.Text = L("AddDelay");
        AddKeyboardText.Text = L("AddKeyboard");
        AddMouseText.Text = L("AddMouseButton");
        AddMouseMoveText.Text = L("AddMouseMove");
        AddWheelText.Text = L("AddWheel");
        AddTextActionText.Text = L("AddText");
        AddMacroText.Text = L("AddMacro");
        AddLoopText.Text = L("AddLoop");
        AddDelayHintText.Text = L("AddDelayHint");
        AddKeyboardHintText.Text = L("AddKeyboardHint");
        AddMouseHintText.Text = L("AddMouseButtonHint");
        AddMouseMoveHintText.Text = L("AddMouseMoveHint");
        AddWheelHintText.Text = L("AddWheelHint");
        AddTextHintText.Text = L("AddTextHint");
        AddMacroActionHintText.Text = L("AddMacroActionHint");
        AddLoopHintText.Text = L("AddLoopHint");
        DragHint.Text = L("ActionPaletteDragHint");
        AddMacroHintText.Text = L("AddMacroHint");
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
}
