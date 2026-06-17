namespace MacroHid.Core;

public sealed record ScheduledInputAction(InputAction Action, long DueTick, MacroStep SourceStep);

public abstract record InputAction;

public sealed record KeyInputAction(
    KeyActionKind Kind,
    HidKey Key,
    HidModifier Modifiers) : InputAction;

public sealed record TextInputAction(string Text) : InputAction;

public sealed record MouseMoveInputAction(
    MouseMoveMode Mode,
    int X,
    int Y,
    MouseButton Buttons = MouseButton.None) : InputAction;

public sealed record MouseButtonInputAction(
    MouseButton Button,
    ButtonActionKind Kind) : InputAction;

public sealed record MouseWheelInputAction(
    int Vertical,
    int Horizontal,
    MouseButton Buttons = MouseButton.None) : InputAction;

public sealed record ConsumerInputAction(
    ConsumerControl Control,
    ButtonActionKind Kind) : InputAction;

public static class InputActionCompiler
{
    public static IReadOnlyList<ScheduledInputAction> Compile(
        MacroDocument document,
        long startTick,
        long qpcFrequency,
        Func<PixelCondition, bool>? pixelEvaluator = null,
        Func<string, MacroDocument?>? macroResolver = null,
        Func<WaitStep, TimeSpan>? waitDurationSampler = null)
    {
        if (qpcFrequency <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(qpcFrequency), "QPC frequency must be positive.");
        }

        var actions = new List<ScheduledInputAction>();
        var elapsedTicks = 0L;
        CompileSteps(document.Steps, actions, startTick, qpcFrequency, pixelEvaluator, macroResolver, waitDurationSampler, ref elapsedTicks, depth: 0);
        return actions;
    }

    private static void CompileSteps(
        IReadOnlyList<MacroStep> steps,
        List<ScheduledInputAction> actions,
        long startTick,
        long qpcFrequency,
        Func<PixelCondition, bool>? pixelEvaluator,
        Func<string, MacroDocument?>? macroResolver,
        Func<WaitStep, TimeSpan>? waitDurationSampler,
        ref long elapsedTicks,
        int depth)
    {
        if (depth > 16)
        {
            throw new InvalidOperationException("Macro call nesting is too deep.");
        }

        foreach (var step in steps)
        {
            switch (step)
            {
                case WaitStep wait:
                    elapsedTicks += ToTicks(SampleWait(wait, waitDurationSampler), qpcFrequency);
                    break;
                case RepeatStep repeat:
                    for (var i = 0; i < repeat.Count; i++)
                    {
                        CompileSteps(repeat.Steps, actions, startTick, qpcFrequency, pixelEvaluator, macroResolver, waitDurationSampler, ref elapsedTicks, depth);
                    }

                    break;
                case MacroCallStep macro:
                    if (macroResolver is null)
                    {
                        throw new InvalidOperationException($"Macro call '{macro.Macro}' cannot be resolved without a macro resolver.");
                    }

                    var document = macroResolver(macro.Macro)
                        ?? throw new InvalidOperationException($"Macro call target '{macro.Macro}' was not found.");
                    CompileSteps(document.Steps, actions, startTick, qpcFrequency, pixelEvaluator, macroResolver, waitDurationSampler, ref elapsedTicks, depth + 1);
                    break;
                case PixelWhenStep pixel:
                    if (pixel.WindowStart is { } windowStart)
                    {
                        var windowStartTicks = ToTicks(windowStart, qpcFrequency);
                        if (elapsedTicks < windowStartTicks)
                        {
                            elapsedTicks = windowStartTicks;
                        }
                    }

                    if (pixelEvaluator?.Invoke(pixel.Condition) == true)
                    {
                        CompileSteps(pixel.ThenSteps, actions, startTick, qpcFrequency, pixelEvaluator, macroResolver, waitDurationSampler, ref elapsedTicks, depth);
                    }
                    else if (pixel.WindowEnd is { } windowEnd)
                    {
                        var windowEndTicks = ToTicks(windowEnd, qpcFrequency);
                        if (elapsedTicks < windowEndTicks)
                        {
                            elapsedTicks = windowEndTicks;
                        }
                    }

                    break;
                case KeyStep key:
                    CompileKey(key, actions, startTick, qpcFrequency, ref elapsedTicks);
                    break;
                case TextStep text:
                    AddAction(actions, new TextInputAction(text.Text), startTick + elapsedTicks, text);
                    break;
                case MouseButtonStep button:
                    CompileMouseButton(button, actions, startTick, qpcFrequency, ref elapsedTicks);
                    break;
                case MouseMoveStep move:
                    AddAction(actions, new MouseMoveInputAction(move.Mode, move.X, move.Y, move.Buttons), startTick + elapsedTicks, move);
                    elapsedTicks += ToTicks(move.Duration, qpcFrequency);
                    break;
                case MouseWheelStep wheel:
                    AddAction(actions, new MouseWheelInputAction(wheel.Vertical, wheel.Horizontal, wheel.Buttons), startTick + elapsedTicks, wheel);
                    break;
                case ConsumerStep consumer:
                    CompileConsumer(consumer, actions, startTick, qpcFrequency, ref elapsedTicks);
                    break;
                default:
                    throw new NotSupportedException($"Unsupported macro step '{step.GetType().Name}'.");
            }
        }
    }

    private static void CompileKey(
        KeyStep step,
        List<ScheduledInputAction> actions,
        long startTick,
        long qpcFrequency,
        ref long elapsedTicks)
    {
        if (step.Kind == KeyActionKind.Tap)
        {
            AddAction(actions, new KeyInputAction(KeyActionKind.Down, step.Key, step.Modifiers), startTick + elapsedTicks, step);
            elapsedTicks += ToTicks(step.Hold, qpcFrequency);
            AddAction(actions, new KeyInputAction(KeyActionKind.Up, step.Key, step.Modifiers), startTick + elapsedTicks, step);
            return;
        }

        AddAction(actions, new KeyInputAction(step.Kind, step.Key, step.Modifiers), startTick + elapsedTicks, step);
    }

    private static void CompileMouseButton(
        MouseButtonStep step,
        List<ScheduledInputAction> actions,
        long startTick,
        long qpcFrequency,
        ref long elapsedTicks)
    {
        AddMouseButtonCoordinateAction(step, actions, startTick + elapsedTicks);

        if (step.Kind == ButtonActionKind.Click)
        {
            AddAction(actions, new MouseButtonInputAction(step.Button, ButtonActionKind.Down), startTick + elapsedTicks, step);
            elapsedTicks += ToTicks(step.Hold, qpcFrequency);
            AddAction(actions, new MouseButtonInputAction(step.Button, ButtonActionKind.Up), startTick + elapsedTicks, step);
            return;
        }

        AddAction(actions, new MouseButtonInputAction(step.Button, step.Kind), startTick + elapsedTicks, step);
    }

    private static void AddMouseButtonCoordinateAction(
        MouseButtonStep step,
        List<ScheduledInputAction> actions,
        long dueTick)
    {
        if (!step.HasCoordinate)
        {
            return;
        }

        AddAction(
            actions,
            new MouseMoveInputAction(step.CoordinateMode ?? MouseMoveMode.Absolute, step.X!.Value, step.Y!.Value),
            dueTick,
            step);
    }

    private static void CompileConsumer(
        ConsumerStep step,
        List<ScheduledInputAction> actions,
        long startTick,
        long qpcFrequency,
        ref long elapsedTicks)
    {
        if (step.Kind == ButtonActionKind.Click)
        {
            AddAction(actions, new ConsumerInputAction(step.Control, ButtonActionKind.Down), startTick + elapsedTicks, step);
            elapsedTicks += ToTicks(step.Hold, qpcFrequency);
            AddAction(actions, new ConsumerInputAction(step.Control, ButtonActionKind.Up), startTick + elapsedTicks, step);
            return;
        }

        AddAction(actions, new ConsumerInputAction(step.Control, step.Kind), startTick + elapsedTicks, step);
    }

    private static void AddAction(List<ScheduledInputAction> actions, InputAction action, long dueTick, MacroStep sourceStep)
    {
        actions.Add(new ScheduledInputAction(action, dueTick, sourceStep));
    }

    private static TimeSpan SampleWait(WaitStep wait, Func<WaitStep, TimeSpan>? waitDurationSampler)
    {
        var sampled = waitDurationSampler?.Invoke(wait) ?? wait.Sample(Random.Shared);
        if (wait.IsRandom && wait.MaxDuration is { } max)
        {
            if (sampled < wait.MinDuration)
            {
                return wait.MinDuration;
            }

            if (sampled > max)
            {
                return max;
            }
        }

        return sampled < TimeSpan.Zero ? TimeSpan.Zero : sampled;
    }

    private static long ToTicks(TimeSpan duration, long qpcFrequency)
    {
        return (long)Math.Round(duration.TotalSeconds * qpcFrequency, MidpointRounding.AwayFromZero);
    }
}
