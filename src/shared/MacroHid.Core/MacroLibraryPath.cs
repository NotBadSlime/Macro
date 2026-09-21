namespace MacroHid.Core;

public sealed record MacroLibraryPathParts(
    string GroupKey,
    string Folder,
    string? MacroName);

public sealed record MacroLibraryLocation(
    string GroupId,
    string Folder,
    string? MacroId);

public static class MacroLibraryPath
{
    public const string Separator = " > ";

    public static string Format(string groupName, string folder, string? macroName)
    {
        var parts = new List<string> { groupName.Trim() };
        if (!string.IsNullOrWhiteSpace(folder))
        {
            parts.Add(folder.Trim());
        }

        if (!string.IsNullOrWhiteSpace(macroName))
        {
            parts.Add(macroName.Trim());
        }

        return string.Join(Separator, parts.Where(part => part.Length > 0));
    }

    public static MacroLibraryPathParts Parse(string? path)
    {
        var parts = Split(path);
        if (parts.Length == 0)
        {
            return new MacroLibraryPathParts(string.Empty, string.Empty, null);
        }

        if (parts.Length == 1)
        {
            return new MacroLibraryPathParts(parts[0], string.Empty, null);
        }

        if (parts.Length == 2)
        {
            return new MacroLibraryPathParts(parts[0], string.Empty, parts[1]);
        }

        return new MacroLibraryPathParts(parts[0], parts[1], parts[^1]);
    }

    public static MacroLibraryItem? ResolveMacro(MacroLibrarySnapshot snapshot, string? path)
    {
        var parsed = Parse(path);
        if (string.IsNullOrWhiteSpace(parsed.GroupKey) || string.IsNullOrWhiteSpace(parsed.MacroName))
        {
            return null;
        }

        var group = FindGroup(snapshot, parsed.GroupKey);
        if (group is null)
        {
            return null;
        }

        var folder = NormalizeFolder(snapshot, group.Id, parsed.Folder, parsed.MacroName);
        return snapshot.Items.FirstOrDefault(item =>
            string.Equals(item.GroupId, group.Id, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.Folder, folder, StringComparison.Ordinal)
            && string.Equals(item.Name, parsed.MacroName, StringComparison.CurrentCultureIgnoreCase));
    }

    public static MacroLibraryLocation? ResolveLocation(MacroLibrarySnapshot snapshot, string? path)
    {
        var parsed = Parse(path);
        if (string.IsNullOrWhiteSpace(parsed.GroupKey))
        {
            return null;
        }

        var group = FindGroup(snapshot, parsed.GroupKey);
        if (group is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(parsed.MacroName))
        {
            return new MacroLibraryLocation(group.Id, string.Empty, null);
        }

        var folder = NormalizeFolder(snapshot, group.Id, parsed.Folder, parsed.MacroName);
        var item = snapshot.Items.FirstOrDefault(candidate =>
            string.Equals(candidate.GroupId, group.Id, StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.Folder, folder, StringComparison.Ordinal)
            && string.Equals(candidate.Name, parsed.MacroName, StringComparison.CurrentCultureIgnoreCase));
        if (item is not null)
        {
            return new MacroLibraryLocation(group.Id, item.Folder, item.Id);
        }

        if (!string.IsNullOrWhiteSpace(parsed.Folder)
            || snapshot.GroupFolders.Any(candidate =>
                string.Equals(candidate.GroupId, group.Id, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.Name, parsed.MacroName, StringComparison.CurrentCultureIgnoreCase)))
        {
            return new MacroLibraryLocation(group.Id, string.IsNullOrWhiteSpace(folder) ? parsed.MacroName : folder, null);
        }

        return new MacroLibraryLocation(group.Id, string.Empty, null);
    }

    public static MacroLibraryGroup? FindGroup(MacroLibrarySnapshot snapshot, string key)
    {
        return snapshot.Groups.FirstOrDefault(group =>
                   string.Equals(group.Name, key, StringComparison.CurrentCultureIgnoreCase))
               ?? snapshot.Groups.FirstOrDefault(group =>
                   string.Equals(group.Id, key, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeFolder(
        MacroLibrarySnapshot snapshot,
        string groupId,
        string folder,
        string macroName)
    {
        if (!string.IsNullOrWhiteSpace(folder))
        {
            var match = snapshot.GroupFolders.FirstOrDefault(candidate =>
                string.Equals(candidate.GroupId, groupId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.Name, folder, StringComparison.CurrentCultureIgnoreCase));
            return match?.Name ?? folder;
        }

        var rootMacro = snapshot.Items.Any(item =>
            string.Equals(item.GroupId, groupId, StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(item.Folder)
            && string.Equals(item.Name, macroName, StringComparison.CurrentCultureIgnoreCase));
        if (rootMacro)
        {
            return string.Empty;
        }

        var folderNamedLikeMacro = snapshot.GroupFolders.FirstOrDefault(candidate =>
            string.Equals(candidate.GroupId, groupId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.Name, macroName, StringComparison.CurrentCultureIgnoreCase));
        return folderNamedLikeMacro?.Name ?? string.Empty;
    }

    private static string[] Split(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return [];
        }

        return path
            .Replace('\\', '/')
            .Split(['>', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
