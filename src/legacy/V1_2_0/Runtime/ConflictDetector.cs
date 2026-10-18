using MacroHid.Core;

namespace MacroHid.Runtime;

public sealed record ConflictWarning(
    string DirectiveId,
    string DirectiveName,
    HidKey ConflictKey,
    int MainStepIndex,
    int ConditionActionIndex);

public static class ConflictDetector
{
    public static IReadOnlyList<ConflictWarning> DetectConflicts(MacroDocument document)
    {
        var warnings = new List<ConflictWarning>();
        var conditions = document.EffectiveConditions;
        if (conditions.Count == 0) return warnings;

        var mainStepKeys = ExtractKeysPerStep(document.Steps);

        foreach (var cond in conditions)
        {
            var condKeys = ExtractAllKeys(cond.ThenSteps);
            if (condKeys.Count == 0) continue;

            for (int stepIdx = cond.StartStepIndex; stepIdx <= cond.EndStepIndex && stepIdx < mainStepKeys.Count; stepIdx++)
            {
                foreach (var mainKey in mainStepKeys[stepIdx])
                {
                    for (int condActionIdx = 0; condActionIdx < condKeys.Count; condActionIdx++)
                    {
                        if (condKeys[condActionIdx] == mainKey)
                        {
                            warnings.Add(new ConflictWarning(
                                cond.Id,
                                cond.Name,
                                mainKey,
                                stepIdx,
                                condActionIdx));
                        }
                    }
                }
            }
        }

        return warnings;
    }

    private static List<HashSet<HidKey>> ExtractKeysPerStep(IReadOnlyList<MacroStep> steps)
    {
        var result = new List<HashSet<HidKey>>();
        foreach (var step in steps)
        {
            var keys = new HashSet<HidKey>();
            CollectKeys(step, keys);
            result.Add(keys);
        }
        return result;
    }

    private static List<HidKey> ExtractAllKeys(IReadOnlyList<MacroStep> steps)
    {
        var keys = new List<HidKey>();
        foreach (var step in steps)
        {
            var set = new HashSet<HidKey>();
            CollectKeys(step, set);
            keys.AddRange(set);
        }
        return keys;
    }

    private static void CollectKeys(MacroStep step, HashSet<HidKey> keys)
    {
        switch (step)
        {
            case KeyStep key:
                if (key.Key != HidKey.None)
                    keys.Add(key.Key);
                break;
            case RepeatStep repeat:
                foreach (var s in repeat.Steps)
                    CollectKeys(s, keys);
                break;
            case PixelWhenStep pixel:
                foreach (var s in pixel.ThenSteps)
                    CollectKeys(s, keys);
                break;
        }
    }

    public static string FormatWarnings(IReadOnlyList<ConflictWarning> warnings)
    {
        if (warnings.Count == 0) return string.Empty;

        var lines = new List<string> { $"检测到 {warnings.Count} 个按键冲突:" };
        foreach (var w in warnings)
        {
            lines.Add($"  [{w.DirectiveName}] 键 '{w.ConflictKey}' 与主序列步骤 #{w.MainStepIndex + 1} 冲突");
        }
        return string.Join(Environment.NewLine, lines);
    }
}
