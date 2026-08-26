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

    private static IReadOnlyList<MacroStep> RemapSteps(
        IReadOnlyList<MacroStep> steps,
        IReadOnlyDictionary<string, string> idMap)
    {
        return steps.Select(step => step switch
        {
            MacroCallStep call when idMap.TryGetValue(call.Macro, out var mapped) => call with { Macro = mapped },
            RepeatStep repeat => repeat with { Steps = RemapSteps(repeat.Steps, idMap) },
            PixelWhenStep pixel => pixel with { ThenSteps = RemapSteps(pixel.ThenSteps, idMap) },
            _ => step
        }).ToList();
    }
}
