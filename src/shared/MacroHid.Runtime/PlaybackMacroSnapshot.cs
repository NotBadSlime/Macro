using System.Runtime.CompilerServices;
using MacroHid.Core;

namespace MacroHid.Runtime;

internal sealed class PlaybackMacroSnapshot : IDisposable
{
    private static readonly AsyncLocal<Func<string, MacroDocument?>?> Active = new();
    // Documents are immutable records, so the macro names a document calls never change. Caching them keeps the per-run
    // tree walk (inside the hotkey-to-first-input path) off every trigger; the resolver itself is still asked every run.
    private static readonly ConditionalWeakTable<MacroDocument, IReadOnlyList<string>> References = new();
    private static readonly Func<string, MacroDocument?> NoCalls = _ => null;
    private readonly Func<string, MacroDocument?>? previous = Active.Value;
    internal static Func<string, MacroDocument?>? Resolver => Active.Value;

    private static IReadOnlyList<string> ReferencesOf(MacroDocument document) =>
        References.GetValue(document, MacroCallReferenceCollector.Collect);

    internal PlaybackMacroSnapshot(MacroDocument root, Func<string, MacroDocument?>? resolver)
    {
        if (Active.Value is not null) return;
        if (ReferencesOf(root).Count == 0)
        {
            Active.Value = NoCalls;
            return;
        }

        var documents = new Dictionary<string, MacroDocument>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<MacroDocument>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<MacroDocument>();
        queue.Enqueue(root);
        while (queue.TryDequeue(out var document))
        {
            if (!visited.Add(document)) continue;
            if (visited.Count > 4096) throw new InvalidOperationException("宏依赖数量超过安全上限。");
            foreach (var name in ReferencesOf(document))
            {
                if (documents.ContainsKey(name)) continue;
                var target = resolver?.Invoke(name) ?? throw new InvalidOperationException($"找不到调用的宏：{name}");
                documents.Add(name, target);
                queue.Enqueue(target);
            }
        }
        Active.Value = name => documents.GetValueOrDefault(name.Trim());
    }
    public void Dispose() => Active.Value = previous;
}
