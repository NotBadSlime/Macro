using MacroHid.Core;

namespace MacroHid.Runtime;

public sealed class CompiledPlaybackPlan
{
    private readonly MacroDocument document;
    private readonly Func<PixelCondition, bool>? pixelEvaluator;
    private readonly Func<string, MacroDocument?>? macroResolver;
    private readonly Dictionary<ActionBatchKey, PreparedInputBatch> preparedCache;

    private CompiledPlaybackPlan(
        MacroDocument document,
        long qpcFrequency,
        Func<PixelCondition, bool>? pixelEvaluator,
        Func<string, MacroDocument?>? macroResolver,
        IReadOnlyList<CompiledInputBatch> batches,
        bool requiresResampling,
        Dictionary<ActionBatchKey, PreparedInputBatch> preparedCache)
    {
        this.document = document;
        this.pixelEvaluator = pixelEvaluator;
        this.macroResolver = macroResolver;
        this.preparedCache = preparedCache;
        QpcFrequency = qpcFrequency;
        Batches = batches;
        RequiresResampling = requiresResampling;
    }

    public long QpcFrequency { get; }

    public IReadOnlyList<CompiledInputBatch> Batches { get; }

    public bool RequiresResampling { get; }

    public static CompiledPlaybackPlan Create(
        MacroDocument document,
        long qpcFrequency,
        Func<PixelCondition, bool>? pixelEvaluator = null,
        Func<string, MacroDocument?>? macroResolver = null,
        Func<WaitStep, TimeSpan>? waitDurationSampler = null)
    {
        return Create(document, qpcFrequency, pixelEvaluator, macroResolver, waitDurationSampler, null);
    }

    private static CompiledPlaybackPlan Create(
        MacroDocument document,
        long qpcFrequency,
        Func<PixelCondition, bool>? pixelEvaluator,
        Func<string, MacroDocument?>? macroResolver,
        Func<WaitStep, TimeSpan>? waitDurationSampler,
        Dictionary<ActionBatchKey, PreparedInputBatch>? existingCache)
    {
        var actions = InputActionCompiler.Compile(
            document,
            startTick: 0,
            qpcFrequency,
            pixelEvaluator,
            macroResolver,
            waitDurationSampler);
        var cache = existingCache ?? new Dictionary<ActionBatchKey, PreparedInputBatch>();
        var batches = BuildBatches(actions, cache);
        return new CompiledPlaybackPlan(
            document,
            qpcFrequency,
            pixelEvaluator,
            macroResolver,
            batches,
            ContainsRandomWait(document.Steps) || document.EffectiveConditions.Any(c => ContainsRandomWait(c.ThenSteps)),
            cache);
    }

    public CompiledPlaybackPlan Resample(Func<WaitStep, TimeSpan>? waitDurationSampler = null)
    {
        return Create(document, QpcFrequency, pixelEvaluator, macroResolver, waitDurationSampler, preparedCache);
    }

    private static IReadOnlyList<CompiledInputBatch> BuildBatches(
        IReadOnlyList<ScheduledInputAction> actions,
        Dictionary<ActionBatchKey, PreparedInputBatch> cache)
    {
        var batches = new List<CompiledInputBatch>();
        var i = 0;
        while (i < actions.Count)
        {
            var dueTick = actions[i].DueTick;
            var batchActions = new List<InputAction>();
            do
            {
                batchActions.Add(actions[i].Action);
                i++;
            }
            while (i < actions.Count && actions[i].DueTick == dueTick);

            var key = new ActionBatchKey(batchActions);
            if (!cache.TryGetValue(key, out var prepared))
            {
                prepared = PreparedInputBatch.FromActions(batchActions);
                cache[key] = prepared;
            }

            batches.Add(new CompiledInputBatch(dueTick, prepared));
        }

        return batches;
    }

    private static bool ContainsRandomWait(IReadOnlyList<MacroStep> steps)
    {
        foreach (var step in steps)
        {
            switch (step)
            {
                case WaitStep { IsRandom: true }:
                    return true;
                case RepeatStep repeat when ContainsRandomWait(repeat.Steps):
                    return true;
                case PixelWhenStep pixel when ContainsRandomWait(pixel.ThenSteps):
                    return true;
            }
        }

        return false;
    }

    private sealed class ActionBatchKey : IEquatable<ActionBatchKey>
    {
        private readonly InputAction[] actions;
        private readonly int hashCode;

        public ActionBatchKey(IReadOnlyList<InputAction> actions)
        {
            this.actions = actions.ToArray();
            var hash = new HashCode();
            foreach (var action in this.actions)
            {
                hash.Add(action);
            }

            hashCode = hash.ToHashCode();
        }

        public bool Equals(ActionBatchKey? other)
        {
            return other is not null && actions.SequenceEqual(other.actions);
        }

        public override bool Equals(object? obj) => Equals(obj as ActionBatchKey);

        public override int GetHashCode() => hashCode;
    }
}

public sealed record CompiledInputBatch(long DueTick, PreparedInputBatch PreparedBatch);
