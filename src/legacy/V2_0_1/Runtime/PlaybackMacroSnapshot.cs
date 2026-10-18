using MacroHid.Core;

namespace MacroHid.Runtime;

internal sealed class PlaybackMacroSnapshot : IDisposable
{
    private static readonly AsyncLocal<Func<string, MacroDocument?>?> Active = new();
    private readonly Func<string, MacroDocument?>? previous = Active.Value;
    internal static Func<string, MacroDocument?>? Resolver => Active.Value;

    internal PlaybackMacroSnapshot(MacroDocument root, Func<string, MacroDocument?>? resolver)
    {
        if (Active.Value is not null) return;
        var documents = new Dictionary<string, MacroDocument>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<MacroDocument>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<MacroDocument>();
        queue.Enqueue(root);
        while (queue.TryDequeue(out var document))
        {
            if (!visited.Add(document)) continue;
            if (visited.Count > 4096) throw new InvalidOperationException("宏依赖数量超过安全上限。");
            foreach (var name in MacroCallReferenceCollector.Collect(document))
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
