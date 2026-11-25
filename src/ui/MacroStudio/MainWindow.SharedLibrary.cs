using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using MacroHid.Core;
using MacroHid.Runtime;

namespace MacroStudio;

public partial class MainWindow
{
    private FileSystemWatcher? sharedLibraryWatcher;
    private DispatcherTimer? sharedLibraryRefreshTimer;
    private int sharedLibraryRefreshRetries;
    private bool sharedLibraryClosed;

    private void StartSharedLibraryWatcher()
    {
        if (!libraryStore.SharesFormalLibrary) return;

        sharedLibraryRefreshTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(350)
        };
        sharedLibraryRefreshTimer.Tick += (_, _) => RefreshSharedLibrary();
        Activated += (_, _) => QueueSharedLibraryRefresh();

        try
        {
            Directory.CreateDirectory(libraryStore.RootDirectory);
            sharedLibraryWatcher = new FileSystemWatcher(libraryStore.RootDirectory)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
            };
            sharedLibraryWatcher.Filters.Add("library.json");
            sharedLibraryWatcher.Filters.Add("*.mcrx");
            sharedLibraryWatcher.Changed += OnSharedLibraryChanged;
            sharedLibraryWatcher.Created += OnSharedLibraryChanged;
            sharedLibraryWatcher.Deleted += OnSharedLibraryChanged;
            sharedLibraryWatcher.Renamed += OnSharedLibraryChanged;
            sharedLibraryWatcher.Error += (_, _) => Dispatcher.BeginInvoke(new Action(QueueSharedLibraryRefresh));
            sharedLibraryWatcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Library", "Could not watch the shared MacroHID library", ex);
        }
    }

    private void OnSharedLibraryChanged(object sender, FileSystemEventArgs e) =>
        Dispatcher.BeginInvoke(new Action(QueueSharedLibraryRefresh));

    private void QueueSharedLibraryRefresh()
    {
        if (sharedLibraryClosed || shutdownStarted) return;
        sharedLibraryRefreshRetries = 0;
        sharedLibraryRefreshTimer?.Stop();
        sharedLibraryRefreshTimer?.Start();
    }

    private void RefreshSharedLibrary()
    {
        sharedLibraryRefreshTimer?.Stop();
        if (sharedLibraryClosed || shutdownStarted) return;

        try
        {
            if (activeEditorMacroId is { } id && activeLibraryRevision is { } revision
                && libraryStore.GetRevision(id) != revision)
            {
                if (hasUnsavedMacroChanges || !jsonValid)
                {
                    MarkSharedLibraryConflict();
                }
                else if (macroRecorder is not null || WorkspaceStepInspector.IsKeyboardFocusWithin
                    || SequencePanelControl.MacroName != SequencePanelControl.GetCurrentDocument().Name)
                {
                    sharedLibraryRefreshTimer?.Start();
                }
                else if (libraryStore.TryGetItem(id) is not null)
                {
                    LoadMacroFromLibrary(id, focusName: false, reportErrors: false);
                }
                else
                {
                    editorState.SelectedMacroId = null;
                    libraryStore.SetSelected(null);
                    ResetEmptyMacroEditor();
                    SetStatus(L("SharedLibraryMacroRemoved"));
                }
            }

            RefreshLibraryListeningState();
            SequencePanelControl.RefreshMacroReferences(ConditionPanel);
            RestartListeningWithCurrentSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException or KeyNotFoundException)
        {
            // The original edition can update a document and the catalogue in separate writes.
            if (++sharedLibraryRefreshRetries <= 5)
                sharedLibraryRefreshTimer?.Start();
            else
                AppLog.Error("Library", "Could not refresh the shared MacroHID library", ex);
        }
    }

    private void MarkSharedLibraryConflict()
    {
        autoSaveTimer.Stop();
        hasSharedLibraryConflict = true;
        SetStatus(L("SharedLibraryEditConflict"), StatusLevel.Warning);
    }
}
