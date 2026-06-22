using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace MacroHid.Core;

public sealed record MacroLibraryItem(
    string Id,
    string Name,
    string Folder,
    string FileName,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<string>? Aliases = null)
{
    public bool MatchesReference(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return false;
        }

        var normalized = reference.Trim();
        return string.Equals(Id, normalized, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Name, normalized, StringComparison.CurrentCultureIgnoreCase)
            || (Aliases?.Any(alias => string.Equals(alias, normalized, StringComparison.OrdinalIgnoreCase)) == true);
    }
}

public sealed record MacroLibrarySnapshot(
    IReadOnlyList<MacroLibraryItem> Items,
    string? SelectedMacroId,
    IReadOnlyList<string> Folders);

public sealed class MacroLibraryStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    private readonly string rootDirectory;
    private readonly string indexPath;

    public MacroLibraryStore(string rootDirectory)
    {
        this.rootDirectory = rootDirectory;
        indexPath = Path.Combine(rootDirectory, "library.json");
    }

    public static string GetDefaultRoot()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MacroHID",
            "MacroLibrary");
    }

    public MacroLibrarySnapshot Load()
    {
        var index = LoadIndex();
        return new MacroLibrarySnapshot(index.Items.AsReadOnly(), index.SelectedMacroId, index.Folders.AsReadOnly());
    }

    public MacroLibraryItem CreateMacro(string name, string? folder = null, IReadOnlyList<MacroStep>? steps = null)
    {
        var document = new MacroDocument(1, NormalizeName(name), PlaybackSettings.Default, steps ?? []);
        return CreateMacro(document, folder);
    }

    public MacroLibraryItem CreateMacro(MacroDocument document, string? folder = null, IReadOnlyList<string>? aliases = null)
    {
        Directory.CreateDirectory(rootDirectory);

        var index = LoadIndex();
        var id = Guid.NewGuid().ToString("N");
        var item = new MacroLibraryItem(
            id,
            NormalizeName(document.Name),
            NormalizeFolder(folder),
            CreateFileName(document.Name, id),
            DateTimeOffset.UtcNow,
            NormalizeAliases(aliases));

        EnsureFolder(index, item.Folder);
        index.Items.Add(item);
        index.SelectedMacroId = item.Id;
        SaveDocumentFile(item, document with { Name = item.Name });
        SaveIndex(index);
        return item;
    }

    public void CreateFolder(string folder)
    {
        var normalized = NormalizeFolder(folder);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        var index = LoadIndex();
        EnsureFolder(index, normalized);
        SaveIndex(index);
    }

    public MacroDocument ReadMacro(string id)
    {
        var item = FindItem(LoadIndex(), id);
        return McrxParser.Parse(File.ReadAllText(GetMacroPath(item)));
    }

    public MacroLibraryItem SaveMacro(string id, MacroDocument document)
    {
        var index = LoadIndex();
        var itemIndex = index.Items.FindIndex(item => item.Id == id);
        if (itemIndex < 0)
        {
            throw new KeyNotFoundException($"Macro '{id}' was not found.");
        }

        var previous = index.Items[itemIndex];
        var updated = previous with
        {
            Name = NormalizeName(document.Name),
            UpdatedAt = DateTimeOffset.UtcNow
        };
        index.Items[itemIndex] = updated;
        index.SelectedMacroId = updated.Id;
        SaveDocumentFile(updated, document with { Name = updated.Name });
        SaveIndex(index);
        return updated;
    }

    public MacroLibraryItem RenameMacro(string id, string newName)
    {
        var index = LoadIndex();
        var itemIndex = index.Items.FindIndex(item => item.Id == id);
        if (itemIndex < 0)
        {
            throw new KeyNotFoundException($"Macro '{id}' was not found.");
        }

        var updated = index.Items[itemIndex] with
        {
            Name = NormalizeName(newName),
            UpdatedAt = DateTimeOffset.UtcNow
        };
        index.Items[itemIndex] = updated;
        index.SelectedMacroId = updated.Id;

        var document = McrxParser.Parse(File.ReadAllText(GetMacroPath(updated)));
        SaveDocumentFile(updated, document with { Name = updated.Name });
        SaveIndex(index);
        return updated;
    }

    public MacroLibraryItem DuplicateMacro(string id, string newName)
    {
        var index = LoadIndex();
        var source = FindItem(index, id);
        var document = ReadMacro(id);
        return CreateMacro(document with { Name = NormalizeName(newName) }, source.Folder);
    }

    public void DeleteMacro(string id)
    {
        var index = LoadIndex();
        var item = FindItem(index, id);
        index.Items.RemoveAll(candidate => candidate.Id == id);

        var path = GetMacroPath(item);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        index.SelectedMacroId = index.Items.FirstOrDefault()?.Id;
        SaveIndex(index);
    }

    public MacroLibraryItem MoveMacro(string id, string? folder, string? beforeMacroId = null)
    {
        var index = LoadIndex();
        var itemIndex = index.Items.FindIndex(item => item.Id == id);
        if (itemIndex < 0)
        {
            throw new KeyNotFoundException($"Macro '{id}' was not found.");
        }

        var previous = index.Items[itemIndex];
        index.Items.RemoveAt(itemIndex);
        var normalizedFolder = NormalizeFolder(folder);
        EnsureFolder(index, normalizedFolder);
        var updated = previous with
        {
            Folder = normalizedFolder,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        index.Items.Insert(GetMoveInsertIndex(index, normalizedFolder, beforeMacroId), updated);
        SaveIndex(index);
        return updated;
    }

    public MacroLibraryItem AddAliasesToMacro(string id, IReadOnlyList<string> aliases)
    {
        var normalizedAliases = NormalizeAliases(aliases);
        if (normalizedAliases.Count == 0)
        {
            return FindItem(LoadIndex(), id);
        }

        var index = LoadIndex();
        var itemIndex = index.Items.FindIndex(item => item.Id == id);
        if (itemIndex < 0)
        {
            throw new KeyNotFoundException($"Macro '{id}' was not found.");
        }

        var existing = index.Items[itemIndex];
        var existingAliases = existing.Aliases ?? [];
        var updated = existing with
        {
            Aliases = NormalizeAliases([.. existingAliases, .. normalizedAliases]),
            UpdatedAt = DateTimeOffset.UtcNow
        };
        index.Items[itemIndex] = updated;
        SaveIndex(index);
        return updated;
    }

    public void DeleteFolder(string folder, bool deleteMacros)
    {
        var normalized = NormalizeFolder(folder);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        var index = LoadIndex();
        index.Folders.RemoveAll(candidate => string.Equals(candidate, normalized, StringComparison.Ordinal));

        if (deleteMacros)
        {
            var removed = index.Items
                .Where(item => string.Equals(item.Folder, normalized, StringComparison.Ordinal))
                .ToList();
            foreach (var item in removed)
            {
                var path = GetMacroPath(item);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }

            index.Items.RemoveAll(item => string.Equals(item.Folder, normalized, StringComparison.Ordinal));
            if (index.SelectedMacroId is not null && removed.Any(item => item.Id == index.SelectedMacroId))
            {
                index.SelectedMacroId = index.Items.FirstOrDefault()?.Id;
            }
        }
        else
        {
            for (var i = 0; i < index.Items.Count; i++)
            {
                if (string.Equals(index.Items[i].Folder, normalized, StringComparison.Ordinal))
                {
                    index.Items[i] = index.Items[i] with
                    {
                        Folder = string.Empty,
                        UpdatedAt = DateTimeOffset.UtcNow
                    };
                }
            }
        }

        SaveIndex(index);
    }

    public void RenameFolder(string oldFolder, string newFolder)
    {
        var oldName = NormalizeFolder(oldFolder);
        var newName = NormalizeFolder(newFolder);
        if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName) || string.Equals(oldName, newName, StringComparison.Ordinal))
        {
            return;
        }

        var index = LoadIndex();
        index.Folders.RemoveAll(folder => string.Equals(folder, oldName, StringComparison.Ordinal));
        EnsureFolder(index, newName);
        for (var i = 0; i < index.Items.Count; i++)
        {
            if (string.Equals(index.Items[i].Folder, oldName, StringComparison.Ordinal))
            {
                index.Items[i] = index.Items[i] with
                {
                    Folder = newName,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
            }
        }

        SaveIndex(index);
    }

    public void SetSelected(string? id)
    {
        var index = LoadIndex();
        if (id is not null)
        {
            _ = FindItem(index, id);
        }

        index.SelectedMacroId = id;
        SaveIndex(index);
    }

    private MacroLibraryIndex LoadIndex()
    {
        Directory.CreateDirectory(rootDirectory);
        if (!File.Exists(indexPath))
        {
            return new MacroLibraryIndex();
        }

        var index = JsonSerializer.Deserialize<MacroLibraryIndex>(File.ReadAllText(indexPath), Options)
            ?? new MacroLibraryIndex();
        index.Items ??= [];
        index.Folders ??= [];
        foreach (var item in index.Items)
        {
            EnsureFolder(index, item.Folder);
        }
        return index;
    }

    private void SaveIndex(MacroLibraryIndex index)
    {
        Directory.CreateDirectory(rootDirectory);
        File.WriteAllText(indexPath, JsonSerializer.Serialize(index, Options));
    }

    private void SaveDocumentFile(MacroLibraryItem item, MacroDocument document)
    {
        Directory.CreateDirectory(rootDirectory);
        File.WriteAllText(GetMacroPath(item), McrxSerializer.Serialize(document));
    }

    private string GetMacroPath(MacroLibraryItem item)
    {
        return Path.Combine(rootDirectory, item.FileName);
    }

    private static MacroLibraryItem FindItem(MacroLibraryIndex index, string id)
    {
        return index.Items.FirstOrDefault(item => item.Id == id)
            ?? throw new KeyNotFoundException($"Macro '{id}' was not found.");
    }

    private static int GetMoveInsertIndex(MacroLibraryIndex index, string folder, string? beforeMacroId)
    {
        if (!string.IsNullOrWhiteSpace(beforeMacroId))
        {
            var beforeIndex = index.Items.FindIndex(item =>
                string.Equals(item.Id, beforeMacroId, StringComparison.Ordinal)
                && string.Equals(item.Folder, folder, StringComparison.Ordinal));
            if (beforeIndex >= 0)
            {
                return beforeIndex;
            }
        }

        var lastInFolderIndex = index.Items.FindLastIndex(item => string.Equals(item.Folder, folder, StringComparison.Ordinal));
        return lastInFolderIndex >= 0 ? lastInFolderIndex + 1 : index.Items.Count;
    }

    private static string NormalizeName(string? name)
    {
        return string.IsNullOrWhiteSpace(name) ? "Macro" : name.Trim();
    }

    private static string NormalizeFolder(string? folder)
    {
        return string.IsNullOrWhiteSpace(folder) ? string.Empty : folder.Trim();
    }

    private static IReadOnlyList<string> NormalizeAliases(IReadOnlyList<string>? aliases)
    {
        if (aliases is null || aliases.Count == 0)
        {
            return [];
        }

        return aliases
            .Select(alias => alias.Trim())
            .Where(alias => !string.IsNullOrWhiteSpace(alias))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void EnsureFolder(MacroLibraryIndex index, string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        if (!index.Folders.Contains(folder, StringComparer.Ordinal))
        {
            index.Folders.Add(folder);
            index.Folders.Sort(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string CreateFileName(string name, string id)
    {
        var safeName = string.Join(
            "_",
            NormalizeName(name).Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (string.IsNullOrWhiteSpace(safeName))
        {
            safeName = "macro";
        }

        return $"{safeName}-{id}.mcrx";
    }

    private sealed class MacroLibraryIndex
    {
        public List<MacroLibraryItem> Items { get; set; } = [];

        public List<string> Folders { get; set; } = [];

        public string? SelectedMacroId { get; set; }
    }
}
