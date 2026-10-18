namespace MacroHid.Core;

public static class ConditionPackCloner
{
    public static IReadOnlyList<ConditionalDirective> CloneWithNewIds(IReadOnlyList<ConditionalDirective> conditions)
    {
        if (conditions.Count == 0)
        {
            return [];
        }

        return conditions
            .Select(condition => condition with { Id = ConditionalDirective.NewId() })
            .ToArray();
    }
}
