namespace MacroHid.Core;

public sealed record MacroDocument(int Version, string Name, IReadOnlyList<MacroStep> Steps);

public abstract record MacroStep;

public sealed record KeyStep(
    KeyActionKind Kind,
    HidKey Key,
    HidModifier Modifiers,
    TimeSpan Hold) : MacroStep;

public sealed record MouseMoveStep(
    MouseMoveMode Mode,
    int X,
    int Y,
    TimeSpan Duration,
    MouseButton Buttons = MouseButton.None) : MacroStep;

public sealed record MouseButtonStep(
    MouseButton Button,
    ButtonActionKind Kind,
    TimeSpan Hold) : MacroStep;

public sealed record MouseWheelStep(
    int Vertical,
    int Horizontal,
    MouseButton Buttons = MouseButton.None) : MacroStep;

public sealed record ConsumerStep(
    ConsumerControl Control,
    ButtonActionKind Kind,
    TimeSpan Hold) : MacroStep;

public sealed record WaitStep(TimeSpan Duration) : MacroStep;

public sealed record RepeatStep(int Count, IReadOnlyList<MacroStep> Steps) : MacroStep;

public sealed record PixelWhenStep(
    PixelCondition Condition,
    IReadOnlyList<MacroStep> ThenSteps) : MacroStep;

public enum KeyActionKind
{
    Down,
    Up,
    Tap
}

public enum MouseMoveMode
{
    Relative,
    Absolute
}

public enum ButtonActionKind
{
    Down,
    Up,
    Click
}

[Flags]
public enum HidModifier : byte
{
    None = 0x00,
    LeftCtrl = 0x01,
    LeftShift = 0x02,
    LeftAlt = 0x04,
    LeftGui = 0x08,
    RightCtrl = 0x10,
    RightShift = 0x20,
    RightAlt = 0x40,
    RightGui = 0x80
}

[Flags]
public enum MouseButton : byte
{
    None = 0x00,
    Left = 0x01,
    Right = 0x02,
    Middle = 0x04,
    X1 = 0x08,
    X2 = 0x10,
    Button6 = 0x20,
    Button7 = 0x40,
    Button8 = 0x80
}
