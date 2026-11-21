using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using MacroHid.Core;
using MacroStudio;
using MacroStudio.Services;

internal static class PlaybackListeningSelectionTests
{
    internal static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "MacroHID-PlaybackSelection-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new MacroLibraryStore(root);
            var editor = store.CreateMacro(new MacroDocument(1, "editor", PlaybackSettings.Default with
            {
                Trigger = McrxParser.ParseHotkeyGesture("X1"), Mode = PlaybackMode.HoldLoop
            }, []));
            var other = store.CreateMacro(new MacroDocument(1, "other", PlaybackSettings.Default with
            { Trigger = McrxParser.ParseHotkeyGesture("X2") }, []));
            var condition = store.CreateMacro("condition", kind: MacroKind.Condition);
            var state = new MacroEditorState(store);
            // Only exercise the real selection/router code: no window constructor, hooks,
            // native engine or user library is opened by this test.
            var window = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
            void Set(string field, object? value) => typeof(MainWindow)
                .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
            Set("libraryStore", store);
            Set("editorState", state);
            var build = typeof(MainWindow).GetMethod("BuildCurrentListeningCandidate", BindingFlags.Instance | BindingFlags.NonPublic)!;
            foreach (var selected in new string?[] { null, condition.Id, other.Id })
            {
                Set("activeEditorMacroId", editor.Id);
                state.SelectedMacroId = selected;
                var candidate = build.Invoke(window, null)!;
                var item = (MacroLibraryItem)candidate.GetType().GetProperty("Item")!.GetValue(candidate)!;
                var trigger = (HotkeyGesture)candidate.GetType().GetProperty("Trigger")!.GetValue(candidate)!;
                var document = (MacroDocument)candidate.GetType().GetProperty("Document")!.GetValue(candidate)!;
                Check(item.Id == editor.Id && trigger.MouseButton == MouseButton.X1
                    && document.Playback.Mode == PlaybackMode.HoldLoop, "Library selection retargeted the playback panel.");
            }
            Set("activeEditorMacroId", null);
            state.SelectedMacroId = other.Id;
            var fallback = build.Invoke(window, null)!;
            Check(((MacroLibraryItem)fallback.GetType().GetProperty("Item")!.GetValue(fallback)!).Id == other.Id,
                "Selection fallback without an active editor was lost.");
            state.SelectedMacroId = null;
            ExpectMissing(build, window);
            Set("activeEditorMacroId", "deleted-editor");
            state.SelectedMacroId = other.Id;
            ExpectMissing(build, window); // Never silently start another macro when the editor's file is missing.
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    private static void ExpectMissing(MethodInfo method, MainWindow window)
    {
        try { method.Invoke(window, null); }
        catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException) { return; }
        throw new Exception("Missing editor must not silently target another macro.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
