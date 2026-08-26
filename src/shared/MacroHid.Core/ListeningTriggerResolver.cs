namespace MacroHid.Core;

public static class ListeningTriggerResolver
{
    public static IReadOnlyList<T> Resolve<T>(
        IReadOnlyList<T> candidates,
        Func<T, string> getId,
        Func<T, T, bool> conflicts,
        IReadOnlyCollection<string>? preferredIds = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(getId);
        ArgumentNullException.ThrowIfNull(conflicts);

        if (candidates.Count <= 1)
        {
            return candidates;
        }

        var preferred = preferredIds is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : preferredIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var others = new List<T>();
        var preferredCandidates = new List<T>();
        foreach (var candidate in candidates)
        {
            if (preferred.Contains(getId(candidate)))
            {
                preferredCandidates.Add(candidate);
            }
            else
            {
                others.Add(candidate);
            }
        }

        var ordered = others.Concat(preferredCandidates).ToList();
        var kept = new List<T>();
        for (var i = ordered.Count - 1; i >= 0; i--)
        {
            var candidate = ordered[i];
            var conflictsWithKept = false;
            foreach (var existing in kept)
            {
                if (conflicts(existing, candidate))
                {
                    conflictsWithKept = true;
                    break;
                }
            }

            if (conflictsWithKept)
            {
                continue;
            }

            kept.Add(candidate);
        }

        var keptIds = new HashSet<string>(kept.Select(getId), StringComparer.OrdinalIgnoreCase);
        return candidates.Where(candidate => keptIds.Contains(getId(candidate))).ToList();
    }
}
