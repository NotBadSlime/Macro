using System.Windows;
using System.Runtime.InteropServices;
using System.Text;
using MacroHid.Core;
using MacroStudio;
using MacroStudio.Controls;
using MacroStudio.Services;

internal static class FileImportUiTests
{
    internal static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { VerifyImport(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new InvalidOperationException("File drop import failed", failure);
    }

    private static void VerifyImport()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/MacroStudio;component/Themes/LightTheme.xaml", UriKind.Relative) });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/MacroStudio;component/Themes/SharedStyles.xaml", UriKind.Relative) });
        var root = Path.Combine(Path.GetTempPath(), "MacroHID-FileDrop-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var store = new MacroLibraryStore(Path.Combine(root, "library"));
            var group = store.CreateGroup("Drop target");
            store.CreateFolder("Routes", group.Id);
            var first = Path.Combine(root, "first.MCRX");
            var second = Path.Combine(root, "second.mcrx");
            File.WriteAllText(first, McrxSerializer.Serialize(new MacroDocument(1, "first", [new WaitStep(TimeSpan.FromMilliseconds(10))])));
            File.WriteAllText(second, McrxSerializer.Serialize(new MacroDocument(1, "second", [new WaitStep(TimeSpan.FromMilliseconds(20))])));
            var unsupported = Path.Combine(root, "ignored.exe");
            File.WriteAllText(unsupported, "not a macro");
            var data = new DataObject(DataFormats.FileDrop,
                new[] { first, first, second, unsupported, root, Path.Combine(root, "missing.mcrx") });
            var files = MainWindow.GetDroppedMacroFiles(data);
            if (!files.SequenceEqual(new[] { first, second }))
                throw new Exception("File drop must accept macro files, deduplicate paths and reject directories/missing/unsupported files.");
            if (MainWindow.GetDroppedMacroFiles(new DataObject(DataFormats.Text, "internal drag")).Length != 0)
                throw new Exception("Internal drags must not be treated as file imports.");
            VerifyShellDrop([first, Path.Combine(root, "中文宏.mcrx")]);

            var panel = new MacroLibraryPanel();
            panel.Initialize(new MacroEditorState(store));
            // Selecting a database in the manager must also permit an external drop.
            typeof(MacroLibraryPanel).GetField("selectedGroupId", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(panel, group.Id);
            panel.ImportMacroFiles(files);
            var imported = store.Load().Items;
            if (imported.Count != 2 || imported.Any(item => item.GroupId != group.Id || item.Folder != string.Empty))
                throw new Exception("Manager drop did not import both files into the selected database.");

            var folderMacro = store.CreateMacro("folder marker", folder: "Routes", groupId: group.Id);
            if (!panel.TryReveal(MacroLibraryPath.Format(group.Name, "Routes", folderMacro.Name), out _))
                throw new Exception("Unable to navigate to destination folder.");
            var third = Path.Combine(root, "third.mcrx");
            File.WriteAllText(third, McrxSerializer.Serialize(new MacroDocument(1, "third", [new WaitStep(TimeSpan.FromMilliseconds(30))])));
            panel.ImportMacroFiles([third]);
            var item = store.Load().Items.Single(item => item.Name == "third");
            if (item.GroupId != group.Id || item.Folder != "Routes" || store.ReadMacro(item.Id).Steps.Count != 1)
                throw new Exception("Drop did not preserve the current folder or macro steps.");
        }
        finally
        {
            app.Shutdown();
            Directory.Delete(root, recursive: true);
        }
    }

    private static void VerifyShellDrop(string[] paths)
    {
        // A real HDROP global-memory block exercises Shell32's Unicode path decoding.
        var names = Encoding.Unicode.GetBytes(string.Join('\0', paths) + "\0\0");
        var drop = GlobalAlloc(0x0042, (nuint)(20 + names.Length));
        if (drop == IntPtr.Zero) throw new Exception("Could not allocate test HDROP.");
        try
        {
            var memory = GlobalLock(drop);
            if (memory == IntPtr.Zero) throw new Exception("Could not lock test HDROP.");
            try
            {
                Marshal.WriteInt32(memory, 0, 20);
                Marshal.WriteInt32(memory, 16, 1);
                Marshal.Copy(names, 0, IntPtr.Add(memory, 20), names.Length);
            }
            finally { GlobalUnlock(drop); }
            if (!MainWindow.ReadShellFileDrop(drop).SequenceEqual(paths))
                throw new Exception("Shell drop lost paths or Unicode characters.");
        }
        finally { GlobalFree(drop); }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr memory);
}
