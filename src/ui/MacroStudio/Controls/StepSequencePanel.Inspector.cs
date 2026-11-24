using System.Windows.Threading;
using MacroHid.Core;
using MacroStudio.Services;

namespace MacroStudio.Controls;

public partial class StepSequencePanel
{
    private StepInspectorPanel? stepInspector;
    private Action? requestShowStepInspector;
    private DispatcherOperation? pendingInspectorRefresh;

    public void AttachStepInspector(StepInspectorPanel inspector, Action? onRequestShow = null)
    {
        if (stepInspector is not null)
        {
            stepInspector.ApplyRequested -= ApplyInspectorStep;
            stepInspector.CopyRequested -= CopyInspectorStep;
            stepInspector.RemoveRequested -= RemoveInspectorStep;
            stepInspector.SetSelection(null, null, isReadOnly);
        }

        stepInspector = inspector;
        requestShowStepInspector = onRequestShow;
        inspector.ApplyRequested += ApplyInspectorStep;
        inspector.CopyRequested += CopyInspectorStep;
        inspector.RemoveRequested += RemoveInspectorStep;
        if (state is not null) inspector.Initialize(state);
        inspector.ApplyLocalization();
        RefreshStepInspector();
    }

    private bool TryUseStepInspector()
    {
        if (stepInspector is null) return false;
        if (!stepInspector.IsVisible) requestShowStepInspector?.Invoke();
        if (!stepInspector.IsVisible) return false;
        InlineStepEditorPopup.IsOpen = false;
        RefreshStepInspector();
        return true;
    }

    private void ScheduleStepInspectorRefresh()
    {
        if (stepInspector is null || pendingInspectorRefresh?.Status == DispatcherOperationStatus.Pending) return;
        pendingInspectorRefresh = Dispatcher.BeginInvoke(DispatcherPriority.DataBind, (Action)RefreshStepInspector);
    }

    private void RefreshStepInspector()
    {
        if (pendingInspectorRefresh?.Status == DispatcherOperationStatus.Pending)
            pendingInspectorRefresh.Abort();
        pendingInspectorRefresh = null;
        if (stepInspector is null) return;

        if (TryGetInspectorSelection(out var row, out var step))
            stepInspector.SetSelection(row, step, isReadOnly);
        else
            stepInspector.SetSelection(null, null, isReadOnly);
    }

    private bool TryGetInspectorSelection(out StepDisplayItem row, out MacroStep step)
    {
        row = null!;
        step = null!;
        if (StepList.SelectedItems.Count != 1 ||
            StepList.SelectedItem is not StepDisplayItem { StepPath.Count: > 0 } selected)
            return false;

        try
        {
            step = MacroStepTreeEditor.GetAtPath(steps, selected.StepPath);
            if (!identities.TryGetValue(step, out var identity) || identity.Value != selected.Identity)
                return false;
            row = selected;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private void ApplyInspectorStep()
    {
        if (isReadOnly || stepInspector is not { } inspector ||
            !TryGetInspectorSelection(out var row, out var step) ||
            !inspector.IsEditing(step))
            return;

        try
        {
            var edited = inspector.BuildEditedStep(step);
            ApplyEditedStepAtPath(row.StepPath, edited);
            RefreshStepInspector();
            inspector.ShowStatus(LocalizationService.Get("StepPropertiesApplied"));
        }
        catch (Exception ex)
        {
            inspector.ShowStatus(ex.Message, error: true);
        }
    }

    private void CopyInspectorStep()
    {
        if (stepInspector is not { } inspector ||
            !TryGetInspectorSelection(out var row, out var step) ||
            !inspector.IsEditing(step) || step is ParallelBranchStep)
            return;

        var copied = CopyStepPathsToClipboard([row.StepPath]);
        inspector.ShowStatus(copied
            ? LocalizationService.Format("SelectedItemsCopied", 1)
            : LocalizationService.Get("StepTableCopyFailed"), error: !copied);
    }

    private void RemoveInspectorStep()
    {
        if (isReadOnly || stepInspector is not { } inspector ||
            !TryGetInspectorSelection(out var row, out var step) ||
            !inspector.IsEditing(step))
            return;

        DeleteStepAtPath(row.StepPath);
        RefreshStepInspector();
    }
}
