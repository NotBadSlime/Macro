namespace MacroHid.Core;

public static class MacroStepNormalizer
{
    public static MacroDocument Normalize(MacroDocument document)
    {
        var steps = new List<MacroStep>();
        var indexMap = new List<(int start, int end)>();

        foreach (var step in document.Steps)
        {
            var start = steps.Count;
            steps.AddRange(NormalizeStep(step));
            var end = Math.Max(start, steps.Count - 1);
            indexMap.Add((start, end));
        }

        var conditions = NormalizeConditions(document.EffectiveConditions, indexMap);
        return document with
        {
            Steps = steps,
            Conditions = conditions.Count > 0 ? conditions : document.Conditions
        };
    }

    public static IReadOnlyList<MacroStep> NormalizeSteps(IReadOnlyList<MacroStep> steps)
    {
        var result = new List<MacroStep>();
        foreach (var step in steps)
        {
            result.AddRange(NormalizeStep(step));
        }

        return result;
    }

    private static IEnumerable<MacroStep> NormalizeStep(MacroStep step)
    {
        switch (step)
        {
            case KeyStep { Kind: KeyActionKind.Tap } key:
                return SplitKeyTap(key);
            case MouseButtonStep { Kind: ButtonActionKind.Click } button:
                return SplitMouseClick(button);
            case ConsumerStep { Kind: ButtonActionKind.Click } consumer:
                return SplitConsumerClick(consumer);
            case RepeatStep repeat:
                return [repeat with { Steps = NormalizeSteps(repeat.Steps) }];
            case PixelWhenStep pixel:
                return [pixel with { ThenSteps = NormalizeSteps(pixel.ThenSteps) }];
            default:
                return [step];
        }
    }

    private static IReadOnlyList<MacroStep> SplitKeyTap(KeyStep key)
    {
        var steps = new List<MacroStep>
        {
            key with { Kind = KeyActionKind.Down, Hold = TimeSpan.Zero }
        };
        AddHoldIfNeeded(steps, key.Hold);
        steps.Add(key with { Kind = KeyActionKind.Up, Hold = TimeSpan.Zero });
        return steps;
    }

    private static IReadOnlyList<MacroStep> SplitMouseClick(MouseButtonStep button)
    {
        var steps = new List<MacroStep>
        {
            button with { Kind = ButtonActionKind.Down, Hold = TimeSpan.Zero }
        };
        AddHoldIfNeeded(steps, button.Hold);
        steps.Add(button with
        {
            Kind = ButtonActionKind.Up,
            Hold = TimeSpan.Zero,
            CoordinateMode = null,
            X = null,
            Y = null
        });
        return steps;
    }

    private static IReadOnlyList<MacroStep> SplitConsumerClick(ConsumerStep consumer)
    {
        var steps = new List<MacroStep>
        {
            consumer with { Kind = ButtonActionKind.Down, Hold = TimeSpan.Zero }
        };
        AddHoldIfNeeded(steps, consumer.Hold);
        steps.Add(consumer with { Kind = ButtonActionKind.Up, Hold = TimeSpan.Zero });
        return steps;
    }

    private static void AddHoldIfNeeded(List<MacroStep> steps, TimeSpan hold)
    {
        if (hold > TimeSpan.Zero)
        {
            steps.Add(new WaitStep(hold));
        }
    }

    private static IReadOnlyList<ConditionalDirective> NormalizeConditions(
        IReadOnlyList<ConditionalDirective> conditions,
        IReadOnlyList<(int start, int end)> indexMap)
    {
        if (conditions.Count == 0 || indexMap.Count == 0)
        {
            return conditions;
        }

        var result = new List<ConditionalDirective>(conditions.Count);
        foreach (var condition in conditions)
        {
            if (condition.HasStepPaths)
            {
                result.Add(condition);
                continue;
            }

            var start = RemapStart(condition.StartStepIndex, indexMap);
            var end = RemapEnd(condition.EndStepIndex, indexMap);
            result.Add(condition with
            {
                StartStepIndex = start,
                EndStepIndex = Math.Max(start, end)
            });
        }

        return result;
    }

    private static int RemapStart(int index, IReadOnlyList<(int start, int end)> indexMap)
    {
        if (index < 0) return 0;
        if (index >= indexMap.Count) return indexMap[^1].start;
        return indexMap[index].start;
    }

    private static int RemapEnd(int index, IReadOnlyList<(int start, int end)> indexMap)
    {
        if (index < 0) return 0;
        if (index >= indexMap.Count) return indexMap[^1].end;
        return indexMap[index].end;
    }
}
