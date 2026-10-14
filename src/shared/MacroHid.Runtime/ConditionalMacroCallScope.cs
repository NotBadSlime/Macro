using MacroHid.Core;

namespace MacroHid.Runtime;

internal sealed class ConditionalMacroCallScope : IDisposable
{
    private static readonly AsyncLocal<HashSet<string>?> Active = new();
    private readonly HashSet<string>? previous = Active.Value;
    private static string Key(MacroDocument document) => string.IsNullOrWhiteSpace(document.Id) ? "name:" + document.Name : "id:" + document.Id;
    internal ConditionalMacroCallScope(MacroDocument document)
    {
        var names = previous is null ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : new(previous, StringComparer.OrdinalIgnoreCase);
        names.Add(Key(document));
        Active.Value = names;
    }
    internal static bool Contains(MacroDocument document) => Active.Value?.Contains(Key(document)) == true;
    public void Dispose() => Active.Value = previous;
}
