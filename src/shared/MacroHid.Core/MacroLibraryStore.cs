using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace MacroHid.Core;

public sealed record MacroLibraryItem(
    string Id,
    string Name,
    string Folder,
    string FileName,
    DateTimeOffset UpdatedAt);

public sealed record MacroLibrarySnapshot(IReadOnlyList<MacroLibraryItem> Items, string? SelectedMacroId);

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
        return new MacroLibrarySnapshot(index.Items.AsReadOnly(), index.SelectedMacroId);
    }

    public MacroLibraryItem CreateMacro(string name, string? folder = null, IReadOnlyList<MacroStep>? steps = null)
    {
        var document = new MacroDocument(1, NormalizeName(name), PlaybackSettings.Default, steps ?? []);
        return CreateMacro(document, folder);
    }

    public MacroLibraryItem CreateMacro(MacroDocument document, string? folder = null)
    {
        Directory.CreateDirectory(rootDirectory);

        var index = LoadIndex();
        var id = Guid.NewGuid().ToString("N");
        var item = new MacroLibraryItem(
            id,
            NormalizeName(document.Name),
            NormalizeFolder(folder),
            CreateFileName(document.Name, id),
            DateTimeOffset.UtcNow);

        index.Items.Add(item);
        index.SelectedMacroId = item.Id;
        SaveDocumentFile(item, document with { Name = item.Name });
        SaveIndex(index);
        return item;
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

    public MacroLibraryItem MoveMacro(string id, string? folder)
    {
        var index = LoadIndex();
        var itemIndex = index.Items.FindIndex(item => item.Id == id);
        if (itemIndex < 0)
        {
            throw new KeyNotFoundException($"Macro '{id}' was not found.");
        }

        var updated = index.Items[itemIndex] with
        {
            Folder = NormalizeFolder(folder),
            UpdatedAt = DateTimeOffset.UtcNow
        };
        index.Items[itemIndex] = updated;
        SaveIndex(index);
        return updated;
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

    private static string NormalizeName(string? name)
    {
        return string.IsNullOrWhiteSpace(name) ? "Macro" : name.Trim();
    }

    private static string NormalizeFolder(string? folder)
    {
        return string.IsNullOrWhiteSpace(folder) ? string.Empty : folder.Trim();
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

        public string? SelectedMacroId { get; set; }
    }
}
