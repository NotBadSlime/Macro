using System.Windows.Media;
using MacroHid.Core;

namespace MacroStudio.Controls;

/// <summary>Original line icons in a shared 24-by-24 drawing area.</summary>
public static class ActionIcons
{
    public static Geometry Delay { get; } = Draw("M12 5.5 C16.5 5.5 20 9 20 13.5 C20 18 16.5 21.5 12 21.5 C7.5 21.5 4 18 4 13.5 C4 9 7.5 5.5 12 5.5 Z M9.5 2.5 H14.5 M12 2.5 V5.5 M17.5 7.5 L20 5 M12 9 V13.5 L15 15");
    public static Geometry Keyboard { get; } = Draw("M5 6 H19 Q21 6 21 8 V16 Q21 18 19 18 H5 Q3 18 3 16 V8 Q3 6 5 6 Z M6 9.5 H6.5 M9.5 9.5 H10 M13 9.5 H13.5 M16.5 9.5 H17 M6 14 H6.5 M9.5 14 H14 M17 14 H18");
    public static Geometry MouseButton { get; } = Draw("M12 2.5 C16 2.5 19 5.5 19 9.5 V14.5 C19 18.5 16 21.5 12 21.5 C8 21.5 5 18.5 5 14.5 V9.5 C5 5.5 8 2.5 12 2.5 Z M5 10 H19 M12 2.5 V10");
    public static Geometry MouseMove { get; } = Draw("M4 4 L20 11 L13 13.5 L10.5 20 Z M17.5 4 H21 V7.5 M21 4 L16.5 8.5");
    public static Geometry MouseWheel { get; } = Draw("M9.5 4 C12.5 4 15 6.5 15 9.5 V14.5 C15 17.5 12.5 20 9.5 20 C6.5 20 4 17.5 4 14.5 V9.5 C4 6.5 6.5 4 9.5 4 Z M9.5 7.5 V11 M20 6 V18 M18 8 L20 6 L22 8 M18 16 L20 18 L22 16");
    public static Geometry Window { get; } = Draw("M13.5 5 H5 Q3 5 3 7 V17 Q3 19 5 19 H19 Q21 19 21 17 V10.5 M3 9 H12 M15.5 3.5 H21 V9 M21 3.5 L13 11.5");
    public static Geometry OcrText { get; } = Draw("M3.5 8 V5 Q3.5 3.5 5 3.5 H8 M16 3.5 H19 Q20.5 3.5 20.5 5 V8 M20.5 16 V19 Q20.5 20.5 19 20.5 H16 M8 20.5 H5 Q3.5 20.5 3.5 19 V16 M8.5 8 H15.5 M12 8 V16 M10 16 H14");
    public static Geometry OcrClick { get; } = Draw("M3.5 8 V5 Q3.5 3.5 5 3.5 H8 M16 3.5 H19 Q20.5 3.5 20.5 5 V8 M20.5 16 V19 Q20.5 20.5 19 20.5 H16 M8 20.5 H5 Q3.5 20.5 3.5 19 V16 M8.5 8.5 L15.5 12 L12.6 13.1 L11.5 16 Z");
    public static Geometry Text { get; } = Draw("M4 7 V5.5 H20 V7 M12 5.5 V18.5 M8.5 18.5 H15.5");
    public static Geometry Comment { get; } = Draw("M5 5 H19 Q21 5 21 7 V15 Q21 17 19 17 H10 L5 21 V17 Q3 17 3 15 V7 Q3 5 5 5 Z M7 9.5 H17 M7 13 H14");
    public static Geometry MacroCall { get; } = Draw("M4 7.5 L12 3 L20 7.5 V16.5 L12 21 L4 16.5 Z M4 7.5 L12 12 L20 7.5 M12 12 V21 M8 5.25 L16 9.75");
    public static Geometry Loop { get; } = Draw("M5 9 H17 Q20 9 20 12 V14 M17 6 L20 9 L17 12 M19 15 H7 Q4 15 4 12 V10 M7 12 L4 15 L7 18");
    public static Geometry Pixel { get; } = Draw("M8 4 H5 Q4 4 4 5 V8 M16 4 H19 Q20 4 20 5 V8 M20 16 V19 Q20 20 19 20 H16 M8 20 H5 Q4 20 4 19 V16 M9 9 H15 V15 H9 Z M12 6.5 V9 M12 15 V17.5 M6.5 12 H9 M15 12 H17.5");
    public static Geometry TextWhen { get; } = Draw("M3.5 7 H14 M8.75 7 V17 M6 17 H11.5 M14 15.5 L16.5 18 L21 12.5");
    public static Geometry WeakNet { get; } = Draw("M3 17 Q12 8 21 17 M6.5 20 Q12 14.5 17.5 20 M3 9 Q7.5 6 12 6 M16 6 L14 10 H19 L17 14");
    public static Geometry WeakNetOff { get; } = Draw("M3 9 Q7.5 6 12 6 M15 6.5 Q18.5 7.5 21 9 M3 14 Q6 11.5 9 11 M13.5 11 Q17.5 11.5 21 14 M7 18 Q9.5 16 12 16 Q14.5 16 17 18 M4 4 L20 20");
    public static Geometry NetworkRate { get; } = Draw("M4 17 Q4 8 12 8 Q20 8 20 17 H4 Z M12 8 V10 M6.5 10.5 L8 12 M17.5 10.5 L16 12 M12 15 L16 11");
    public static Geometry NetworkRateOff { get; } = Draw("M4 17 Q4 8 12 8 Q20 8 20 17 H4 Z M5 5 L19 19");
    public static Geometry ClockShift { get; } = Draw("M12 3.5 C16.75 3.5 20.5 7.25 20.5 12 C20.5 16.75 16.75 20.5 12 20.5 C7.25 20.5 3.5 16.75 3.5 12 C3.5 7.25 7.25 3.5 12 3.5 Z M12 7 V12 L16.5 12 M14.5 10 L16.5 12 L14.5 14");
    public static Geometry ClockRestore { get; } = Draw("M4 10 V5.5 M4 10 H8.5 M4 10 C5 6.5 8 4 12 4 C16.5 4 20 7.5 20 12 C20 16.5 16.5 20 12 20 C8.5 20 5.5 18 4.5 15 M12 7.5 V12 L15.5 14");
    public static Geometry FrameLock { get; } = Draw("M11 7 H5 Q3 7 3 9 V16 Q3 18 5 18 H19 Q21 18 21 16 V13.5 M8 21 H16 M12 18 V21 M14 7 H21 V12 H14 Z M15.5 7 V5.5 Q15.5 3.5 17.5 3.5 Q19.5 3.5 19.5 5.5 V7");
    public static Geometry FrameUnlock { get; } = Draw("M11 7 H5 Q3 7 3 9 V16 Q3 18 5 18 H19 Q21 18 21 16 V13.5 M8 21 H16 M12 18 V21 M14 7 H21 V12 H14 Z M19.5 7 V5.5 Q19.5 3.5 17.5 3.5 Q15.5 3.5 15.5 5");
    public static Geometry Parallel { get; } = Draw("M4 12 H8 Q10 12 11 10 L13 6 Q14 5 16 5 H20 M17 2 L20 5 L17 8 M8 12 Q10 12 11 14 L13 18 Q14 19 16 19 H20 M17 16 L20 19 L17 22");
    public static Geometry StopCurrent { get; } = Draw("M8 7 L3 12 L8 17 M3 12 H11 M13 8 H21 V16 H13 Z");
    public static Geometry StopIteration { get; } = Draw("M5 10 Q5 5 10 5 H20 M17 2 L20 5 L17 8 M12 19 H9 Q5 19 5 15 V13 M14 12 H20 V18 H14 Z");
    public static Geometry StopAll { get; } = Draw("M8 3.5 H16 L20.5 8 V16 L16 20.5 H8 L3.5 16 V8 Z M9 9 H15 V15 H9 Z");
    public static Geometry Media { get; } = Draw("M9 16 V5 L20 3 V13 M9 8 L20 6 M9 16 C9 17.75 7.5 19 5.5 19 C3.75 19 3 18 3 17 C3 15.5 4.5 14.5 6.5 14.5 Q9 14.5 9 16 Z M20 13 C20 14.75 18.5 16 16.5 16 C14.75 16 14 15 14 14 C14 12.5 15.5 11.5 17.5 11.5 Q20 11.5 20 13 Z");
    public static Geometry Warning { get; } = Draw("M12 3 L22 20 H2 Z M12 9 V14 M12 17 V17.25");

    public static Geometry For(MacroActionTemplateKind kind) => kind switch
    {
        MacroActionTemplateKind.Delay => Delay,
        MacroActionTemplateKind.Keyboard => Keyboard,
        MacroActionTemplateKind.MouseButton => MouseButton,
        MacroActionTemplateKind.MouseMove => MouseMove,
        MacroActionTemplateKind.MouseWheel => MouseWheel,
        MacroActionTemplateKind.WindowActivate => Window,
        MacroActionTemplateKind.OcrExtractText => OcrText,
        MacroActionTemplateKind.OcrClick => OcrClick,
        MacroActionTemplateKind.Text => Text,
        MacroActionTemplateKind.Comment => Comment,
        MacroActionTemplateKind.Macro => MacroCall,
        MacroActionTemplateKind.Loop => Loop,
        MacroActionTemplateKind.Pixel => Pixel,
        MacroActionTemplateKind.TextWhen => TextWhen,
        MacroActionTemplateKind.WeakNetStart => WeakNet,
        MacroActionTemplateKind.WeakNetStop => WeakNetOff,
        MacroActionTemplateKind.NetLimitStart or MacroActionTemplateKind.NetLimiterStart => NetworkRate,
        MacroActionTemplateKind.NetLimitStop or MacroActionTemplateKind.NetLimiterStop => NetworkRateOff,
        MacroActionTemplateKind.ClockShift => ClockShift,
        MacroActionTemplateKind.ClockRestore => ClockRestore,
        MacroActionTemplateKind.FrameLockStart or MacroActionTemplateKind.YsrFpsStart or MacroActionTemplateKind.YsrFpsToggle
            or MacroActionTemplateKind.Ky3FpsStart or MacroActionTemplateKind.FufuFpsStart => FrameLock,
        MacroActionTemplateKind.FrameLockStop or MacroActionTemplateKind.YsrFpsStop
            or MacroActionTemplateKind.Ky3FpsStop or MacroActionTemplateKind.FufuFpsStop => FrameUnlock,
        MacroActionTemplateKind.Parallel => Parallel,
        MacroActionTemplateKind.StopCurrent => StopCurrent,
        MacroActionTemplateKind.StopIteration => StopIteration,
        MacroActionTemplateKind.StopAll => StopAll,
        _ => Warning
    };

    private static Geometry Draw(string path)
    {
        var geometry = Geometry.Parse(path);
        geometry.Freeze();
        return geometry;
    }
}
