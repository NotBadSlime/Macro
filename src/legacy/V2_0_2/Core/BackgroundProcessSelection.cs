namespace MacroHid.Core;

public static class BackgroundProcessSelection
{
    public static string Resolve(string? userOverride, string? databaseProcessFilter)
    {
        var chosen = FirstProcessName(userOverride);
        return chosen.Length > 0 ? chosen : FirstProcessName(databaseProcessFilter);
    }

    public static bool RequiresUserChoice(string? userOverride, bool databaseIsGlobal, string? databaseProcessFilter)
    {
        if (FirstProcessName(userOverride).Length > 0)
        {
            return false;
        }

        return databaseIsGlobal || FirstProcessName(databaseProcessFilter).Length == 0;
    }

    public static string FirstProcessName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var first = value
            .Split([',', ';', '|', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(first))
        {
            return string.Empty;
        }

        var name = Path.GetFileName(first.Trim());
        if (name.Length == 0)
        {
            return string.Empty;
        }

        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
    }
}
