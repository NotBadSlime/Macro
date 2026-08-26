namespace MacroHid.Core;

public sealed record MacroLibraryExportEntry(MacroLibraryItem Item, MacroDocument Document, string RelativePath);

public sealed record MacroLibraryExportBundle(
    IReadOnlyList<MacroLibraryExportEntry> Primary,
    IReadOnlyList<MacroLibraryExportEntry> Dependencies);

public static class MacroLibraryExportBundles
{
    public static MacroLibraryExportBundle FromFolder(
        MacroLibraryStore store,
        MacroLibrarySnapshot snapshot,
        string groupId,
        string folder)
    {
        var primaryItems = snapshot.Items
            .Where(item =>
                string.Equals(item.GroupId, groupId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.Folder, folder, StringComparison.Ordinal))
            .ToList();

        var primary = primaryItems
            .Select(item => new MacroLibraryExportEntry(item, store.ReadMacro(item.Id) with { Id = item.Id }, item.FileName))
            .ToList();

        var primaryIds = primaryItems.Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var deps = new Dictionary<string, MacroLibraryExportEntry>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>(primary.SelectMany(entry => MacroCallReferenceCollector.Collect(entry.Document)));
        var visitedRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (pending.TryDequeue(out var reference))
        {
            if (!visitedRefs.Add(reference)) continue;
            var item = snapshot.Items.FirstOrDefault(candidate => candidate.MatchesReference(reference));
            if (item is null || primaryIds.Contains(item.Id) || deps.ContainsKey(item.Id)) continue;
            var document = store.ReadMacro(item.Id) with { Id = item.Id };
            deps[item.Id] = new MacroLibraryExportEntry(item, document, item.FileName);
            foreach (var nested in MacroCallReferenceCollector.Collect(document))
            {
                pending.Enqueue(nested);
            }
        }

        return new MacroLibraryExportBundle(primary, deps.Values.ToList());
    }

    public static MacroLibraryExportBundle FromItems(
        MacroLibraryStore store,
        MacroLibrarySnapshot snapshot,
        IReadOnlyList<MacroLibraryItem> items,
        Func<MacroLibraryItem, MacroDocument>? readPrimaryDocument = null)
    {
        var primaryItems = items.ToList();
        var primary = primaryItems
            .Select(item =>
            {
                var document = readPrimaryDocument?.Invoke(item)
                    ?? (store.ReadMacro(item.Id) with { Id = item.Id });
                if (string.IsNullOrWhiteSpace(document.Id))
                {
                    document = document with { Id = item.Id };
                }

                return new MacroLibraryExportEntry(item, document, item.FileName);
            })
            .ToList();
        var primaryIds = primaryItems.Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var deps = new Dictionary<string, MacroLibraryExportEntry>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>(primary.SelectMany(entry => MacroCallReferenceCollector.Collect(entry.Document)));
        var visitedRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (pending.TryDequeue(out var reference))
        {
            if (!visitedRefs.Add(reference)) continue;
            var item = snapshot.Items.FirstOrDefault(candidate => candidate.MatchesReference(reference));
            if (item is null || primaryIds.Contains(item.Id) || deps.ContainsKey(item.Id)) continue;
            var document = store.ReadMacro(item.Id) with { Id = item.Id };
            deps[item.Id] = new MacroLibraryExportEntry(item, document, item.FileName);
            foreach (var nested in MacroCallReferenceCollector.Collect(document))
            {
                pending.Enqueue(nested);
            }
        }

        return new MacroLibraryExportBundle(primary, deps.Values.ToList());
    }
}
