using MacroHid.Core;
using MacroHid.Runtime;
namespace MacroStudio;
public partial class MainWindow
{
    private int jsonRevision;
    private bool jsonValid = true;
    private Task pendingAutoSave = Task.CompletedTask;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> saveRevisions = new();
    private async void QueueJsonValidation(string text)
    {
        int revision = ++jsonRevision;
        string? macroId = activeEditorMacroId;
        jsonValid = false;
        autoSaveTimer.Stop();
        try
        {
            await Task.Delay(250);
            if (revision != jsonRevision || shutdownStarted) return;
            var parsed = await Task.Run(() => McrxParser.Parse(text));
            if (revision != jsonRevision || activeEditorMacroId != macroId || shutdownStarted) return;
            TryApplyJsonTextToEditor(text, false, parsed);
        }
        catch (Exception ex)
        {
            if (revision == jsonRevision && activeEditorMacroId == macroId)
            { jsonValid = false; JsonPanel.SetValidationMessage(ex.Message); }
        }
    }
    private MacroLibraryItem SaveCurrentRevision(string id, MacroDocument document)
    {
        lock (libraryStore.SyncRoot)
        {
            saveRevisions.AddOrUpdate(id, 1, (_, current) => current + 1);
            return libraryStore.SaveMacro(id, document);
        }
    }
}
