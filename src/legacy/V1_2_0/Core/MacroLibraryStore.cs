using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace MacroHid.Core;

public sealed record MacroLibraryItem(
    string Id,
    string Name,
    string Folder,
    string FileName,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<string>? Aliases = null,
    string GroupId = MacroLibraryStore.GlobalGroupId)
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

public sealed record MacroLibraryGroup(
    string Id,
    string Name,
    string ProcessFilter,
    bool IsGlobal = false);

public sealed record MacroLibraryFolder(
    string GroupId,
    string Name);

public sealed record MacroLibrarySnapshot(
    IReadOnlyList<MacroLibraryItem> Items,
    string? SelectedMacroId,
    IReadOnlyList<string> Folders,
    IReadOnlyList<MacroLibraryGroup> Groups,
    IReadOnlyList<MacroLibraryFolder> GroupFolders);

public sealed class MacroLibraryStore
{
    public const string GlobalGroupId = "global";

    private const string GlobalGroupName = "全局";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
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
        return new MacroLibrarySnapshot(
            index.Items.AsReadOnly(),
            index.SelectedMacroId,
            index.Folders.AsReadOnly(),
            index.Groups.AsReadOnly(),
            index.GroupFolders.AsReadOnly());
    }

    public MacroLibraryItem CreateMacro(string name, string? folder = null, IReadOnlyList<MacroStep>? steps = null, string? groupId = null)
    {
        var document = new MacroDocument(1, NormalizeName(name), PlaybackSettings.Default, steps ?? []);
        return CreateMacro(document, folder, groupId: groupId);
    }

    public MacroLibraryItem CreateMacro(MacroDocument document, string? folder = null, IReadOnlyList<string>? aliases = null, string? groupId = null)
    {
        Directory.CreateDirectory(rootDirectory);

        var index = LoadIndex();
        var id = Guid.NewGuid().ToString("N");
        var normalizedGroupId = NormalizeGroupId(groupId);
        EnsureGroupExists(index, normalizedGroupId);
        var item = new MacroLibraryItem(
            id,
            NormalizeName(document.Name),
            NormalizeFolder(folder),
            CreateFileName(document.Name, id),
            DateTimeOffset.UtcNow,
            NormalizeAliases(aliases),
            normalizedGroupId);

        EnsureGroupFolder(index, item.GroupId, item.Folder);
        index.Items.Add(item);
        index.SelectedMacroId = item.Id;
        SaveDocumentFile(item, document with { Name = item.Name });
        SaveIndex(index);
        return item;
    }

    public MacroLibraryGroup CreateGroup(string name, string? processFilter = null)
    {
        var index = LoadIndex();
        var group = new MacroLibraryGroup(
            Guid.NewGuid().ToString("N"),
            NormalizeName(name),
            NormalizeProcessFilter(processFilter));
        index.Groups.Add(group);
        SaveIndex(index);
        return group;
    }

    public MacroLibraryGroup UpdateGroup(string id, string name, string? processFilter = null)
    {
        var index = LoadIndex();
        var groupIndex = index.Groups.FindIndex(group => string.Equals(group.Id, id, StringComparison.OrdinalIgnoreCase));
        if (groupIndex < 0)
        {
            throw new KeyNotFoundException($"Macro group '{id}' was not found.");
        }

        var previous = index.Groups[groupIndex];
        var updated = previous with
        {
            Name = NormalizeName(name),
            ProcessFilter = previous.IsGlobal ? string.Empty : NormalizeProcessFilter(processFilter)
        };
        index.Groups[groupIndex] = updated;
        SaveIndex(index);
        return updated;
    }

    public void DeleteGroup(string id)
    {
        var index = LoadIndex();
        var group = FindGroup(index, id);
        if (group.IsGlobal)
        {
            return;
        }

        index.Groups.RemoveAll(candidate => string.Equals(candidate.Id, group.Id, StringComparison.OrdinalIgnoreCase));
        index.GroupFolders.RemoveAll(folder => string.Equals(folder.GroupId, group.Id, StringComparison.OrdinalIgnoreCase));
        for (var i = 0; i < index.Items.Count; i++)
        {
            if (!string.Equals(index.Items[i].GroupId, group.Id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            index.Items[i] = index.Items[i] with
            {
                GroupId = GlobalGroupId,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            EnsureGroupFolder(index, GlobalGroupId, index.Items[i].Folder);
        }

        SaveIndex(index);
    }

    public void CreateFolder(string folder, string? groupId = null)
    {
        var normalized = NormalizeFolder(folder);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        var index = LoadIndex();
        EnsureGroupFolder(index, NormalizeGroupId(groupId), normalized);
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
        return CreateMacro(document with { Name = NormalizeName(newName) }, source.Folder, groupId: source.GroupId);
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

    public MacroLibraryItem MoveMacro(string id, string? folder, string? beforeMacroId = null, string? groupId = null)
    {
        var index = LoadIndex();
        var itemIndex = index.Items.FindIndex(item => item.Id == id);
        if (itemIndex < 0)
        {
            throw new KeyNotFoundException($"Macro '{id}' was not found.");
        }

        var previous = index.Items[itemIndex];
        index.Items.RemoveAt(itemIndex);
        var normalizedGroupId = NormalizeGroupId(groupId ?? previous.GroupId);
        EnsureGroupExists(index, normalizedGroupId);
        var normalizedFolder = NormalizeFolder(folder);
        EnsureGroupFolder(index, normalizedGroupId, normalizedFolder);
        var updated = previous with
        {
            Folder = normalizedFolder,
            GroupId = normalizedGroupId,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        index.Items.Insert(GetMoveInsertIndex(index, normalizedGroupId, normalizedFolder, beforeMacroId), updated);
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

    public void DeleteFolder(string folder, bool deleteMacros, string? groupId = null)
    {
        var normalized = NormalizeFolder(folder);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        var index = LoadIndex();
        var normalizedGroupId = NormalizeGroupId(groupId);
        index.GroupFolders.RemoveAll(candidate =>
            string.Equals(candidate.GroupId, normalizedGroupId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.Name, normalized, StringComparison.Ordinal));

        if (deleteMacros)
        {
            var removed = index.Items
                .Where(item => string.Equals(item.GroupId, normalizedGroupId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(item.Folder, normalized, StringComparison.Ordinal))
                .ToList();
            foreach (var item in removed)
            {
                var path = GetMacroPath(item);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }

            index.Items.RemoveAll(item => string.Equals(item.GroupId, normalizedGroupId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.Folder, normalized, StringComparison.Ordinal));
            if (index.SelectedMacroId is not null && removed.Any(item => item.Id == index.SelectedMacroId))
            {
                index.SelectedMacroId = index.Items.FirstOrDefault()?.Id;
            }
        }
        else
        {
            for (var i = 0; i < index.Items.Count; i++)
            {
                if (string.Equals(index.Items[i].GroupId, normalizedGroupId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(index.Items[i].Folder, normalized, StringComparison.Ordinal))
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

    public void RenameFolder(string oldFolder, string newFolder, string? groupId = null)
    {
        var oldName = NormalizeFolder(oldFolder);
        var newName = NormalizeFolder(newFolder);
        if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName) || string.Equals(oldName, newName, StringComparison.Ordinal))
        {
            return;
        }

        var index = LoadIndex();
        var normalizedGroupId = NormalizeGroupId(groupId);
        index.GroupFolders.RemoveAll(folder =>
            string.Equals(folder.GroupId, normalizedGroupId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(folder.Name, oldName, StringComparison.Ordinal));
        EnsureGroupFolder(index, normalizedGroupId, newName);
        for (var i = 0; i < index.Items.Count; i++)
        {
            if (string.Equals(index.Items[i].GroupId, normalizedGroupId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(index.Items[i].Folder, oldName, StringComparison.Ordinal))
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
            var empty = new MacroLibraryIndex();
            NormalizeIndex(empty);
            return empty;
        }

        var index = JsonSerializer.Deserialize<MacroLibraryIndex>(File.ReadAllText(indexPath), Options)
            ?? new MacroLibraryIndex();
        NormalizeIndex(index);
        return index;
    }

    private static void NormalizeIndex(MacroLibraryIndex index)
    {
        index.Items ??= [];
        index.Folders ??= [];
        index.Groups ??= [];
        index.GroupFolders ??= [];
        var migrateLegacyFolders = index.Groups.Count == 0 && index.GroupFolders.Count == 0;
        EnsureGlobalGroup(index);

        foreach (var legacyFolder in migrateLegacyFolders ? index.Folders.ToList() : [])
        {
            EnsureGroupFolder(index, GlobalGroupId, legacyFolder);
        }

        for (var i = 0; i < index.Items.Count; i++)
        {
            var item = index.Items[i];
            var groupId = NormalizeGroupId(item.GroupId);
            if (!index.Groups.Any(group => string.Equals(group.Id, groupId, StringComparison.OrdinalIgnoreCase)))
            {
                groupId = GlobalGroupId;
            }

            if (!string.Equals(item.GroupId, groupId, StringComparison.Ordinal))
            {
                item = item with { GroupId = groupId };
                index.Items[i] = item;
            }

            EnsureGroupFolder(index, item.GroupId, item.Folder);
        }

        SyncLegacyFolders(index);
    }

    private void SaveIndex(MacroLibraryIndex index)
    {
        Directory.CreateDirectory(rootDirectory);
        NormalizeIndex(index);
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

    private static MacroLibraryGroup FindGroup(MacroLibraryIndex index, string id)
    {
        var normalized = NormalizeGroupId(id);
        return index.Groups.FirstOrDefault(group => string.Equals(group.Id, normalized, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Macro group '{id}' was not found.");
    }

    private static int GetMoveInsertIndex(MacroLibraryIndex index, string groupId, string folder, string? beforeMacroId)
    {
        if (!string.IsNullOrWhiteSpace(beforeMacroId))
        {
            var beforeIndex = index.Items.FindIndex(item =>
                string.Equals(item.Id, beforeMacroId, StringComparison.Ordinal)
                && string.Equals(item.GroupId, groupId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.Folder, folder, StringComparison.Ordinal));
            if (beforeIndex >= 0)
            {
                return beforeIndex;
            }
        }

        var lastInFolderIndex = index.Items.FindLastIndex(item =>
            string.Equals(item.GroupId, groupId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.Folder, folder, StringComparison.Ordinal));
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

    private static string NormalizeProcessFilter(string? processFilter)
    {
        return string.IsNullOrWhiteSpace(processFilter) ? string.Empty : processFilter.Trim();
    }

    private static string NormalizeGroupId(string? groupId)
    {
        return string.IsNullOrWhiteSpace(groupId) ? GlobalGroupId : groupId.Trim();
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

    private static void EnsureGlobalGroup(MacroLibraryIndex index)
    {
        var existingIndex = index.Groups.FindIndex(group => string.Equals(group.Id, GlobalGroupId, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
        {
            var existing = index.Groups[existingIndex];
            index.Groups[existingIndex] = existing with
            {
                Id = GlobalGroupId,
                Name = string.IsNullOrWhiteSpace(existing.Name) ? GlobalGroupName : existing.Name,
                ProcessFilter = string.Empty,
                IsGlobal = true
            };
            return;
        }

        index.Groups.Insert(0, new MacroLibraryGroup(GlobalGroupId, GlobalGroupName, string.Empty, true));
    }

    private static void EnsureGroupExists(MacroLibraryIndex index, string groupId)
    {
        EnsureGlobalGroup(index);
        if (index.Groups.Any(group => string.Equals(group.Id, groupId, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        throw new KeyNotFoundException($"Macro group '{groupId}' was not found.");
    }

    private static void EnsureGroupFolder(MacroLibraryIndex index, string groupId, string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        var normalizedGroupId = NormalizeGroupId(groupId);
        if (!index.GroupFolders.Any(candidate =>
            string.Equals(candidate.GroupId, normalizedGroupId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.Name, folder, StringComparison.Ordinal)))
        {
            index.GroupFolders.Add(new MacroLibraryFolder(normalizedGroupId, folder));
            index.GroupFolders.Sort((left, right) =>
            {
                var groupCompare = string.Compare(left.GroupId, right.GroupId, StringComparison.OrdinalIgnoreCase);
                return groupCompare != 0
                    ? groupCompare
                    : string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
            });
        }
    }

    private static void SyncLegacyFolders(MacroLibraryIndex index)
    {
        var folders = index.GroupFolders
            .Select(folder => folder.Name)
            .Concat(index.Items.Select(item => item.Folder))
            .Where(folder => !string.IsNullOrWhiteSpace(folder))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(folder => folder, StringComparer.OrdinalIgnoreCase)
            .ToList();

        index.Folders.Clear();
        index.Folders.AddRange(folders);
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

        public List<MacroLibraryGroup> Groups { get; set; } = [];

        public List<MacroLibraryFolder> GroupFolders { get; set; } = [];

        public string? SelectedMacroId { get; set; }
    }
}
