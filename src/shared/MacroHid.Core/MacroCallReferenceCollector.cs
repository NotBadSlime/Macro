namespace MacroHid.Core;

public static class MacroCallReferenceCollector
{
    public static IReadOnlyList<string> Collect(MacroDocument document)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectSteps(document.Steps, set);
        foreach (var condition in document.EffectiveConditions)
        {
            CollectSteps(condition.ThenSteps, set);
        }

        return set.ToList();
    }

    private static void CollectSteps(IReadOnlyList<MacroStep> steps, HashSet<string> set)
    {
        foreach (var step in steps)
        {
            switch (step)
            {
                case MacroCallStep call when !string.IsNullOrWhiteSpace(call.Macro):
                    set.Add(call.Macro.Trim());
                    break;
                case RepeatStep repeat:
                    CollectSteps(repeat.Steps, set);
                    break;
                case PixelWhenStep pixel:
                    CollectSteps(pixel.ThenSteps, set);
                    break;
            }
        }
    }
}
