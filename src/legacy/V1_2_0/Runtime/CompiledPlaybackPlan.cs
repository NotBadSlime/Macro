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
        long durationTicks,
        bool requiresResampling,
        Dictionary<ActionBatchKey, PreparedInputBatch> preparedCache)
    {
        this.document = document;
        this.pixelEvaluator = pixelEvaluator;
        this.macroResolver = macroResolver;
        this.preparedCache = preparedCache;
        QpcFrequency = qpcFrequency;
        Batches = batches;
        DurationTicks = durationTicks;
        RequiresResampling = requiresResampling;
    }

    public long QpcFrequency { get; }

    public IReadOnlyList<CompiledInputBatch> Batches { get; }

    public long DurationTicks { get; }

    public bool RequiresResampling { get; }

    internal NativePlaybackTimeline ExportNativeTimeline()
    {
        var nativeInputCount = 0;
        foreach (var batch in Batches)
        {
            nativeInputCount += batch.PreparedBatch.NativeInputCount;
        }

        var nativeInputs = new NativeInput[nativeInputCount];
        var nativeBatches = new MhpBatch[Batches.Count];
        var offset = 0;

        for (var i = 0; i < Batches.Count; i++)
        {
            var batch = Batches[i];
            var batchInputs = batch.PreparedBatch.NativeInputs;
            Array.Copy(batchInputs, 0, nativeInputs, offset, batchInputs.Length);
            nativeBatches[i] = new MhpBatch(
                batch.DueTick,
                checked((uint)offset),
                checked((uint)batchInputs.Length),
                checked((uint)batch.PreparedBatch.ActionCount));
            offset += batchInputs.Length;
        }

        return new NativePlaybackTimeline(nativeInputs, nativeBatches, DurationTicks);
    }

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
        var compiled = InputActionCompiler.CompileWithDuration(
            document,
            startTick: 0,
            qpcFrequency,
            pixelEvaluator,
            macroResolver,
            waitDurationSampler);
        var cache = existingCache ?? new Dictionary<ActionBatchKey, PreparedInputBatch>();
        var batches = BuildBatches(compiled.Actions, cache);
        var lastBatchTick = batches.Count == 0 ? 0 : Math.Max(0, batches[^1].DueTick);
        var durationTicks = Math.Max(compiled.DurationTicks, lastBatchTick);
        return new CompiledPlaybackPlan(
            document,
            qpcFrequency,
            pixelEvaluator,
            macroResolver,
            batches,
            durationTicks,
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

internal sealed record NativePlaybackTimeline(
    NativeInput[] Inputs,
    MhpBatch[] Batches,
    long DurationTicks,
    int LoopStepCount = 0);
