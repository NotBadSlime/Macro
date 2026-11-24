using System.Text.Json;

namespace MacroHid.Core;

public sealed record MacroLibraryDirectoryImportResult(
    int ImportedCount,
    int SkippedCount,
    int ImportedGroupCount,
    int ImportedFolderCount);

public sealed partial class MacroLibraryStore
{
    public MacroLibraryDirectoryImportResult? ImportLegacyLibraryIfNeeded(string sourceDirectory)
    {
        lock (SyncRoot)
        {
            var sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDirectory));
            var destinationRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
            var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (string.Equals(sourceRoot, destinationRoot, pathComparison))
            {
                throw new InvalidDataException("The source macro library cannot be the Studio library.");
            }

            var destinationIndexPath = Path.GetFullPath(indexPath);
            var destinationBytes = ReadImportIndex(destinationIndexPath);
            var destination = destinationBytes is null ? new MacroLibraryIndex() : ParseImportIndex(destinationBytes);
            if (destination.LegacyLibraryImported)
            {
                return null;
            }

            var sourceIndexPath = Path.Combine(sourceRoot, "library.json");
            var sourceBytes = ReadImportIndex(sourceIndexPath);
            if (sourceBytes is null)
            {
                return null;
            }

            var source = ParseImportIndex(sourceBytes);
            ValidateSourceItems(source);
            NormalizeIndex(source);
            if (source.Items.Count == 0)
            {
                return null;
            }

            NormalizeIndex(destination);
            var groupCount = destination.Groups.Count;
            var folderCount = destination.GroupFolders.Count;
            MergeImportGroups(source, destination);

            var destinationIds = destination.Items.Select(item => ImportIdentity(item.Id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var destinationNames = destination.Items.Select(item => item.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var imported = new List<ImportDocument>();
            foreach (var item in source.Items)
            {
                if (!destinationIds.Add(ImportIdentity(item.Id)))
                {
                    continue;
                }

                var fileName = ReserveImportFileName(destinationRoot, item.FileName, destinationNames);
                imported.Add(new ImportDocument(Path.Combine(sourceRoot, item.FileName), Path.Combine(destinationRoot, fileName)));
                destination.Items.Add(item with { FileName = fileName });
            }

            NormalizeIndex(destination);
            destination.LegacyLibraryImported = true;
            Directory.CreateDirectory(destinationRoot);
            CheckImportDirectory(destinationRoot);
            CommitLibraryImport(destinationIndexPath, destinationBytes, destination, sourceIndexPath, sourceBytes, imported);
            return new MacroLibraryDirectoryImportResult(imported.Count, source.Items.Count - imported.Count,
                destination.Groups.Count - groupCount, destination.GroupFolders.Count - folderCount);
        }
    }

    private static byte[]? ReadImportIndex(string path)
    {
        try
        {
            CheckImportPath(path);
            return File.ReadAllBytes(path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static MacroLibraryIndex ParseImportIndex(byte[] content)
    {
        try
        {
            using var json = JsonDocument.Parse(content.AsSpan().StartsWith("\uFEFF"u8)
                ? content.AsMemory(3) : content);
            if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.EnumerateObject().Any(
                property => property.Name.Equals(nameof(MacroLibraryIndex.Items), StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.Array))
            {
                throw new InvalidDataException("The selected folder does not contain a valid macro library catalog.");
            }

            return json.RootElement.Deserialize<MacroLibraryIndex>(Options)!;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The macro library catalog could not be read.", ex);
        }
    }

    private static void ValidateSourceItems(MacroLibraryIndex index)
    {
        foreach (var item in index.Items ?? [])
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.FileName)
                || item.FileName.IndexOfAny(['/', '\\', ':']) >= 0
                || item.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || !string.Equals(Path.GetExtension(item.FileName), ".mcrx", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The source macro library contains an invalid macro filename or identity.");
            }
        }

        if ((index.Groups?.Any(group => group is null) ?? false)
            || (index.GroupFolders?.Any(folder => folder is null) ?? false))
        {
            throw new InvalidDataException("The source macro library contains an invalid group or folder.");
        }
    }

    private static void MergeImportGroups(MacroLibraryIndex source, MacroLibraryIndex destination)
    {
        var groupIds = destination.Groups.Select(group => group.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var group in source.Groups)
        {
            if (groupIds.Add(group.Id))
            {
                destination.Groups.Add(group);
            }
        }

        foreach (var folder in source.GroupFolders)
        {
            EnsureGroupFolder(destination, folder.GroupId, folder.Name);
        }
    }

    private static string ImportIdentity(string id) => Guid.TryParse(id, out var guid) ? guid.ToString("N") : id;

    private static string ReserveImportFileName(string root, string input, HashSet<string> names)
    {
        var candidate = input;
        while (Path.Exists(Path.Combine(root, candidate)) || !names.Add(candidate))
        {
            candidate = $"macro-{Guid.NewGuid():N}.mcrx";
        }

        return candidate;
    }

    private static void CheckImportPath(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory | FileAttributes.Device)) != 0)
        {
            throw new InvalidDataException("A macro library entry is a directory or a link.");
        }

        CheckImportDirectory(Path.GetDirectoryName(path));
    }

    private static void CheckImportDirectory(string? directory)
    {
        for (; directory is not null; directory = Path.GetDirectoryName(directory))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("A macro library directory is a link.");
            }
        }
    }

    private static void CommitLibraryImport(string destinationIndexPath, byte[]? destinationBytes,
        MacroLibraryIndex destination, string sourceIndexPath, byte[] sourceBytes, List<ImportDocument> documents)
    {
        var destinationRoot = Path.GetDirectoryName(destinationIndexPath)!;
        var pendingIndex = Path.Combine(destinationRoot, $".library-import-{Guid.NewGuid():N}.tmp");
        var pending = new List<(string Stage, string Destination)>();
        var moved = new List<string>();
        try
        {
            foreach (var document in documents)
            {
                CheckImportPath(document.Source);
                var stagingPath = Path.Combine(destinationRoot, $".macro-import-{Guid.NewGuid():N}.tmp");
                using (var staging = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write))
                {
                    pending.Add((stagingPath, document.Destination));
                    using var source = File.OpenRead(document.Source);
                    source.CopyTo(staging);
                }

                try
                {
                    _ = McrxParser.Parse(File.ReadAllText(stagingPath));
                }
                catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or OverflowException)
                {
                    throw new InvalidDataException($"The source macro '{Path.GetFileName(document.Source)}' could not be read.", ex);
                }
            }

            File.WriteAllText(pendingIndex, JsonSerializer.Serialize(destination, Options));
            foreach (var (stage, target) in pending)
            {
                File.Move(stage, target);
                moved.Add(target);
            }

            if (!SameIndex(destinationBytes, ReadImportIndex(destinationIndexPath))
                || !SameIndex(sourceBytes, ReadImportIndex(sourceIndexPath)))
            {
                throw new IOException("A macro library catalog changed during the import.");
            }

            if (destinationBytes is null)
            {
                File.Move(pendingIndex, destinationIndexPath);
            }
            else
            {
                File.Replace(pendingIndex, destinationIndexPath, null);
            }
        }
        catch
        {
            DeleteImportFiles(moved.Concat(pending.Select(document => document.Stage)).Append(pendingIndex));
            throw;
        }
    }

    private static bool SameIndex(byte[]? original, byte[]? current) => original is null
        ? current is null
        : current is not null && original.AsSpan().SequenceEqual(current);

    private static void DeleteImportFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed record ImportDocument(string Source, string Destination);
}
