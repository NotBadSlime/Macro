namespace MacroHid.Core;

public enum MacroActionTemplateKind
{
    Delay,
    Keyboard,
    MouseButton,
    MouseMove,
    MouseWheel,
    WindowActivate,
    OcrExtractText,
    OcrClick,
    Text,
    Comment,
    Macro,
    Loop,
    Pixel,
    TextWhen,
    WeakNetStart,
    WeakNetStop,
    NetLimitStart,
    NetLimitStop,
    NetLimiterStart,
    NetLimiterStop,
    ClockShift,
    ClockRestore,
    FrameLockStart,
    FrameLockStop,
    Parallel,
    StopCurrent,
    StopIteration,
    StopAll
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
                new WaitStep(InputPressTiming.DefaultPressReleaseGap),
                new KeyStep(KeyActionKind.Up, HidKey.A, HidModifier.None, TimeSpan.Zero)
            ],
            MacroActionTemplateKind.MouseButton =>
            [
                new MouseButtonStep(MouseButton.Left, ButtonActionKind.Down, TimeSpan.Zero),
                new WaitStep(InputPressTiming.DefaultPressReleaseGap),
                new MouseButtonStep(MouseButton.Left, ButtonActionKind.Up, TimeSpan.Zero)
            ],
            MacroActionTemplateKind.MouseMove => [new MouseMoveStep(MouseMoveMode.Relative, 20, 0, TimeSpan.Zero)],
            MacroActionTemplateKind.MouseWheel => [new MouseWheelStep(-1, 0)],
            MacroActionTemplateKind.WindowActivate => [new WindowActivateStep(
                "YuanShen.exe",
                Timeout: TimeSpan.FromSeconds(3))],
            MacroActionTemplateKind.OcrExtractText => [new OcrExtractTextStep(
                ScreenRegion.FromRect(0, 0, 640, 360))],
            MacroActionTemplateKind.OcrClick => [new OcrClickStep(
                ScreenRegion.FromRect(0, 0, 640, 360),
                "文字",
                Hold: TimeSpan.FromMilliseconds(20),
                Interval: TimeSpan.FromMilliseconds(80))],
            MacroActionTemplateKind.Text => [new TextStep("text")],
            MacroActionTemplateKind.Comment => [new CommentStep("注释")],
            MacroActionTemplateKind.Macro => [new MacroCallStep(string.Empty)],
            MacroActionTemplateKind.Loop => [new RepeatStep(2, [])],
            MacroActionTemplateKind.TextWhen => [new TextWhenStep(
                ScreenRegion.FromRect(0, 0, 640, 360),
                "文字",
                [])],
            MacroActionTemplateKind.WeakNetStart => [new WeakNetStep(
                "YuanShen.exe",
                WeakNetAction.Start,
                TimeSpan.Zero,
                TimeSpan.FromHours(1))],
            MacroActionTemplateKind.WeakNetStop => [new WeakNetStep(
                string.Empty,
                WeakNetAction.Stop,
                TimeSpan.Zero,
                TimeSpan.Zero)],
            MacroActionTemplateKind.NetLimitStart => [new NetLimitStep(
                "YuanShen.exe",
                NetLimitAction.Start,
                NetLimitStep.DefaultBytesPerSecond)],
            MacroActionTemplateKind.NetLimitStop => [new NetLimitStep(
                string.Empty,
                NetLimitAction.Stop,
                NetLimitStep.DefaultBytesPerSecond)],
            MacroActionTemplateKind.NetLimiterStart => [new NetLimitStep(
                "YuanShen.exe",
                NetLimitAction.Start,
                NetLimitStep.DefaultBytesPerSecond,
                NetLimitEngine.NetLimiter)],
            MacroActionTemplateKind.NetLimiterStop => [new NetLimitStep(
                string.Empty,
                NetLimitAction.Stop,
                NetLimitStep.DefaultBytesPerSecond,
                NetLimitEngine.NetLimiter)],
            MacroActionTemplateKind.ClockShift => [new ClockShiftStep(
                ClockShiftAction.Shift,
                TimeSpan.FromHours(1))],
            MacroActionTemplateKind.ClockRestore => [new ClockShiftStep(
                ClockShiftAction.Restore,
                TimeSpan.Zero)],
            MacroActionTemplateKind.FrameLockStart => [new FrameLockStep("YuanShen.exe", FrameLockAction.Start, 60)],
            MacroActionTemplateKind.FrameLockStop => [new FrameLockStep("YuanShen.exe", FrameLockAction.Stop, 60)],
            MacroActionTemplateKind.Parallel => [new ParallelStep(
                new ParallelBranchStep("分支 1", []),
                new ParallelBranchStep("分支 2", []))],
            MacroActionTemplateKind.StopCurrent => [new StopCurrentSequenceStep()],
            MacroActionTemplateKind.StopIteration => [new StopCurrentIterationStep()],
            MacroActionTemplateKind.StopAll => [new StopAllSequencesStep()],
            MacroActionTemplateKind.Pixel => [new PixelWhenStep(
                new PixelCondition(new PixelCoordinate(CoordinateScope.Screen, 0, 0), new RgbColor(0, 0, 0), 0),
                [
                    new KeyStep(KeyActionKind.Down, HidKey.A, HidModifier.None, TimeSpan.Zero),
                    new WaitStep(InputPressTiming.DefaultPressReleaseGap),
                    new KeyStep(KeyActionKind.Up, HidKey.A, HidModifier.None, TimeSpan.Zero)
                ])],
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
    }
}
