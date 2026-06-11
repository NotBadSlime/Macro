namespace MacroHid.Core;

public enum MacroActionTemplateKind
{
    Delay,
    Keyboard,
    MouseButton,
    MouseMove,
    MouseWheel,
    Text,
    Loop,
    Pixel
}

public static class MacroActionTemplateFactory
{
    public static MacroStep CreateStep(MacroActionTemplateKind kind)
    {
        return kind switch
        {
            MacroActionTemplateKind.Delay => new WaitStep(TimeSpan.FromMilliseconds(100)),
            MacroActionTemplateKind.Keyboard => new KeyStep(KeyActionKind.Tap, HidKey.A, HidModifier.None, TimeSpan.FromMilliseconds(1)),
            MacroActionTemplateKind.MouseButton => new MouseButtonStep(MouseButton.Left, ButtonActionKind.Click, TimeSpan.FromMilliseconds(1)),
            MacroActionTemplateKind.MouseMove => new MouseMoveStep(MouseMoveMode.Relative, 20, 0, TimeSpan.Zero),
            MacroActionTemplateKind.MouseWheel => new MouseWheelStep(-1, 0),
            MacroActionTemplateKind.Text => new TextStep("text"),
            MacroActionTemplateKind.Loop => new RepeatStep(2, [new WaitStep(TimeSpan.FromMilliseconds(100))]),
            MacroActionTemplateKind.Pixel => new PixelWhenStep(
                new PixelCondition(new PixelCoordinate(CoordinateScope.Screen, 0, 0), new RgbColor(0, 0, 0), 0),
                [new KeyStep(KeyActionKind.Tap, HidKey.A, HidModifier.None, TimeSpan.FromMilliseconds(1))]),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
    }
}
