namespace MacroStudio;

internal static class TrayMinimizePolicy
{
    internal const int ScMinimize = 0xF020;

    internal readonly record struct Gesture(
        bool ProgrammaticMinimize,
        bool MonitorMoveChord,
        bool DisplayTransition,
        bool PointerOverTaskbar,
        bool WinDownMinimizeChord);

    internal static bool IsMinimizeCommand(long wParam) => (wParam & 0xFFF0) == ScMinimize;

    // Windows also sends SC_MINIMIZE while moving a window between monitors,
    // changing the display layout, or yielding to a fullscreen app.
    // Those have to stay normal minimizes so the window can come back.
    internal static bool ShouldHideToTray(Gesture gesture) =>
        !gesture.ProgrammaticMinimize
        && !gesture.MonitorMoveChord
        && !gesture.DisplayTransition
        && (gesture.PointerOverTaskbar || gesture.WinDownMinimizeChord);
}
