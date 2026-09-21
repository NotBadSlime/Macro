using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using MacroHid.Core;

namespace MacroHid.Converter;

internal static class GengDiJiScript
{
    private const int NormalizedMax = 65535;
    private const int DefaultTpcConfirmX = 54231;
    private const int DefaultTpcConfirmY = 60979;

    internal static (int Left, int Top, int Width, int Height)? TestScreenBounds { get; set; }

    public static bool LooksLike(string content)
    {
        return Regex.IsMatch(
            content,
            "(?is)\\b(tpc|kDown|kUp|moveR3D|moveR|map|book)\\s*\\(|\\bclick\\s*\\(\\s*\\[|\\bpress\\s*\\(\\s*['\"]");
    }

    public static MacroDocument Import(string content, string? fileName, List<MacroConversionDiagnostic> diagnostics)
    {
        var name = Path.GetFileNameWithoutExtension(fileName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "耕地机宏";
        }

        var screen = ResolveScreen();
        var steps = new List<MacroStep>();
        var lastAbsX = screen.Left + screen.Width / 2;
        var lastAbsY = screen.Top + screen.Height / 2;
        var blocks = SplitBlocks(content);
        if (blocks.Count == 0)
        {
            ParseStatements(StripComments(content), 1, screen, diagnostics, steps, ref lastAbsX, ref lastAbsY);
        }
        else
        {
            foreach (var block in blocks)
            {
                if (!string.IsNullOrWhiteSpace(block.Name))
                {
                    steps.Add(new CommentStep(block.Name));
                }

                ParseStatements(block.Body, block.LineNumber, screen, diagnostics, steps, ref lastAbsX, ref lastAbsY);
            }
        }

        return new MacroDocument(1, name, steps);
    }

    public static string Export(MacroDocument document, List<MacroConversionDiagnostic> diagnostics)
    {
        var screen = ResolveScreen();
        var builder = new StringBuilder();
        builder.Append(SanitizeBlockName(document.Name));
        builder.AppendLine(" {");

        var i = 0;
        var steps = document.Steps;
        while (i < steps.Count)
        {
            if (steps[i] is CommentStep comment)
            {
                builder.Append("  // ");
                builder.AppendLine(comment.Text.Replace("\r", " ").Replace("\n", " "));
                i++;
                continue;
            }

            var delay = 0;
            var consumed = 1;
            if (i + 1 < steps.Count && steps[i + 1] is WaitStep waitAfterAction && !waitAfterAction.IsRandom)
            {
                delay = ToDelayMs(waitAfterAction.Duration);
                consumed = 2;
            }

            if (TryExportClick(steps, i, screen, delay, out var clickLine, out var clickConsumed))
            {
                builder.Append("  ");
                builder.AppendLine(clickLine);
                i += clickConsumed;
                continue;
            }

            var line = ExportStep(steps[i], screen, delay, diagnostics);
            if (line is not null)
            {
                builder.Append("  ");
                builder.AppendLine(line);
            }

            i += consumed;
        }

        builder.AppendLine("}");
        return builder.ToString();
    }

    private static bool TryExportClick(
        IReadOnlyList<MacroStep> steps,
        int index,
        ScreenBounds screen,
        int delay,
        out string line,
        out int consumed)
    {
        line = string.Empty;
        consumed = 0;
        if (index + 2 >= steps.Count)
        {
            return false;
        }

        if (steps[index] is not MouseMoveStep { Mode: MouseMoveMode.Absolute } move
            || steps[index + 1] is not MouseButtonStep { Button: MouseButton.Left, Kind: ButtonActionKind.Down }
            || steps[index + 2] is not MouseButtonStep { Button: MouseButton.Left, Kind: ButtonActionKind.Up })
        {
            return false;
        }

        var clickDelay = delay;
        consumed = 3;
        if (index + 3 < steps.Count && steps[index + 3] is WaitStep wait && !wait.IsRandom)
        {
            clickDelay = ToDelayMs(wait.Duration);
            consumed = 4;
        }

        var (nx, ny) = ToNormalized(move.X, move.Y, screen);
        line = $"click([{nx}, {ny}], {clickDelay});";
        return true;
    }

    private static string? ExportStep(
        MacroStep step,
        ScreenBounds screen,
        int delay,
        List<MacroConversionDiagnostic> diagnostics)
    {
        switch (step)
        {
            case WaitStep wait when !wait.IsRandom:
                return $"wait({ToDelayMs(wait.Duration)});";
            case KeyStep { Kind: KeyActionKind.Down } key:
                return $"kDown('{ToGengDiJiKey(key.Key, key.Modifiers)}', {delay});";
            case KeyStep { Kind: KeyActionKind.Up } key:
                return $"kUp('{ToGengDiJiKey(key.Key, key.Modifiers)}', {delay});";
            case KeyStep { Kind: KeyActionKind.Tap } key:
                return $"press('{ToGengDiJiKey(key.Key, key.Modifiers)}', {delay});";
            case MouseMoveStep { Mode: MouseMoveMode.Absolute } move:
                var (nx, ny) = ToNormalized(move.X, move.Y, screen);
                return $"move([{nx}, {ny}], {delay});";
            case MouseMoveStep { Mode: MouseMoveMode.Relative } move:
                return $"moveR3D([{move.X}, {move.Y}], {delay});";
            case MouseButtonStep { Kind: ButtonActionKind.Down } button:
                return button.Button == MouseButton.Left
                    ? $"mDown({delay});"
                    : $"mDown('{ToGengDiJiMouse(button.Button)}', {delay});";
            case MouseButtonStep { Kind: ButtonActionKind.Up } button:
                return button.Button == MouseButton.Left
                    ? $"mUp({delay});"
                    : $"mUp('{ToGengDiJiMouse(button.Button)}', {delay});";
            case MouseButtonStep { Kind: ButtonActionKind.Click, Button: MouseButton.Left, HasCoordinate: true } click:
                var (cx, cy) = ToNormalized(click.X!.Value, click.Y!.Value, screen);
                return $"click([{cx}, {cy}], {delay});";
            default:
                diagnostics.Warning("gengdiji.unsupportedStep", $"步骤 '{step.GetType().Name}' 无法导出为耕地机脚本，已跳过。");
                return null;
        }
    }

    private static void ParseStatements(
        string body,
        int startLine,
        ScreenBounds screen,
        List<MacroConversionDiagnostic> diagnostics,
        List<MacroStep> steps,
        ref int lastAbsX,
        ref int lastAbsY)
    {
        var text = StripComments(body);
        var i = 0;
        while (i < text.Length)
        {
            SkipSpace(text, ref i);
            if (i >= text.Length)
            {
                break;
            }

            if (text[i] is ';' or ',')
            {
                i++;
                continue;
            }

            var line = startLine + CountNewLines(text, 0, i);
            if (!TryReadIdentifier(text, ref i, out var name))
            {
                diagnostics.Warning("gengdiji.skippedText", $"无法解析的耕地机语句已跳过。", line);
                SkipUntilStatementEnd(text, ref i);
                continue;
            }

            name = name.ToLowerInvariant();
            SkipSpace(text, ref i);
            if (i >= text.Length || text[i] != '(')
            {
                diagnostics.Warning("gengdiji.missingCall", $"'{name}' 不是函数调用，已跳过。", line);
                continue;
            }

            i++;
            var args = ReadArguments(text, ref i, line, diagnostics);
            SkipSpace(text, ref i);
            if (i < text.Length && text[i] == ';')
            {
                i++;
            }

            AppendCall(name, args, line, screen, diagnostics, steps, ref lastAbsX, ref lastAbsY);
        }
    }

    private static void AppendCall(
        string name,
        List<GengDiJiArg> args,
        int line,
        ScreenBounds screen,
        List<MacroConversionDiagnostic> diagnostics,
        List<MacroStep> steps,
        ref int lastAbsX,
        ref int lastAbsY)
    {
        switch (name)
        {
            case "wait":
                steps.Add(new WaitStep(TimeSpan.FromMilliseconds(FirstNumber(args))));
                return;
            case "map":
                AppendKeyTap(HidKey.M, FirstNumber(args), steps);
                return;
            case "book":
                AppendKeyTap(HidKey.F1, FirstNumber(args), steps);
                return;
            case "press":
                if (!TryParseKeyArg(args, 0, out var pressKey, diagnostics, line))
                {
                    return;
                }

                AppendKeyTap(pressKey, NumberAt(args, 1), steps);
                return;
            case "kdown":
                if (!TryParseKeyArg(args, 0, out var downKey, diagnostics, line))
                {
                    return;
                }

                steps.Add(new KeyStep(KeyActionKind.Down, downKey, HidModifier.None, TimeSpan.Zero));
                AppendDelay(NumberAt(args, 1), steps);
                return;
            case "kup":
                if (!TryParseKeyArg(args, 0, out var upKey, diagnostics, line))
                {
                    return;
                }

                steps.Add(new KeyStep(KeyActionKind.Up, upKey, HidModifier.None, TimeSpan.Zero));
                AppendDelay(NumberAt(args, 1), steps);
                return;
            case "click":
                if (!TryPointArg(args, 0, out var clickX, out var clickY))
                {
                    diagnostics.Warning("gengdiji.badClick", "click 需要坐标数组。", line);
                    return;
                }

                AppendAbsoluteClick(clickX, clickY, NumberAt(args, 1), screen, steps, ref lastAbsX, ref lastAbsY);
                return;
            case "tpc":
                if (!TryPointArg(args, 0, out var tpcX, out var tpcY))
                {
                    diagnostics.Warning("gengdiji.badTpc", "tpc 需要坐标数组。", line);
                    return;
                }

                var tpcDelay = NumberAt(args, 1);
                AppendAbsoluteClick(tpcX, tpcY, tpcDelay, screen, steps, ref lastAbsX, ref lastAbsY);
                AppendAbsoluteClick(DefaultTpcConfirmX, DefaultTpcConfirmY, tpcDelay, screen, steps, ref lastAbsX, ref lastAbsY);
                diagnostics.Warning("gengdiji.tpcConfirm", "tpc 已展开为选点点击 + 常见传送确认点点击（约 54231,60979）。", line);
                return;
            case "mdown":
                steps.Add(new MouseButtonStep(ParseMouseArg(args), ButtonActionKind.Down, TimeSpan.Zero));
                AppendDelay(LastNumber(args), steps);
                return;
            case "mup":
                steps.Add(new MouseButtonStep(ParseMouseArg(args), ButtonActionKind.Up, TimeSpan.Zero));
                AppendDelay(LastNumber(args), steps);
                return;
            case "move":
                AppendMove(args, MouseMoveMode.Absolute, convertNormalized: true, screen, diagnostics, line, steps, ref lastAbsX, ref lastAbsY);
                return;
            case "mover":
                AppendMove(args, MouseMoveMode.Relative, convertNormalized: true, screen, diagnostics, line, steps, ref lastAbsX, ref lastAbsY);
                return;
            case "mover3d":
                AppendMove(args, MouseMoveMode.Relative, convertNormalized: false, screen, diagnostics, line, steps, ref lastAbsX, ref lastAbsY);
                return;
            case "drag":
                AppendDrag(args, screen, diagnostics, line, steps, ref lastAbsX, ref lastAbsY);
                return;
            default:
                diagnostics.Warning("gengdiji.unsupportedCall", $"未支持的耕地机函数 '{name}' 已跳过。", line);
                return;
        }
    }

    private static void AppendMove(
        List<GengDiJiArg> args,
        MouseMoveMode mode,
        bool convertNormalized,
        ScreenBounds screen,
        List<MacroConversionDiagnostic> diagnostics,
        int line,
        List<MacroStep> steps,
        ref int lastAbsX,
        ref int lastAbsY)
    {
        if (!TryPointArg(args, 0, out var x, out var y))
        {
            diagnostics.Warning("gengdiji.badMove", "move 需要坐标数组。", line);
            return;
        }

        var rest = args.Skip(1).Select(arg => arg.Number ?? 0).ToList();
        var stepsCount = rest.Count >= 3 ? Math.Max(1, (int)rest[0]) : 1;
        var stepDelay = rest.Count >= 3 ? rest[1] : 0;
        var finalDelay = rest.Count switch
        {
            0 => 0,
            1 => rest[0],
            2 => rest[1],
            _ => rest[2]
        };

        var (targetX, targetY) = mode == MouseMoveMode.Absolute && convertNormalized
            ? ToPixels(x, y, screen)
            : mode == MouseMoveMode.Relative && convertNormalized
                ? ToRelativePixels(x, y, screen)
                : ((int)Math.Round(x), (int)Math.Round(y));

        if (mode == MouseMoveMode.Absolute && stepsCount > 1)
        {
            for (var n = 1; n <= stepsCount; n++)
            {
                var px = lastAbsX + (targetX - lastAbsX) * n / stepsCount;
                var py = lastAbsY + (targetY - lastAbsY) * n / stepsCount;
                steps.Add(new MouseMoveStep(MouseMoveMode.Absolute, px, py, TimeSpan.Zero));
                if (n < stepsCount)
                {
                    AppendDelay(stepDelay, steps);
                }
            }

            lastAbsX = targetX;
            lastAbsY = targetY;
            AppendDelay(finalDelay, steps);
            return;
        }

        steps.Add(new MouseMoveStep(mode, targetX, targetY, TimeSpan.Zero));
        if (mode == MouseMoveMode.Absolute)
        {
            lastAbsX = targetX;
            lastAbsY = targetY;
        }

        AppendDelay(finalDelay + (stepsCount > 1 ? stepDelay * (stepsCount - 1) : 0), steps);
    }

    private static void AppendDrag(
        List<GengDiJiArg> args,
        ScreenBounds screen,
        List<MacroConversionDiagnostic> diagnostics,
        int line,
        List<MacroStep> steps,
        ref int lastAbsX,
        ref int lastAbsY)
    {
        if (args.Count == 0 || args[0].Numbers is not { Count: >= 4 } numbers || numbers.Count % 4 != 0)
        {
            diagnostics.Warning("gengdiji.badDrag", "drag 需要每四个整数一组的坐标。", line);
            return;
        }

        var firstPixels = args.Count > 1 ? Math.Max(0, (int)(args[1].Number ?? 0)) : 0;
        var delay = args.Count > 2 ? args[2].Number ?? 0 : 0;
        for (var offset = 0; offset < numbers.Count; offset += 4)
        {
            var (x1, y1) = ToPixels(numbers[offset], numbers[offset + 1], screen);
            var (x2, y2) = ToPixels(numbers[offset + 2], numbers[offset + 3], screen);
            steps.Add(new MouseMoveStep(MouseMoveMode.Absolute, x1, y1, TimeSpan.Zero));
            steps.Add(new MouseButtonStep(MouseButton.Left, ButtonActionKind.Down, TimeSpan.Zero));
            AppendInterpolatedAbsolute(x1, y1, x2, y2, firstPixels, steps);
            steps.Add(new MouseButtonStep(MouseButton.Left, ButtonActionKind.Up, TimeSpan.Zero));
            lastAbsX = x2;
            lastAbsY = y2;
        }

        AppendDelay(delay, steps);
    }

    private static void AppendInterpolatedAbsolute(int x1, int y1, int x2, int y2, int firstPixels, List<MacroStep> steps)
    {
            var distance = Math.Sqrt((x2 - x1) * (double)(x2 - x1) + (y2 - y1) * (double)(y2 - y1));
        var count = firstPixels <= 0 ? 1 : Math.Max(1, (int)Math.Ceiling(distance / firstPixels));
        for (var n = 1; n <= count; n++)
        {
            var x = x1 + (x2 - x1) * n / count;
            var y = y1 + (y2 - y1) * n / count;
            steps.Add(new MouseMoveStep(MouseMoveMode.Absolute, x, y, TimeSpan.Zero));
        }
    }

    private static void AppendAbsoluteClick(
        double nx,
        double ny,
        double delay,
        ScreenBounds screen,
        List<MacroStep> steps,
        ref int lastAbsX,
        ref int lastAbsY)
    {
        var (x, y) = ToPixels(nx, ny, screen);
        lastAbsX = x;
        lastAbsY = y;
        steps.Add(new MouseMoveStep(MouseMoveMode.Absolute, x, y, TimeSpan.Zero));
        steps.Add(new MouseButtonStep(MouseButton.Left, ButtonActionKind.Down, TimeSpan.Zero));
        steps.Add(new MouseButtonStep(MouseButton.Left, ButtonActionKind.Up, TimeSpan.Zero));
        AppendDelay(delay, steps);
    }

    private static void AppendKeyTap(HidKey key, double delay, List<MacroStep> steps)
    {
        steps.Add(new KeyStep(KeyActionKind.Down, key, HidModifier.None, TimeSpan.Zero));
        steps.Add(new KeyStep(KeyActionKind.Up, key, HidModifier.None, TimeSpan.Zero));
        AppendDelay(delay, steps);
    }

    private static void AppendDelay(double ms, List<MacroStep> steps)
    {
        if (ms > 0)
        {
            steps.Add(new WaitStep(TimeSpan.FromMilliseconds(ms)));
        }
    }

    private static bool TryParseKeyArg(
        List<GengDiJiArg> args,
        int index,
        out HidKey key,
        List<MacroConversionDiagnostic> diagnostics,
        int line)
    {
        key = HidKey.None;
        var token = index < args.Count ? args[index].Text ?? args[index].Identifier : null;
        if (string.IsNullOrWhiteSpace(token) || !MacroConversionService.TryParseGengDiJiKey(token, out key))
        {
            diagnostics.Warning("gengdiji.badKey", $"无法识别按键 '{token}'。", line);
            return false;
        }

        return true;
    }

    private static MouseButton ParseMouseArg(List<GengDiJiArg> args)
    {
        var token = args.Select(arg => arg.Text ?? arg.Identifier).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        return token?.Trim().ToLowerInvariant() switch
        {
            "right" or "r" or "rmb" => MouseButton.Right,
            "middle" or "m" or "mmb" => MouseButton.Middle,
            "x1" => MouseButton.X1,
            "x2" => MouseButton.X2,
            _ => MouseButton.Left
        };
    }

    private static bool TryPointArg(List<GengDiJiArg> args, int index, out double x, out double y)
    {
        x = 0;
        y = 0;
        if (index >= args.Count || args[index].Numbers is not { Count: >= 2 } numbers)
        {
            return false;
        }

        x = numbers[0];
        y = numbers[1];
        return true;
    }

    private static double FirstNumber(List<GengDiJiArg> args) => args.Count == 0 ? 0 : args[0].Number ?? 0;

    private static double NumberAt(List<GengDiJiArg> args, int index) =>
        index < args.Count ? args[index].Number ?? 0 : 0;

    private static double LastNumber(List<GengDiJiArg> args) =>
        args.Select(arg => arg.Number).LastOrDefault(value => value is not null) ?? 0;

    private static List<GengDiJiArg> ReadArguments(
        string text,
        ref int i,
        int line,
        List<MacroConversionDiagnostic> diagnostics)
    {
        var args = new List<GengDiJiArg>();
        while (i < text.Length)
        {
            SkipSpace(text, ref i);
            if (i >= text.Length)
            {
                break;
            }

            if (text[i] == ')')
            {
                i++;
                return args;
            }

            if (text[i] == ',')
            {
                i++;
                continue;
            }

            args.Add(ReadArgument(text, ref i, line, diagnostics));
        }

        diagnostics.Warning("gengdiji.unclosedCall", "函数参数列表缺少右括号。", line);
        return args;
    }

    private static GengDiJiArg ReadArgument(string text, ref int i, int line, List<MacroConversionDiagnostic> diagnostics)
    {
        SkipSpace(text, ref i);
        if (i >= text.Length)
        {
            return new GengDiJiArg(null, null, null, null);
        }

        if (text[i] == '[')
        {
            i++;
            var numbers = new List<double>();
            while (i < text.Length && text[i] != ']')
            {
                SkipSpace(text, ref i);
                if (i < text.Length && (text[i] == ',' || text[i] == ';'))
                {
                    i++;
                    continue;
                }

                if (TryReadNumber(text, ref i, out var number))
                {
                    numbers.Add(number);
                    continue;
                }

                i++;
            }

            if (i < text.Length && text[i] == ']')
            {
                i++;
            }

            return new GengDiJiArg(null, null, null, numbers);
        }

        if (text[i] is '\'' or '"')
        {
            var quote = text[i++];
            var start = i;
            while (i < text.Length && text[i] != quote)
            {
                i++;
            }

            var value = text[start..i];
            if (i < text.Length)
            {
                i++;
            }

            return new GengDiJiArg(null, value, null, null);
        }

        if (TryReadNumber(text, ref i, out var numeric))
        {
            return new GengDiJiArg(numeric, null, null, null);
        }

        if (TryReadIdentifier(text, ref i, out var ident))
        {
            return new GengDiJiArg(null, null, ident, null);
        }

        diagnostics.Warning("gengdiji.badArg", "无法解析函数参数。", line);
        i++;
        return new GengDiJiArg(null, null, null, null);
    }

    private static bool TryReadIdentifier(string text, ref int i, out string name)
    {
        name = string.Empty;
        if (i >= text.Length || !char.IsLetter(text[i]) && text[i] != '_')
        {
            return false;
        }

        var start = i;
        i++;
        while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
        {
            i++;
        }

        name = text[start..i];
        return true;
    }

    private static bool TryReadNumber(string text, ref int i, out double value)
    {
        value = 0;
        var start = i;
        if (i < text.Length && text[i] is '+' or '-')
        {
            i++;
        }

        var digits = false;
        while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.'))
        {
            digits = true;
            i++;
        }

        if (!digits)
        {
            i = start;
            return false;
        }

        return double.TryParse(text[start..i], NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static void SkipSpace(string text, ref int i)
    {
        while (i < text.Length && char.IsWhiteSpace(text[i]))
        {
            i++;
        }
    }

    private static void SkipUntilStatementEnd(string text, ref int i)
    {
        while (i < text.Length && text[i] is not ';' and not '\n' and not '{')
        {
            i++;
        }

        if (i < text.Length && text[i] == ';')
        {
            i++;
        }
    }

    private static List<GengDiJiBlock> SplitBlocks(string content)
    {
        var blocks = new List<GengDiJiBlock>();
        var i = 0;
        while (i < content.Length)
        {
            var brace = content.IndexOf('{', i);
            if (brace < 0)
            {
                break;
            }

            var close = FindMatchingBrace(content, brace);
            if (close < 0)
            {
                break;
            }

            var name = StripComments(content[i..brace]).Trim().Trim(';', ',');
            name = Regex.Replace(name, @"\s+", " ").Trim();
            var line = 1 + CountNewLines(content, 0, brace);
            blocks.Add(new GengDiJiBlock(name, content[(brace + 1)..close], line));
            i = close + 1;
        }

        return blocks;
    }

    private static int FindMatchingBrace(string content, int open)
    {
        var depth = 0;
        var inLineComment = false;
        var inBlockComment = false;
        char? quote = null;
        for (var i = open; i < content.Length; i++)
        {
            var ch = content[i];
            if (inLineComment)
            {
                if (ch == '\n')
                {
                    inLineComment = false;
                }

                continue;
            }

            if (inBlockComment)
            {
                if (ch == '*' && i + 1 < content.Length && content[i + 1] == '/')
                {
                    inBlockComment = false;
                    i++;
                }

                continue;
            }

            if (quote is not null)
            {
                if (ch == quote)
                {
                    quote = null;
                }

                continue;
            }

            if (ch is '\'' or '"')
            {
                quote = ch;
                continue;
            }

            if (ch == '/' && i + 1 < content.Length && content[i + 1] == '/')
            {
                inLineComment = true;
                i++;
                continue;
            }

            if (ch == '/' && i + 1 < content.Length && content[i + 1] == '*')
            {
                inBlockComment = true;
                i++;
                continue;
            }

            if (ch == '{')
            {
                depth++;
            }
            else if (ch == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static string StripComments(string content)
    {
        var builder = new StringBuilder(content.Length);
        var inLineComment = false;
        var inBlockComment = false;
        char? quote = null;
        for (var i = 0; i < content.Length; i++)
        {
            var ch = content[i];
            if (inLineComment)
            {
                if (ch == '\n')
                {
                    inLineComment = false;
                    builder.Append(ch);
                }

                continue;
            }

            if (inBlockComment)
            {
                if (ch == '*' && i + 1 < content.Length && content[i + 1] == '/')
                {
                    inBlockComment = false;
                    i++;
                }
                else if (ch == '\n')
                {
                    builder.Append(ch);
                }

                continue;
            }

            if (quote is not null)
            {
                builder.Append(ch);
                if (ch == quote)
                {
                    quote = null;
                }

                continue;
            }

            if (ch is '\'' or '"')
            {
                quote = ch;
                builder.Append(ch);
                continue;
            }

            if (ch == '/' && i + 1 < content.Length && content[i + 1] == '/')
            {
                inLineComment = true;
                i++;
                continue;
            }

            if (ch == '/' && i + 1 < content.Length && content[i + 1] == '*')
            {
                inBlockComment = true;
                i++;
                continue;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    private static int CountNewLines(string text, int start, int end)
    {
        var count = 0;
        var last = Math.Min(end, text.Length);
        for (var i = start; i < last; i++)
        {
            if (text[i] == '\n')
            {
                count++;
            }
        }

        return count;
    }

    private static ScreenBounds ResolveScreen()
    {
        if (TestScreenBounds is { } test)
        {
            return new ScreenBounds(test.Left, test.Top, test.Width, test.Height);
        }

        return new ScreenBounds(
            GetSystemMetrics(76),
            GetSystemMetrics(77),
            Math.Max(1, GetSystemMetrics(78)),
            Math.Max(1, GetSystemMetrics(79)));
    }

    private static (int X, int Y) ToPixels(double nx, double ny, ScreenBounds screen) =>
        (ToPixel(nx, screen.Left, screen.Width), ToPixel(ny, screen.Top, screen.Height));

    private static (int X, int Y) ToRelativePixels(double nx, double ny, ScreenBounds screen) =>
        (ToRelativePixel(nx, screen.Width), ToRelativePixel(ny, screen.Height));

    private static (int X, int Y) ToNormalized(int x, int y, ScreenBounds screen) =>
        (ToNormalized(x, screen.Left, screen.Width), ToNormalized(y, screen.Top, screen.Height));

    internal static int ToPixel(double normalized, int origin, int size)
    {
        if (size <= 1)
        {
            return origin;
        }

        return origin + (int)Math.Round(normalized * (size - 1) / NormalizedMax, MidpointRounding.AwayFromZero);
    }

    internal static int ToNormalized(int pixel, int origin, int size)
    {
        if (size <= 1)
        {
            return 0;
        }

        return (int)Math.Round((pixel - origin) * (double)NormalizedMax / (size - 1), MidpointRounding.AwayFromZero);
    }

    private static int ToRelativePixel(double normalized, int size)
    {
        if (size <= 1)
        {
            return 0;
        }

        return (int)Math.Round(normalized * (size - 1) / NormalizedMax, MidpointRounding.AwayFromZero);
    }

    private static int ToDelayMs(TimeSpan duration) =>
        (int)Math.Round(duration.TotalMilliseconds, MidpointRounding.AwayFromZero);

    private static string ToGengDiJiKey(HidKey key, HidModifier modifiers)
    {
        var name = key switch
        {
            HidKey.Escape => "esc",
            HidKey.Enter => "enter",
            HidKey.Space => "space",
            HidKey.Tab => "tab",
            HidKey.LeftAlt or HidKey.RightAlt => "alt",
            HidKey.LeftControl or HidKey.RightControl => "ctrl",
            HidKey.LeftShift or HidKey.RightShift => "shift",
            HidKey.LeftGui or HidKey.RightGui => "win",
            _ => key.ToString().ToLowerInvariant()
        };

        if ((modifiers & (HidModifier.LeftCtrl | HidModifier.RightCtrl)) != 0 && key is not (HidKey.LeftControl or HidKey.RightControl))
        {
            return "ctrl+" + name;
        }

        return name;
    }

    private static string ToGengDiJiMouse(MouseButton button) => button switch
    {
        MouseButton.Right => "right",
        MouseButton.Middle => "middle",
        MouseButton.X1 => "x1",
        MouseButton.X2 => "x2",
        _ => "left"
    };

    private static string SanitizeBlockName(string name)
    {
        var trimmed = string.IsNullOrWhiteSpace(name) ? "宏" : name.Trim();
        return Regex.Replace(trimmed, @"[{}]", string.Empty);
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    private sealed record ScreenBounds(int Left, int Top, int Width, int Height);

    private sealed record GengDiJiBlock(string Name, string Body, int LineNumber);

    private sealed record GengDiJiArg(double? Number, string? Text, string? Identifier, IReadOnlyList<double>? Numbers);
}

internal static class GengDiJiDiagnosticExtensions
{
    public static void Warning(
        this List<MacroConversionDiagnostic> diagnostics,
        string code,
        string message,
        int? line = null)
    {
        diagnostics.Add(new MacroConversionDiagnostic(MacroDiagnosticSeverity.Warning, code, message, line));
    }
}
