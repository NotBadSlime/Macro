using MacroHid.Core;

namespace MacroHid.Runtime;

public interface IConditionEvaluator
{
    bool Evaluate(IConditionMatcher matcher);
}

public sealed class ConditionMonitor : IDisposable
{
    private readonly ConditionalDirective directive;
    private readonly IConditionEvaluator evaluator;
    private readonly IMacroInputSink inputSink;
    private readonly Func<string, MacroDocument?>? macroResolver;
    private readonly long macroStartTick;
    private readonly long qpcFrequency;
    private readonly IHighResolutionClock clock = new QpcHighResolutionClock();
    private readonly IPlaybackDelayStrategy delayStrategy;

    private CancellationTokenSource cts = new();
    private Thread? monitorThread;
    private volatile bool triggered;
    private volatile bool active;

    public ConditionMonitor(
        ConditionalDirective directive,
        IConditionEvaluator evaluator,
        IMacroInputSink inputSink,
        Func<string, MacroDocument?>? macroResolver = null,
        long macroStartTick = 0,
        long qpcFrequency = 0)
    {
        this.directive = directive;
        this.evaluator = evaluator;
        this.inputSink = inputSink;
        this.macroResolver = macroResolver;
        this.macroStartTick = macroStartTick;
        this.qpcFrequency = qpcFrequency;
        delayStrategy = new QpcPlaybackDelayStrategy(clock);
    }

    public bool HasTriggered => triggered;
    public string DirectiveId => directive.Id;
    public int StartStepIndex => directive.StartStepIndex;
    public int EndStepIndex => directive.EndStepIndex;

    public void Activate()
    {
        if (active) return;
        active = true;
        if (cts.IsCancellationRequested)
        {
            cts.Dispose();
            cts = new CancellationTokenSource();
        }

        monitorThread = new Thread(() => PollLoop(cts.Token))
        {
            IsBackground = true,
            Priority = ThreadPriority.Highest,
            Name = $"MacroHID-Condition-{directive.Id}"
        };
        monitorThread.Start();
    }

    public void Deactivate()
    {
        active = false;
        cts.Cancel();
    }

    public void CompleteAfterCurrentEvaluation()
    {
        active = false;
    }

    public void WaitForTriggeredCompletion(CancellationToken cancellationToken)
    {
        if (monitorThread is null)
        {
            return;
        }

        while (monitorThread.IsAlive)
        {
            cancellationToken.ThrowIfCancellationRequested();
            monitorThread.Join(TimeSpan.FromMilliseconds(10));
        }
    }

    private void PollLoop(CancellationToken cancellationToken)
    {
        using var precisionContext = PrecisionPlaybackContext.Enter(PrecisionMode.ExtremeDuringPlayback);
        var frequency = EffectiveFrequency();
        var pollTicks = Math.Max(1, (long)Math.Round(directive.EffectivePollInterval.TotalSeconds * frequency, MidpointRounding.AwayFromZero));
        var nextPollTick = clock.GetTimestamp();

        while (!cancellationToken.IsCancellationRequested && active && !triggered)
        {
            try
            {
                if (!WaitUntilInsideTimeWindow(cancellationToken))
                {
                    return;
                }

                if (evaluator.Evaluate(directive.Condition))
                {
                    triggered = true;
                    ExecuteThenSteps(cancellationToken);
                    return;
                }

                nextPollTick += pollTicks;
                delayStrategy.WaitUntil(nextPollTick, frequency, cancellationToken, noWait: false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private bool WaitUntilInsideTimeWindow(CancellationToken cancellationToken)
    {
        if (macroStartTick <= 0 || qpcFrequency <= 0)
        {
            return true;
        }

        while (!cancellationToken.IsCancellationRequested && active)
        {
            RuntimeNativeMethods.QueryPerformanceCounter(out var now);
            var elapsedMs = (now - macroStartTick) * 1000.0 / qpcFrequency;

            if (directive.WindowEnd is { } end && elapsedMs > end.TotalMilliseconds)
            {
                return false;
            }

            if (directive.WindowStart is { } start && elapsedMs < start.TotalMilliseconds)
            {
                var dueTick = macroStartTick + (long)Math.Round(start.TotalSeconds * qpcFrequency, MidpointRounding.AwayFromZero);
                delayStrategy.WaitUntil(dueTick, qpcFrequency, cancellationToken, noWait: false);
                continue;
            }

            return true;
        }

        return false;
    }

    private void ExecuteThenSteps(CancellationToken cancellationToken)
    {
        if (directive.ThenSteps.Count == 0) return;

        var qpcFrequency = EffectiveFrequency();
        var plan = CompiledPlaybackPlan.Create(
            new MacroDocument(1, "_condition_then", PlaybackSettings.Default, directive.ThenSteps, null),
            qpcFrequency,
            pixelEvaluator: null,
            macroResolver);

        var startTick = clock.GetTimestamp();
        uint sequence = 100_000;

        foreach (var batch in plan.Batches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            delayStrategy.WaitUntil(startTick + batch.DueTick, qpcFrequency, cancellationToken, noWait: false);
            if (inputSink is SendInputMacroSink sendInput)
            {
                sendInput.SubmitPrepared(sequence, batch.PreparedBatch);
                sequence += (uint)batch.PreparedBatch.ActionCount;
            }
            else
            {
                foreach (var action in batch.PreparedBatch.Actions)
                {
                    inputSink.Submit(sequence++, action);
                }
            }
        }
    }

    public void Dispose()
    {
        Deactivate();
        try { monitorThread?.Join(TimeSpan.FromMilliseconds(500)); } catch { }
        cts.Dispose();
    }

    private long EffectiveFrequency() => qpcFrequency > 0 ? qpcFrequency : clock.Frequency;
}

public sealed class CompositeConditionEvaluator : IConditionEvaluator, IDisposable
{
    private readonly Func<PixelCondition, bool> pixelEvaluator;
    private readonly Func<TemplateMatcher, bool> templateEvaluator;
    private readonly Func<PixelHashMatcher, bool> pixelHashEvaluator;
    private readonly Func<TextMatcher, bool> textEvaluator;
    private readonly Lazy<PaddleOcrBridge> ocrBridge = new(() => new PaddleOcrBridge());

    public CompositeConditionEvaluator(
        Func<PixelCondition, bool>? pixelEvaluator = null,
        Func<TemplateMatcher, bool>? templateEvaluator = null,
        Func<PixelHashMatcher, bool>? pixelHashEvaluator = null,
        Func<TextMatcher, bool>? textEvaluator = null)
    {
        this.pixelEvaluator = pixelEvaluator ?? ScreenPixelSampler.Matches;
        this.templateEvaluator = templateEvaluator ?? EvaluateTemplate;
        this.pixelHashEvaluator = pixelHashEvaluator ?? EvaluatePixelHash;
        this.textEvaluator = textEvaluator ?? EvaluateText;
    }

    public bool Evaluate(IConditionMatcher matcher)
    {
        return matcher switch
        {
            PixelMatcher pixel => EvaluatePixel(pixel),
            TemplateMatcher template => templateEvaluator(template),
            PixelHashMatcher hash => pixelHashEvaluator(hash),
            TextMatcher text => textEvaluator(text),
            _ => false
        };
    }

    private bool EvaluatePixel(PixelMatcher pixel)
    {
        var condition = new PixelCondition(
            new PixelCoordinate(CoordinateScope.Screen, pixel.Region.TopLeft.X, pixel.Region.TopLeft.Y),
            pixel.Expected,
            pixel.Tolerance);
        return pixelEvaluator(condition);
    }

    private static bool EvaluateTemplate(TemplateMatcher template)
    {
        if (template.TemplateImageData.Length == 0)
        {
            return false;
        }

        return TemplateMatchEngine.Matches(template.Region, template.TemplateImageData, template.Threshold);
    }

    private static bool EvaluatePixelHash(PixelHashMatcher hash)
    {
        if (hash.ReferenceHash.Length == 0)
        {
            return false;
        }

        return PixelHashEngine.Matches(hash.Region, hash.ReferenceHash, hash.SimilarityThreshold);
    }

    private bool EvaluateText(TextMatcher text)
    {
        if (string.IsNullOrWhiteSpace(text.ExpectedText) || !ocrBridge.Value.IsAvailable)
        {
            return false;
        }

        return ocrBridge.Value.ContainsText(text.Region, text.ExpectedText, text.Contains, text.Language);
    }

    public void Dispose()
    {
        if (ocrBridge.IsValueCreated)
        {
            ocrBridge.Value.Dispose();
        }
    }
}
