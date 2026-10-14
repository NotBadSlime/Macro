using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;

namespace InputApiProbe;

/// <summary>Separate-process Win32 window. Records the QPC of the first WM_KEYDOWN / WM_KEYUP for the row the sender selected.</summary>
internal static class ReceiverProcess
{
    public const int Header = 64, Stride = 32;
    public const int Ready = 0, WindowHandle = 8, CurrentRow = 16, Done = 20;
    public const int DownQpc = 0, UpQpc = 8, DownCount = 16, UpCount = 20, DownVk = 24;

    public static long RowOffset(int row) => Header + (long)row * Stride;

    private static bool ParentExited(int pid)
    {
        try { using var parent = Process.GetProcessById(pid); return parent.HasExited; }
        catch (ArgumentException) { return true; }
    }

    public static void Run(string name, int parentPid)
    {
        using var memory = MemoryMappedFile.OpenExisting(name);
        using var view = memory.CreateViewAccessor();
        // Normal priority: a real target app does not run above normal, so the receiver must not be artificially fast.
        using var window = new MessageWindow((message, w, l) =>
        {
            if (message is not (0x100 or 0x101)) return false; // WM_KEYDOWN / WM_KEYUP
            long tick = Stopwatch.GetTimestamp();
            int row = view.ReadInt32(CurrentRow);
            if (row < 0) return false;
            bool down = message == 0x100;
            long at = RowOffset(row);
            long countAt = at + (down ? DownCount : UpCount);
            int previous = view.ReadInt32(countAt);
            view.Write(countAt, previous + 1);
            if (previous == 0)
            {
                view.Write(at + (down ? DownQpc : UpQpc), tick);
                if (down) view.Write(at + DownVk, (int)w.ToInt64());
            }
            return false;
        }, visible: true, title: "MacroHID input API probe receiver", onCreated: hwnd =>
        {
            view.Write(WindowHandle, hwnd.ToInt64());
            view.Write(Ready, 1);
            Native.SetForegroundWindow(hwnd);
        }, priority: ThreadPriority.Normal);
        while (view.ReadInt32(Done) == 0 && !ParentExited(parentPid)) Thread.Sleep(100);
    }
}
