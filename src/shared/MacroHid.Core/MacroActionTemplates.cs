namespace MacroHid.Core;

public enum MacroActionTemplateKind
{
    Delay,
    Keyboard,
    MouseButton,
    MouseMove,
    MouseWheel,
    Text,
    Macro,
    Loop,
    Pixel
}

public static class MacroActionTemplateFactory
{
    public static MacroStep CreateStep(MacroActionTemplateKind kind)
    {
        return CreateSteps(kind)[0];
    }

    public static IReadOnlyList<MacroStep> CreateSteps(MacroActionTemplateKind kind)
    {
        return kind switch
        {
            MacroActionTemplateKind.Delay => [new WaitStep(TimeSpan.FromMilliseconds(100))],
            MacroActionTemplateKind.Keyboard =>
            [
                new KeyStep(KeyActionKind.Down, HidKey.A, HidModifier.None, TimeSpan.Zero),
                new KeyStep(KeyActionKind.Up, HidKey.A, HidModifier.None, TimeSpan.Zero)
            ],
            MacroActionTemplateKind.MouseButton =>
            [
                new MouseButtonStep(MouseButton.Left, ButtonActionKind.Down, TimeSpan.Zero),
                new MouseButtonStep(MouseButton.Left, ButtonActionKind.Up, TimeSpan.Zero)
            ],
            MacroActionTemplateKind.MouseMove => [new MouseMoveStep(MouseMoveMode.Relative, 20, 0, TimeSpan.Zero)],
            MacroActionTemplateKind.MouseWheel => [new MouseWheelStep(-1, 0)],
            MacroActionTemplateKind.Text => [new TextStep("text")],
            MacroActionTemplateKind.Macro => [new MacroCallStep(string.Empty)],
            MacroActionTemplateKind.Loop => [new RepeatStep(2, [])],
            MacroActionTemplateKind.Pixel => [new PixelWhenStep(
                new PixelCondition(new PixelCoordinate(CoordinateScope.Screen, 0, 0), new RgbColor(0, 0, 0), 0),
                [
                    new KeyStep(KeyActionKind.Down, HidKey.A, HidModifier.None, TimeSpan.Zero),
                    new KeyStep(KeyActionKind.Up, HidKey.A, HidModifier.None, TimeSpan.Zero)
                ])],
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
    }
}
