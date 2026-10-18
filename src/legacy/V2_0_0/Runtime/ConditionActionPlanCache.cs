using MacroHid.Core;

namespace MacroHid.Runtime;

internal sealed class ConditionActionPlanCache : IDisposable
{
    internal sealed class Store : IDisposable
    {
        internal readonly Dictionary<IReadOnlyList<MacroStep>, CompiledPlaybackPlan> Plans = new(ReferenceEqualityComparer.Instance);
        internal readonly Dictionary<CompiledPlaybackPlan, NativePlaybackPreparedPlan> Native = new(ReferenceEqualityComparer.Instance);
        public void Dispose() { foreach (var plan in Native.Values) plan.Dispose(); Native.Clear(); Plans.Clear(); }
    }
    private static readonly AsyncLocal<Store?> Active = new();
    private readonly Store? previous = Active.Value;
    private readonly Store? owned;
    internal ConditionActionPlanCache(Store? prepared = null)
    {
        if (Active.Value is not null) return;
        Active.Value = prepared ?? (owned = new Store());
    }
    internal static NativePlaybackPreparedPlan? GetNative(CompiledPlaybackPlan plan)
    {
        var store = Active.Value;
        if (store is null) return null;
        lock (store.Plans) return store.Native.GetValueOrDefault(plan);
    }
    internal static void Prewarm(MacroDocument root, Store store, long frequency, Func<string, MacroDocument?>? resolver, InputPressFloor floor, bool native)
    {
        using var scope = new ConditionActionPlanCache(store);
        var visited = new HashSet<MacroDocument>(ReferenceEqualityComparer.Instance);
        var pending = new Queue<MacroDocument>(); pending.Enqueue(root);
        while (pending.TryDequeue(out var document))
        {
            if (!visited.Add(document)) continue;
            if (visited.Count > 4096) throw new InvalidOperationException("Macro dependency limit exceeded.");
            foreach (var condition in document.EffectiveConditions)
            {
                var then = new MacroDocument(1, "_prepared_then", PlaybackSettings.Default, condition.ThenSteps);
                if (!CanCache(condition.ThenSteps) || MacroControlFlowInspector.RequiresManagedExecution(then, resolver)) continue;
                var plan = Get(condition.ThenSteps, () => CompiledPlaybackPlan.Create(then, frequency, macroResolver: resolver, pressFloor: floor));
                if (native && !store.Native.ContainsKey(plan) && NativePlaybackEngine.TryCreatePreparedPlan(plan.ExportNativeTimeline(), frequency, out var preparedPlan, out _, out _))
                    store.Native.Add(plan, preparedPlan!);
            }
            foreach (var name in MacroCallReferenceCollector.Collect(document))
                if (resolver?.Invoke(name) is { } target) pending.Enqueue(target);
        }
    }
    internal static bool CanCache(IReadOnlyList<MacroStep> steps) => steps.All(step => step switch
    {
        WaitStep { IsRandom: true } => false,
        RepeatStep repeat => CanCache(repeat.Steps),
        MacroCallStep or PixelWhenStep or TextWhenStep or ParallelStep or WindowActivateStep or OcrClickStep or OcrExtractTextStep => false,
        _ => true
    });
    internal static CompiledPlaybackPlan Get(IReadOnlyList<MacroStep> steps, Func<CompiledPlaybackPlan> compile)
    {
        var cache = Active.Value?.Plans;
        if (cache is null || !CanCache(steps)) return compile();
        lock (cache)
        {
            if (cache.TryGetValue(steps, out var plan)) return plan;
            plan = compile();
            if (cache.Count < 256) cache.Add(steps, plan);
            return plan;
        }
    }
    public void Dispose() { Active.Value = previous; owned?.Dispose(); }
}
