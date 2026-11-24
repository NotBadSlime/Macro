using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MacroHid.Core;

public sealed record MacroLibraryRevision(string Id, string FileName, DateTimeOffset UpdatedAt, bool IsLocked, string ContentHash);

public sealed class MacroLibraryConflictException : IOException
{
    internal MacroLibraryConflictException()
        : base("The shared macro library changed before the edit could be saved.")
    {
    }
}

public sealed partial class MacroLibraryStore
{
    public MacroLibraryRevision? GetRevision(string id)
    {
        lock (SyncRoot)
        {
            var item = LoadIndex().Items.FirstOrDefault(candidate => candidate.Id == id);
            if (item is null) return null;

            var content = ReadSharedFile(GetMacroPath(item));
            return content is null ? null : CreateRevision(item, content);
        }
    }

    private static MacroLibraryRevision CreateRevision(MacroLibraryItem item, string content) =>
        new(item.Id, item.FileName, item.UpdatedAt, item.IsLocked, HashContent(content));

    private static bool MatchesRevision(MacroLibraryItem item, MacroLibraryRevision revision) =>
        item.Id == revision.Id && item.FileName == revision.FileName && item.UpdatedAt == revision.UpdatedAt
        && item.IsLocked == revision.IsLocked;

    private static string HashContent(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private MacroLibraryIndex ReadSharedIndex()
    {
        // An older app writes its catalog in place. Retry a transient incomplete read without writing the catalog.
        for (var attempt = 0; ; attempt++)
        {
            var content = ReadSharedFile(indexPath);
            try
            {
                var index = content is null ? new MacroLibraryIndex() : JsonSerializer.Deserialize<MacroLibraryIndex>(content, Options)
                    ?? throw new JsonException("The shared macro library catalog is empty.");
                NormalizeIndex(index);
                index.SourceText = content;
                return index;
            }
            catch (JsonException) when (attempt < 4)
            {
                Thread.Sleep(25);
            }
        }
    }

    private static string? ReadSharedFile(string path)
    {
        using var reader = OpenSharedFile(path);
        return reader?.ReadToEnd();
    }

    private static StreamReader? OpenSharedFile(string path)
    {
        // Permit atomic replacement, but an in-place writer must wait while a checked read is in progress.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete));
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            catch (IOException ex) when (IsSharingViolation(ex) && attempt < 4)
            {
                Thread.Sleep(25);
            }
        }
    }

    private void WriteSharedIndex(MacroLibraryIndex index, string catalog, MacroLibraryItem? item,
        string? document, MacroLibraryRevision? expectedRevision)
    {
        // Stage complete files before taking the existing catalog out of in-place write sharing.
        var stagedIndexPath = NewStagingPath();
        var stagedMacroPath = document is null ? null : NewStagingPath();
        try
        {
            File.WriteAllText(stagedIndexPath, catalog);
            if (stagedMacroPath is not null) File.WriteAllText(stagedMacroPath, document!);

            using var indexReader = OpenSharedFile(indexPath);
            if (!string.Equals(indexReader?.ReadToEnd(), index.SourceText, StringComparison.Ordinal))
            {
                throw new MacroLibraryConflictException();
            }

            if (item is null || stagedMacroPath is null)
            {
                MoveSharedFile(stagedIndexPath, indexPath, overwrite: indexReader is not null);
                return;
            }

            var macroPath = GetMacroPath(item);
            using var oldMacro = OpenSharedFile(macroPath);
            var previous = oldMacro?.ReadToEnd();
            if (expectedRevision is not null && (previous is null || HashContent(previous) != expectedRevision.ContentHash))
            {
                throw new MacroLibraryConflictException();
            }

            // Keep the published document out of in-place write sharing until the new catalog is visible.
            using var pendingDocument = OpenSharedFile(stagedMacroPath);
            MoveSharedFile(stagedMacroPath, macroPath, overwrite: oldMacro is not null);
            try
            {
                MoveSharedFile(stagedIndexPath, indexPath, overwrite: indexReader is not null);
            }
            catch
            {
                if (previous is null) File.Delete(macroPath);
                else
                {
                    File.WriteAllText(stagedMacroPath, previous);
                    MoveSharedFile(stagedMacroPath, macroPath, overwrite: true);
                }
                throw;
            }
        }
        finally
        {
            File.Delete(stagedIndexPath);
            if (stagedMacroPath is not null) File.Delete(stagedMacroPath);
        }
    }

    private string NewStagingPath() => Path.Combine(rootDirectory, $".library-write-{Guid.NewGuid():N}.tmp");

    private static void MoveSharedFile(string source, string destination, bool overwrite)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (OperatingSystem.IsWindows() && overwrite)
                {
                    ReplaceOpenFileOnWindows(source, destination);
                }
                else
                {
                    File.Move(source, destination, overwrite);
                }
                return;
            }
            catch (IOException ex) when (IsSharingViolation(ex) && attempt < 4)
            {
                Thread.Sleep(25);
            }
        }
    }

    private static void ReplaceOpenFileOnWindows(string source, string destination)
    {
        const uint deleteAccess = 0x10000;
        const int openExisting = 3;
        const int fileRenameInfoEx = 22;
        const int replaceIfExists = 1;
        const int posixSemantics = 2;

        // POSIX replacement lets readers of the old file finish while the name points to the new file.
        // Keeping our read handle open until the replacement also prevents in-place writers from racing the check.
        using var handle = OpenFileForRename(Path.GetFullPath(source), deleteAccess, FileShare.Read | FileShare.Delete,
            IntPtr.Zero, openExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException("Could not open the staged library file for replacement.", Marshal.GetHRForLastWin32Error());

        var targetName = Encoding.Unicode.GetBytes(Path.GetFullPath(destination));
        var lengthOffset = IntPtr.Size * 2; // FILE_RENAME_INFO.Flags, aligned RootDirectory, then FileNameLength.
        var nameOffset = lengthOffset + sizeof(int);
        var rename = new byte[nameOffset + targetName.Length];
        BitConverter.TryWriteBytes(rename.AsSpan(), replaceIfExists | posixSemantics);
        BitConverter.TryWriteBytes(rename.AsSpan(lengthOffset), targetName.Length);
        targetName.CopyTo(rename, nameOffset);
        if (!SetFileInformationByHandle(handle, fileRenameInfoEx, rename, rename.Length))
        {
            throw new IOException("Could not replace the shared library file.", Marshal.GetHRForLastWin32Error());
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle OpenFileForRename(string path, uint access, FileShare share, IntPtr attributes,
        int creationDisposition, int flags, IntPtr template);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, byte[] info, int length);

    private static bool IsSharingViolation(IOException ex) => (ex.HResult & 0xFFFF) is 32 or 33;
}
