using MacroHid.Core;

namespace MacroStudio.Services;

public class MacroEditorState
{
    private readonly MacroLibraryStore libraryStore;

    public MacroEditorState(MacroLibraryStore libraryStore)
    {
        this.libraryStore = libraryStore;
    }

    public MacroLibraryStore LibraryStore => libraryStore;
    public MacroLibrarySnapshot LibrarySnapshot { get; set; } = new(
        [],
        null,
        [],
        [new MacroLibraryGroup(MacroLibraryStore.GlobalGroupId, "全局", string.Empty, true)],
        []);
    public string? SelectedMacroId { get; set; }
    public MacroDocument? CurrentDocument { get; set; }
    public int SelectedStepIndex { get; set; } = -1;
    public bool IsPlaying { get; set; }
    public bool IsListening { get; set; }

    public event Action? StateChanged;
    public event Action<string>? StatusMessage;

    public void NotifyChanged() => StateChanged?.Invoke();

    public void SetStatus(string message) => StatusMessage?.Invoke(message);

    public MacroLibrarySnapshot ReloadLibrary()
    {
        LibrarySnapshot = libraryStore.Load();
        return LibrarySnapshot;
    }
}
