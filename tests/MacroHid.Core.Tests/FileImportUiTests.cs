using System.IO.Compression;
using System.Windows;
using System.Runtime.InteropServices;
using System.Text;
using MacroHid.Converter;
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
            VerifyMixedZipDrop(root, panel, store, group.Id, third);
            VerifyInvalidZipEntries(root);
        }
        finally
        {
            app.Shutdown();
            Directory.Delete(root, recursive: true);
        }
    }

    private static void VerifyMixedZipDrop(string root, MacroLibraryPanel panel, MacroLibraryStore store, string groupId, string looseMacro)
    {
        var source = new MacroLibraryStore(Path.Combine(root, "export-source"));
        var dependency = source.CreateMacro(new MacroDocument(1, "依赖宏", [new WaitStep(TimeSpan.FromMilliseconds(5))]));
        var primary = source.CreateMacro(new MacroDocument(1, "主宏", [new MacroCallStep(dependency.Id)]));
        var bundle = MacroLibraryExportBundles.FromItems(source, source.Load(), [primary]);
        var zipPath = Path.Combine(root, "宏导出.ZIP");
        MacroLibraryExportWriter.WriteZip(bundle, zipPath, "导出", "主宏", "依赖子宏", MacroConversionFormat.MacroHidMcrx);
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Update))
        {
            zip.CreateEntry("empty/");
            zip.CreateEntry("metadata/readme.md");
            zip.CreateEntry("metadata/unused.zip");
            using var readme = new StreamWriter(zip.CreateEntry("README.txt").Open());
            readme.Write("A readme is not a macro.");
        }

        var otherZip = Path.Combine(root, "another.zip");
        var otherBundle = bundle with
        {
            Dependencies = bundle.Dependencies.Select(entry => entry with
            {
                Document = entry.Document with { Steps = [new WaitStep(TimeSpan.FromMilliseconds(8))] }
            }).ToArray()
        };
        MacroLibraryExportWriter.WriteZip(otherBundle, otherZip, "另一个导出", "主宏", "依赖子宏", MacroConversionFormat.MacroHidMcrx);
        var data = new DataObject(DataFormats.FileDrop, new[] { zipPath, looseMacro, zipPath, otherZip });
        var paths = MainWindow.GetDroppedMacroFiles(data);
        if (!paths.SequenceEqual(new[] { zipPath, looseMacro, otherZip }))
            throw new Exception("Mixed drops must accept uppercase ZIP and loose macro files without importing the same ZIP twice.");
        VerifyShellDrop(paths);

        var before = store.Load().Items.Select(item => item.Id).ToHashSet();
        panel.ImportMacroFiles(paths);
        var imported = store.Load().Items.Where(item => !before.Contains(item.Id)).ToArray();
        if (imported.Length != 5 || imported.Any(item => item.GroupId != groupId || item.Folder != "Routes"))
            throw new Exception("Mixed drop must import the loose macro and each ZIP's pair into the active folder.");

        var loose = imported.Single(item => item.Name == "third");
        if (store.ReadMacro(loose.Id).Steps.Single() is not WaitStep { Duration.TotalMilliseconds: 30 }
            || imported.Count(item => item.Name == dependency.Name) != 2)
            throw new Exception("Mixed drop must preserve the loose macro and both copies of the archived dependency.");
        var callDurations = imported.Where(item => item.Name == primary.Name).Select(item =>
        {
            var call = (MacroCallStep)store.ReadMacro(item.Id).Steps.Single();
            var child = imported.Single(candidate => candidate.Id == call.Macro && candidate.Name == dependency.Name);
            return ((WaitStep)store.ReadMacro(child.Id).Steps.Single()).Duration.TotalMilliseconds;
        }).ToArray();
        if (!callDurations.SequenceEqual(new[] { 5d, 8d }))
            throw new Exception("Each ZIP macro must call the dependency from its own archive, even with matching source names and IDs.");
    }

    private static void VerifyInvalidZipEntries(string root)
    {
        var unsupported = Path.Combine(root, "empty.zip");
        WriteZipEntries(unsupported, ["folder/", "folder/readme.md", "folder/nested.zip"]);
        if (MacroImportFiles.ReadZip(unsupported).Count != 0)
            throw new Exception("ZIP reading must skip directories, unrelated file types and nested ZIPs.");

        var traversal = Path.Combine(root, "traversal.zip");
        WriteZipEntries(traversal, ["nested/../../escape.mcrx"]);
        ExpectInvalidZip(traversal);

        var duplicate = Path.Combine(root, "duplicate.zip");
        WriteZipEntries(duplicate, ["nested/test.mcrx", "NESTED/Test.MCRX"]);
        ExpectInvalidZip(duplicate);
    }

    private static void WriteZipEntries(string path, string[] names)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var name in names) zip.CreateEntry(name);
    }

    private static void ExpectInvalidZip(string path)
    {
        try { MacroImportFiles.ReadZip(path); }
        catch (InvalidDataException) { return; }
        throw new Exception($"ZIP reader did not reject {Path.GetFileName(path)}.");
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
