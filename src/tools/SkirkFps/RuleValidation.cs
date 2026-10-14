namespace SkirkFps;
public static class RuleValidation
{
    public static int ParseFps(FpsRule rule)
    {
        if (!int.TryParse(rule.Fps?.Trim(), out var fps) || fps is < 1 or > 1000)
            throw new InvalidOperationException("目标帧率必须是 1–1000 的整数。");
        return fps;
    }
    public static List<FpsRule> Validate(IEnumerable<FpsRule> rules, FpsRule restore)
    {
        var enabled = rules.Where(r => r.Enabled).ToList();
        if (enabled.Count == 0) throw new InvalidOperationException("请至少启用一条快捷键规则。");
        if (enabled.Count > 64) throw new InvalidOperationException("最多支持 64 条规则。");
        var unique = new HashSet<(uint, int)>();
        foreach (var rule in enabled.Append(restore))
        {
            if (rule.VirtualKey is <= 0 or > 254 || (rule.Modifiers & ~15u) != 0)
                throw new InvalidOperationException("请为启用的规则录入有效快捷键。");
            if (!unique.Add((rule.Modifiers, rule.VirtualKey))) throw new InvalidOperationException("快捷键重复：" + rule.HotkeyText);
            if (!ReferenceEquals(rule, restore)) ParseFps(rule);
        }
        return enabled;
    }
}
