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
        Func<PixelCondition, bool>? pixelEvaluator = null)
    {
        if (qpcFrequency <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(qpcFrequency), "QPC frequency must be positive.");
        }

        var actions = new List<ScheduledInputAction>();
        var elapsedTicks = 0L;
        CompileSteps(document.Steps, actions, startTick, qpcFrequency, pixelEvaluator, ref elapsedTicks);
        return actions;
    }

    private static void CompileSteps(
        IReadOnlyList<MacroStep> steps,
        List<ScheduledInputAction> actions,
        long startTick,
        long qpcFrequency,
        Func<PixelCondition, bool>? pixelEvaluator,
        ref long elapsedTicks)
    {
        foreach (var step in steps)
        {
            switch (step)
            {
                case WaitStep wait:
                    elapsedTicks += ToTicks(wait.Duration, qpcFrequency);
                    break;
                case RepeatStep repeat:
                    for (var i = 0; i < repeat.Count; i++)
                    {
                        CompileSteps(repeat.Steps, actions, startTick, qpcFrequency, pixelEvaluator, ref elapsedTicks);
                    }

                    break;
                case PixelWhenStep pixel:
                    if (pixelEvaluator?.Invoke(pixel.Condition) == true)
                    {
                        CompileSteps(pixel.ThenSteps, actions, startTick, qpcFrequency, pixelEvaluator, ref elapsedTicks);
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
        if (step.Kind == ButtonActionKind.Click)
        {
            AddAction(actions, new MouseButtonInputAction(step.Button, ButtonActionKind.Down), startTick + elapsedTicks, step);
            elapsedTicks += ToTicks(step.Hold, qpcFrequency);
            AddAction(actions, new MouseButtonInputAction(step.Button, ButtonActionKind.Up), startTick + elapsedTicks, step);
            return;
        }

        AddAction(actions, new MouseButtonInputAction(step.Button, step.Kind), startTick + elapsedTicks, step);
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

    private static long ToTicks(TimeSpan duration, long qpcFrequency)
    {
        return (long)Math.Round(duration.TotalSeconds * qpcFrequency, MidpointRounding.AwayFromZero);
    }
}
