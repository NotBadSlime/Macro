using MacroHid.Runtime;

namespace MacroStudio;

public partial class MainWindow
{
    private void OnStartupGateWaitChanged()
    {
        if (shutdownStarted) return;
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (!shutdownStarted) UpdatePlaybackStatus();
        });
    }

    private void RefreshWorkspaceStartupGateWait()
    {
        var editorId = activeEditorMacroId ?? editorState.SelectedMacroId;
        StartupGateWait? wait;
        lock (triggerGate) wait = playbackRuns.Find(editorId)?.Controller.WaitingStartupGate;
        if (wait is null)
        {
            SequencePanelControl.SetStartupGateWait(null, null);
            return;
        }

        var isEditorDocument = (editorId is not null && string.Equals(wait.Document.Id, editorId, StringComparison.OrdinalIgnoreCase))
            || string.Equals(wait.Document.Name, SequencePanelControl.MacroName, StringComparison.Ordinal);
        var localGate = isEditorDocument && ConditionPanel.Conditions.Any(condition => condition.Id == wait.Condition.Id);
        if (editorId is null && !localGate)
        {
            SequencePanelControl.SetStartupGateWait(null, null);
            return;
        }

        var name = Controls.SequencePanel.StartupGateName(wait.Condition);
        SequencePanelControl.SetStartupGateWait(localGate ? name : $"{wait.Document.Name} / {name}",
            localGate ? wait.Condition.Id : null);
    }

    private StartupGateWait? GetPlaybackStartupGateWait()
    {
        lock (triggerGate)
        {
            return playbackController?.WaitingStartupGate ?? playbackRuns.Active
                .OrderBy(run => run.Daemon)
                .Select(run => run.Controller.WaitingStartupGate)
                .FirstOrDefault(wait => wait is not null);
        }
    }

    private bool UpdateStartupGateStatus()
    {
        RefreshWorkspaceStartupGateWait();
        if (GetPlaybackStartupGateWait() is not { } wait) return false;

        var statusText = LocalizationService.Format("PlaybackWaitingForStartupGate", wait.Document.Name,
            Controls.SequencePanel.StartupGateName(wait.Condition));
        PlaybackPanelControl.SetPlaybackStatus(statusText);
        SetStatus(statusText);
        return true;
    }
}
