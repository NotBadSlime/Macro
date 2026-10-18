namespace MacroHid.Core;

/// <summary>
/// The parallel card was removed: only one macro (one timeline) runs at a time. Macros that still contain one open and can be
/// edited, but are not played; this finds the macro that holds one so the user can delete it.
/// </summary>
public static class ParallelCards
{
    /// <summary>The name of <paramref name="document"/> or a macro it calls that contains a parallel card, or null.</summary>
    public static string? FindIn(MacroDocument document, Func<string, MacroDocument?>? resolver)
    {
        var pending = new Queue<MacroDocument>([document]);
        var seen = new HashSet<MacroDocument>(ReferenceEqualityComparer.Instance);
        while (pending.TryDequeue(out var current) && seen.Count < 4096)
        {
            if (!seen.Add(current)) continue;
            if (Contains(current.Steps) || current.EffectiveConditions.Any(c => Contains(c.ThenSteps))) return current.Name;
            if (resolver is null) continue;
            foreach (var name in MacroCallReferenceCollector.Collect(current))
                if (resolver(name) is { } target) pending.Enqueue(target);
        }
        return null;
    }

    public static bool Contains(IReadOnlyList<MacroStep> steps)
    {
        foreach (var step in steps)
        {
            switch (step)
            {
                case ParallelStep or ParallelBranchStep:
                    return true;
                case RepeatStep repeat when Contains(repeat.Steps):
                case PixelWhenStep pixel when Contains(pixel.ThenSteps):
                case TextWhenStep text when Contains(text.ThenSteps):
                    return true;
            }
        }
        return false;
    }
}
