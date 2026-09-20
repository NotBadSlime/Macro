namespace MacroHid.Core;

public static class MacroCallRewriter
{
    public static MacroDocument RemapReferences(
        MacroDocument document,
        IReadOnlyDictionary<string, string> idMap)
    {
        if (idMap.Count == 0)
        {
            return document;
        }

        return document with
        {
            Steps = RemapSteps(document.Steps, idMap),
            Conditions = document.Conditions is { Count: > 0 } conditions
                ? conditions.Select(condition => condition with
                {
                    ThenSteps = RemapSteps(condition.ThenSteps, idMap)
                }).ToList()
                : document.Conditions
        };
    }

    public static MacroDocument RewriteToLibraryNames(
        MacroDocument document,
        IEnumerable<MacroLibraryItem> items,
        string? preferredGroupId = null)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items.OrderBy(candidate =>
            string.Equals(candidate.GroupId, preferredGroupId, StringComparison.OrdinalIgnoreCase) ? 0 : 1))
        {
            foreach (var key in ReferenceKeys(item))
            {
                map.TryAdd(key, item.Name);
            }
        }

        return RemapReferences(document, map);
    }

    private static IReadOnlyList<MacroStep> RemapSteps(
        IReadOnlyList<MacroStep> steps,
        IReadOnlyDictionary<string, string> idMap)
    {
        return steps.Select(step => step switch
        {
            MacroCallStep call when TryMap(idMap, call.Macro, out var mapped) => call with { Macro = mapped },
            RepeatStep repeat => repeat with { Steps = RemapSteps(repeat.Steps, idMap) },
            PixelWhenStep pixel => pixel with { ThenSteps = RemapSteps(pixel.ThenSteps, idMap) },
            _ => step
        }).ToList();
    }

    private static bool TryMap(IReadOnlyDictionary<string, string> idMap, string source, out string mapped)
    {
        if (idMap.TryGetValue(source, out mapped!))
        {
            return true;
        }

        var trimmed = source.Trim();
        if (idMap.TryGetValue(trimmed, out mapped!))
        {
            return true;
        }

        if (Guid.TryParse(trimmed, out var guid))
        {
            if (idMap.TryGetValue(guid.ToString("D"), out mapped!))
            {
                return true;
            }

            if (idMap.TryGetValue(guid.ToString("N"), out mapped!))
            {
                return true;
            }
        }

        mapped = source;
        return false;
    }

    private static IEnumerable<string> ReferenceKeys(MacroLibraryItem item)
    {
        foreach (var key in Expand(item.Id))
        {
            yield return key;
        }

        foreach (var alias in item.Aliases ?? [])
        {
            foreach (var key in Expand(alias))
            {
                yield return key;
            }
        }
    }

    private static IEnumerable<string> Expand(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        var trimmed = value.Trim();
        yield return trimmed;
        if (!Guid.TryParse(trimmed, out var guid))
        {
            yield break;
        }

        yield return guid.ToString("D");
        yield return guid.ToString("N");
    }
}
