using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using MacroHid.Runtime;

namespace MacroStudio;

public partial class MainWindow
{
    internal static string[] GetDroppedMacroFiles(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop)
            || data.GetData(DataFormats.FileDrop) is not string[] paths)
            return [];

        return GetSupportedMacroFiles(paths);
    }

    private static string[] GetSupportedMacroFiles(IEnumerable<string> paths)
    {
        return paths.Where(path => File.Exists(path) && Path.GetExtension(path).ToLowerInvariant()
                is ".mcrx" or ".xml" or ".lua" or ".xmbcs" or ".mq" or ".txt" or ".json")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void MainWindow_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Effects = !shutdownQueued && GetDroppedMacroFiles(e.Data).Length > 0
            && (e.AllowedEffects & DragDropEffects.Copy) != 0
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void MainWindow_PreviewDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        if (shutdownQueued || (e.AllowedEffects & DragDropEffects.Copy) == 0) return;
        var files = GetDroppedMacroFiles(e.Data);
        if (files.Length == 0) return;
        e.Effects = DragDropEffects.Copy;
        ImportDroppedMacroFiles(files);
    }

    private void ImportDroppedMacroFiles(string[] files)
    {
        if (shutdownQueued || files.Length == 0) return;
        try { LibraryPanel.ImportMacroFiles(files); }
        catch (Exception ex) { SetErrorStatus(ex); }
    }

    // Explorer normally runs at medium integrity while the manifest elevates MacroStudio.
    // OLE drops cannot cross that boundary; accept the Shell's file-only message instead.
    private const int ShellDropFilesMessage = 0x0233;

    private static void EnableShellFileDrop(IntPtr handle)
    {
        foreach (var message in new uint[] { ShellDropFilesMessage, 0x0049, 0x004A })
        {
            if (!ChangeWindowMessageFilterEx(handle, message, 1, IntPtr.Zero))
                AppLog.Error("FileDrop", $"Could not allow Shell message 0x{message:X}: {Marshal.GetLastWin32Error()}");
        }
        DragAcceptFiles(handle, true);
    }

    private void ReceiveShellFileDrop(IntPtr drop)
    {
        try
        {
            var files = ReadShellFileDrop(drop);
            // Finish the native message before import changes the UI or displays a dialog.
            Dispatcher.BeginInvoke(new Action(() => ImportDroppedMacroFiles(GetSupportedMacroFiles(files))));
        }
        finally { DragFinish(drop); }
    }

    internal static string[] ReadShellFileDrop(IntPtr drop)
    {
        var files = new List<string>();
        uint count = DragQueryFile(drop, uint.MaxValue, null, 0);
        for (uint index = 0; index < count; index++)
        {
            uint length = DragQueryFile(drop, index, null, 0);
            var path = new StringBuilder(checked((int)length + 1));
            if (DragQueryFile(drop, index, path, (uint)path.Capacity) > 0)
                files.Add(path.ToString());
        }
        return files.ToArray();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr window, uint message, uint action, IntPtr changeInfo);

    [DllImport("shell32.dll")]
    private static extern void DragAcceptFiles(IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool accept);

    [DllImport("shell32.dll", EntryPoint = "DragQueryFileW", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(IntPtr drop, uint index, StringBuilder? path, uint capacity);

    [DllImport("shell32.dll")]
    private static extern void DragFinish(IntPtr drop);
}
