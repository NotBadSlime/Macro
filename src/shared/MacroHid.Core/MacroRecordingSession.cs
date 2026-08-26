namespace MacroHid.Core;

public enum MacroRecordingMode
{
    Replica,
    FixedDelay,
    NoDelay
}

public sealed record MacroRecordingOptions(
    bool RecordKeyboard = true,
    bool RecordMouseButtons = true,
    bool RecordMouseWheel = true,
    bool RecordMouseMove = false,
    MacroRecordingMode Mode = MacroRecordingMode.Replica,
    TimeSpan MinimumDelay = default,
    TimeSpan FixedDelay = default,
    TimeSpan MouseMoveSampleInterval = default,
    int MouseMoveMinimumDistance = 2)
{
    public static MacroRecordingOptions Default { get; } = new(
        MinimumDelay: TimeSpan.FromMilliseconds(1),
        MouseMoveSampleInterval: TimeSpan.FromMilliseconds(16),
        MouseMoveMinimumDistance: 2);

    public static MacroRecordingOptions ForUserMode(MacroRecordingMode mode) => new(
        RecordMouseMove: false,
        Mode: mode,
        MinimumDelay: TimeSpan.FromMilliseconds(1),
        FixedDelay: InputPressTiming.DefaultPressReleaseGap,
        MouseMoveSampleInterval: TimeSpan.FromMilliseconds(16),
        MouseMoveMinimumDistance: 2);
}

public sealed class MacroRecordingSession
{
    private readonly MacroRecordingOptions options;
    private readonly List<MacroStep> steps = [];
    private readonly HashSet<HidKey> pressedKeys = [];
    private readonly HashSet<MouseButton> pressedMouseButtons = [];
    private TimeSpan lastAcceptedTime;
    private TimeSpan lastMouseMoveTime;
    private int lastMouseX;
    private int lastMouseY;
    private bool hasAcceptedInput;
    private bool hasMousePosition;
    private bool completed;

    public MacroRecordingSession(MacroRecordingOptions? options = null)
    {
        this.options = NormalizeOptions(options ?? MacroRecordingOptions.Default);
    }

    public int InputCount => steps.Count(static step => step is not WaitStep);

    public IReadOnlyList<MacroStep> Snapshot() => steps.ToList();

    public bool RecordKey(TimeSpan elapsed, HidKey key, bool isDown)
    {
        EnsureActive();
        if (!options.RecordKeyboard || key == HidKey.None)
        {
            return false;
        }

        if (isDown ? !pressedKeys.Add(key) : !pressedKeys.Remove(key))
        {
            return false;
        }

        AddInput(elapsed, new KeyStep(
            isDown ? KeyActionKind.Down : KeyActionKind.Up,
            key,
            HidModifier.None,
            TimeSpan.Zero));
        return true;
    }

    public bool RecordMouseButton(
        TimeSpan elapsed,
        MouseButton button,
        bool isDown,
        int x,
        int y)
    {
        EnsureActive();
        if (!options.RecordMouseButtons || button == MouseButton.None)
        {
            return false;
        }

        if (isDown ? !pressedMouseButtons.Add(button) : !pressedMouseButtons.Remove(button))
        {
            return false;
        }

        AddInput(elapsed, new MouseButtonStep(
            button,
            isDown ? ButtonActionKind.Down : ButtonActionKind.Up,
            TimeSpan.Zero,
            isDown ? MouseMoveMode.Absolute : null,
            isDown ? x : null,
            isDown ? y : null));
        return true;
    }

    public bool RecordMouseMove(TimeSpan elapsed, int x, int y)
    {
        EnsureActive();
        if (!options.RecordMouseMove)
        {
            return false;
        }

        if (hasMousePosition)
        {
            var elapsedSinceMove = elapsed - lastMouseMoveTime;
            var distance = Math.Max(Math.Abs(x - lastMouseX), Math.Abs(y - lastMouseY));
            if (elapsedSinceMove < options.MouseMoveSampleInterval
                || distance < options.MouseMoveMinimumDistance)
            {
                return false;
            }
        }

        lastMouseMoveTime = elapsed;
        lastMouseX = x;
        lastMouseY = y;
        hasMousePosition = true;
        AddInput(elapsed, new MouseMoveStep(MouseMoveMode.Absolute, x, y, TimeSpan.Zero));
        return true;
    }

    public bool RecordMouseWheel(TimeSpan elapsed, int vertical, int horizontal)
    {
        EnsureActive();
        if (!options.RecordMouseWheel || vertical == 0 && horizontal == 0)
        {
            return false;
        }

        AddInput(elapsed, new MouseWheelStep(vertical, horizontal));
        return true;
    }

    public void DiscardTrailingStopHotkey()
    {
        EnsureActive();
        var stopKeys = new HashSet<HidKey>
        {
            HidKey.LeftControl,
            HidKey.RightControl,
            HidKey.LeftShift,
            HidKey.RightShift,
            HidKey.F12
        };

        while (steps.Count > 0)
        {
            if (steps[^1] is WaitStep)
            {
                steps.RemoveAt(steps.Count - 1);
                continue;
            }

            if (steps[^1] is KeyStep key && stopKeys.Contains(key.Key))
            {
                steps.RemoveAt(steps.Count - 1);
                pressedKeys.Remove(key.Key);
                continue;
            }

            break;
        }
    }

    public IReadOnlyList<MacroStep> Complete()
    {
        if (completed)
        {
            return steps.ToList();
        }

        completed = true;
        foreach (var key in pressedKeys.OrderBy(static key => key))
        {
            steps.Add(new KeyStep(KeyActionKind.Up, key, HidModifier.None, TimeSpan.Zero));
        }

        foreach (var button in pressedMouseButtons.OrderBy(static button => button))
        {
            steps.Add(new MouseButtonStep(button, ButtonActionKind.Up, TimeSpan.Zero));
        }

        pressedKeys.Clear();
        pressedMouseButtons.Clear();
        return steps.ToList();
    }

    private void AddInput(TimeSpan elapsed, MacroStep step)
    {
        elapsed = elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
        if (hasAcceptedInput)
        {
            switch (options.Mode)
            {
                case MacroRecordingMode.NoDelay:
                    break;
                case MacroRecordingMode.FixedDelay:
                    steps.Add(new WaitStep(options.FixedDelay));
                    break;
                default:
                    var delay = elapsed - lastAcceptedTime;
                    if (delay >= options.MinimumDelay)
                    {
                        steps.Add(new WaitStep(delay));
                    }
                    break;
            }
        }

        steps.Add(step);
        lastAcceptedTime = elapsed;
        hasAcceptedInput = true;
    }

    private void EnsureActive()
    {
        if (completed)
        {
            throw new InvalidOperationException("The recording session has already completed.");
        }
    }

    private static MacroRecordingOptions NormalizeOptions(MacroRecordingOptions options)
    {
        return options with
        {
            MinimumDelay = options.MinimumDelay <= TimeSpan.Zero
                ? TimeSpan.FromMilliseconds(1)
                : options.MinimumDelay,
            FixedDelay = options.FixedDelay <= TimeSpan.Zero
                ? InputPressTiming.DefaultPressReleaseGap
                : options.FixedDelay,
            MouseMoveSampleInterval = options.MouseMoveSampleInterval <= TimeSpan.Zero
                ? TimeSpan.FromMilliseconds(16)
                : options.MouseMoveSampleInterval,
            MouseMoveMinimumDistance = Math.Max(1, options.MouseMoveMinimumDistance)
        };
    }
}
