using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using MacroHid.Core;
using MacroStudio.Services;

namespace MacroStudio.Controls;

/// <summary>Right-hand editor for the current step in the main sequence.</summary>
public partial class StepInspectorPanel : UserControl
{
    private MacroStep? current;
    private bool isReadOnly;

    public StepInspectorPanel()
    {
        InitializeComponent();
        Editor.UseEmbeddedPresentation();
        Editor.StepEdited += _ => ApplyRequested?.Invoke();
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) Editor.CancelStepKeyCapture();
        };
    }

    internal event Action? ApplyRequested;
    internal event Action? CopyRequested;
    internal event Action? RemoveRequested;

    internal void ApplyLocalization()
    {
        EmptySelectionText.Text = L("StepPropertiesHint");
        ApplyButton.Content = L("Apply");
        CopyButton.Content = L("Copy");
        RemoveButton.Content = L("Delete");
        Editor.ApplyLocalization();
    }

    internal void Initialize(MacroEditorState state) => Editor.Initialize(state);

    internal void SetSelection(StepDisplayItem? row, MacroStep? step, bool readOnly)
    {
        EmptySelectionText.Visibility = row is null ? Visibility.Visible : Visibility.Collapsed;
        SelectedDetail.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
        if (!ReferenceEquals(current, step))
        {
            current = step;
            if (step is MacroCallStep call) Editor.RefreshMacroTargetBox(call.Macro);
            Editor.ShowStep(step);
            ShowStatus(string.Empty);
        }

        if (!readOnly && isReadOnly) ShowStatus(string.Empty);
        isReadOnly = readOnly;
        if (row is null) return;

        ActionHeading.Text = row.Table.Action;
        KeyParameterPreview.Visibility = row.Table.IsKey ? Visibility.Visible : Visibility.Collapsed;
        KeyStateArrow.Text = row.Table.Indicator;
        KeyStateArrow.ToolTip = row.Table.StateDescription;
        AutomationProperties.SetName(KeyStateArrow, row.Table.StateDescription);
        KeyPreviewText.Text = row.Table.Parameter;
        KeyPreviewText.ToolTip = row.Table.AccessibleParameter;
        ParameterPreview.Text = row.Table.ParameterWithIndicator;
        ParameterPreview.Visibility = row.Table.IsKey || string.IsNullOrEmpty(ParameterPreview.Text)
            ? Visibility.Collapsed : Visibility.Visible;
        StepGlyph.Data = row.IconGeometry;
        StepGlyph.Foreground = row.IconBrush;
        Editor.IsEnabled = !readOnly;
        if (readOnly) Editor.CancelStepKeyCapture();
        ApplyButton.IsEnabled = !readOnly && Editor.CanApplyStepEdit;
        RemoveButton.IsEnabled = !readOnly && step is not ParallelBranchStep;
        CopyButton.IsEnabled = step is not ParallelBranchStep;
        if (readOnly) ShowStatus(L("MacroLockedReadOnly"));
    }

    internal bool IsEditing(MacroStep step) => ReferenceEquals(current, step);

    internal MacroStep BuildEditedStep(MacroStep currentStep) => Editor.BuildEditedStep(currentStep);

    internal void ShowStatus(string text, bool error = false)
    {
        EditStatusText.Text = text;
        EditStatusText.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        EditStatusText.SetResourceReference(TextBlock.ForegroundProperty, error ? "Danger" : "SecondaryText");
    }

    private void Apply_Click(object sender, RoutedEventArgs e) => ApplyRequested?.Invoke();
    private void Copy_Click(object sender, RoutedEventArgs e) => CopyRequested?.Invoke();
    private void Remove_Click(object sender, RoutedEventArgs e) => RemoveRequested?.Invoke();
    private static string L(string key) => LocalizationService.Get(key);
}
