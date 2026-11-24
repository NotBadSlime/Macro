using MacroHid.Core;
using MacroHid.Runtime;
namespace MacroStudio;
public partial class MainWindow
{
    private int jsonRevision;
    private bool jsonValid = true;
    private MacroLibraryRevision? activeLibraryRevision;
    private bool hasUnsavedMacroChanges;
    private bool hasSharedLibraryConflict;
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
        if (activeEditorMacroId == id && activeLibraryRevision is { } expected)
        {
            var item = libraryStore.SaveMacro(id, document, expected, out var saved);
            activeLibraryRevision = saved;
            hasUnsavedMacroChanges = false;
            hasSharedLibraryConflict = false;
            return item;
        }

        return libraryStore.SaveMacro(id, document);
    }
}
