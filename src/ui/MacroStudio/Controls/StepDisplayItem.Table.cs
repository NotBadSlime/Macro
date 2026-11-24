using System.Globalization;
using System.Windows;
using MacroHid.Core;
using MacroStudio.Services;

namespace MacroStudio.Controls;

public sealed record StepTableCells(
    string Start,
    string StartDescription,
    string Action,
    string Parameter,
    string Indicator,
    string StateDescription,
    bool IsKey)
{
    internal static readonly StepTableCells Empty = new("", "", "", "", "", "", false);

    public string ParameterWithIndicator => string.IsNullOrEmpty(Indicator)
        ? Parameter
        : $"{Indicator} {Parameter}";

    public string AccessibleParameter => string.IsNullOrEmpty(StateDescription)
        ? Parameter
        : $"{StateDescription} {Parameter}";
}

public sealed partial class StepDisplayItem
{
    public string TableCopyLabel => L("Duplicate");
    public string TableDeleteLabel => L("Delete");

    public StepTableCells Table { get; private set; } = StepTableCells.Empty;
    public Thickness TableActionIndent => new(Math.Min(Indent * 7, 35), 0, 0, 0);
    public string AccessibleRow => string.Join(" · ", new[] { StepNumberLabel, Table.StartDescription, Table.Action, Table.AccessibleParameter }
        .Where(value => value.Length > 0));

    private static void AssignTableCells(
        IReadOnlyList<MacroStep> steps,
        IReadOnlyList<StepDisplayItem> rows,
        Func<string, string>? macroNameResolver)
    {
        var index = 0;
        AssignSchedule(steps, rows, ref index, new ScheduleRange(0, 0), macroNameResolver);
    }

    private static ScheduleRange? AssignSchedule(
        IReadOnlyList<MacroStep> steps,
        IReadOnlyList<StepDisplayItem> rows,
        ref int index,
        ScheduleRange? cursor,
        Func<string, string>? macroNameResolver)
    {
        foreach (var step in steps)
        {
            AssignRow(rows[index++], step, cursor, macroNameResolver);
            switch (step)
            {
                case RepeatStep repeat:
                    var firstPass = AssignSchedule(repeat.Steps, rows, ref index, repeat.Count > 0 ? cursor : null, macroNameResolver);
                    if (repeat.Count > 0)
                        cursor = RepeatSchedule(cursor, firstPass, repeat.Count);
                    AssignRow(rows[index++], step, cursor, macroNameResolver);
                    break;

                case PixelWhenStep pixel:
                    AssignSchedule(pixel.ThenSteps, rows, ref index, null, macroNameResolver);
                    cursor = null;
                    AssignRow(rows[index++], step, cursor, macroNameResolver);
                    break;

                case TextWhenStep text:
                    AssignSchedule(text.ThenSteps, rows, ref index, null, macroNameResolver);
                    cursor = null;
                    AssignRow(rows[index++], step, cursor, macroNameResolver);
                    break;

                case ParallelStep parallel:
                    var left = AssignSchedule([parallel.Left], rows, ref index, cursor, macroNameResolver);
                    var right = AssignSchedule([parallel.Right], rows, ref index, cursor, macroNameResolver);
                    cursor = left is { } leftEnd && right is { } rightEnd
                        ? new ScheduleRange(Math.Max(leftEnd.Min, rightEnd.Min), Math.Max(leftEnd.Max, rightEnd.Max))
                        : null;
                    AssignRow(rows[index++], step, cursor, macroNameResolver);
                    break;

                case ParallelBranchStep branch:
                    cursor = AssignSchedule(branch.Steps, rows, ref index, cursor, macroNameResolver);
                    AssignRow(rows[index++], step, cursor, macroNameResolver);
                    break;

                case MacroCallStep or OcrExtractTextStep or OcrClickStep or WindowActivateStep
                    or StopCurrentIterationStep or StopCurrentSequenceStep or StopAllSequencesStep:
                    cursor = null;
                    break;

                case WaitStep or KeyStep or MouseMoveStep or MouseButtonStep or ConsumerStep or WeakNetStep:
                    if (cursor is { } previous)
                    {
                        var duration = MacroDurationEstimator.EstimateSteps([step]);
                        cursor = new ScheduleRange(
                            LimitTicks(previous.Min + (decimal)duration.Min.Ticks),
                            LimitTicks(previous.Max + (decimal)duration.Max.Ticks));
                    }
                    break;
            }
        }
        return cursor;
    }

    private static ScheduleRange? RepeatSchedule(ScheduleRange? start, ScheduleRange? firstEnd, int count)
    {
        if (start is not { } beginning || firstEnd is not { } first) return null;
        return new ScheduleRange(
            LimitTicks(beginning.Min + ((decimal)first.Min - beginning.Min) * count),
            LimitTicks(beginning.Max + ((decimal)first.Max - beginning.Max) * count));
    }

    private static long LimitTicks(decimal ticks) => (long)Math.Clamp(ticks, 0m, long.MaxValue);

    private readonly record struct ScheduleRange(long Min, long Max);

    private static void AssignRow(
        StepDisplayItem row,
        MacroStep step,
        ScheduleRange? cursor,
        Func<string, string>? macroNameResolver) =>
        row.Table = BuildCells(row, step, cursor, macroNameResolver);

    private static StepTableCells BuildCells(
        StepDisplayItem row,
        MacroStep step,
        ScheduleRange? cursor,
        Func<string, string>? macroNameResolver)
    {
        string time = "";
        string description = "";
        if (row.ExecutionOrdinal >= 0)
        {
            time = cursor is { } position ? FormatTime(position) : "—";
            description = cursor.HasValue ? L("StepTablePlannedAt", time) : L("StepTableUnknownTime");
        }

        if (row.IsContainerEnd || row.Kind == StepDisplayKind.GroupEnd)
            return new StepTableCells(time, description, L("StepTableGroupEnd"), "", "", "", false);

        var (action, parameter) = step switch
        {
            KeyStep key => (L("StepTableKeyboard"), FormatModifiers(key.Modifiers) + KeyCaption(key.Key)),
            WaitStep wait => (L("AddDelay"), FormatWait(wait)),
            MouseMoveStep movement => (L("AddMouseMove"), JoinParameters(
                $"X {FormatCoordinate(movement.X, movement.Mode)} · Y {FormatCoordinate(movement.Y, movement.Mode)}",
                FormatDuration(movement.Duration))),
            MouseButtonStep mouse => (L("AddMouseButton"), JoinParameters(MouseCaption(mouse.Button),
                mouse.HasCoordinate ? $"X {mouse.X} · Y {mouse.Y}" : FormatDuration(mouse.Hold))),
            MouseWheelStep wheel => (L("AddWheel"), $"V {wheel.Vertical} · H {wheel.Horizontal}"),
            TextStep text => (L("AddText"), TrimText(text.Text)),
            CommentStep note => (L("AddComment"), TrimText(note.Text)),
            MacroCallStep call => (L("AddMacro"), ResolveMacroDisplayName(call.Macro, macroNameResolver)),
            RepeatStep repeat => (L("AddLoop"), $"×{repeat.Count}"),
            ParallelStep => (L("AddParallel"), row.Badge),
            ParallelBranchStep branch => (L("StepTableBranch"), branch.Name),
            WindowActivateStep window => (L("AddWindowActivate"), JoinParameters(window.ProcessName, window.WindowTitle)),
            OcrExtractTextStep extract => (L("AddOcrExtractText"), JoinParameters(TrimText(extract.Pattern), row.Badge)),
            OcrClickStep click => (L("AddOcrClick"), JoinParameters(TrimText(click.ExpectedText), row.Badge)),
            PixelWhenStep pixel => (L("AddPixel"), $"X {pixel.Condition.Coordinate.X} · Y {pixel.Condition.Coordinate.Y}"),
            TextWhenStep textMatch => (L("AddTextWhen"), TrimText(textMatch.ExpectedText)),
            ConsumerStep media => (L("StepTableMedia"), media.Control.ToString()),
            _ => (row.Title, row.Badge)
        };

        var (indicator, state) = step switch
        {
            KeyStep { Kind: KeyActionKind.Down } or MouseButtonStep { Kind: ButtonActionKind.Down }
                or ConsumerStep { Kind: ButtonActionKind.Down } => ("↓", L("StepTableDown")),
            KeyStep { Kind: KeyActionKind.Up } or MouseButtonStep { Kind: ButtonActionKind.Up }
                or ConsumerStep { Kind: ButtonActionKind.Up } => ("↑", L("StepTableUp")),
            KeyStep or MouseButtonStep or ConsumerStep => ("↓↑", L("StepTableTap")),
            _ => ("", "")
        };
        return new StepTableCells(time, description, action, parameter, indicator, state, step is KeyStep);
    }

    private static string FormatTime(ScheduleRange time)
    {
        static string Seconds(long ticks) => (ticks / (decimal)TimeSpan.TicksPerSecond).ToString("0.000", CultureInfo.InvariantCulture);
        return time.Min == time.Max ? Seconds(time.Min) : $"{Seconds(time.Min)}–{Seconds(time.Max)}";
    }

    private static string KeyCaption(HidKey key) => key switch
    {
        HidKey.PrintScreen => "PrtSc",
        HidKey.LeftControl => "Left Ctrl",
        HidKey.RightControl => "Right Ctrl",
        _ => key.ToString()
    };

    private static string MouseCaption(MouseButton button) => button switch
    {
        MouseButton.Left => L("StepTableMouseLeft"),
        MouseButton.Right => L("StepTableMouseRight"),
        MouseButton.Middle => L("StepTableMouseMiddle"),
        _ => button.ToString()
    };

    private static string FormatCoordinate(int value, MouseMoveMode mode) => mode == MouseMoveMode.Relative && value > 0 ? $"+{value}" : $"{value}";

    private static string JoinParameters(string? first, string? second) =>
        string.Join(" · ", new[] { first, second }.Where(value => !string.IsNullOrWhiteSpace(value)));

    private static string L(string key) => LocalizationService.Get(key);
    private static string L(string key, string argument) => LocalizationService.Format(key, argument);
}
