using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using MacroHid.Core;

namespace MacroHid.Converter;

public static class MacroConversionService
{
    private static readonly IReadOnlyList<MacroFormatInfo> FormatInfos =
    [
        new(MacroConversionFormat.MacroHidMcrx, "MacroHID MCRX", ".mcrx", "MacroHID macro (*.mcrx)|*.mcrx", true, true),
        new(MacroConversionFormat.MacroConverterXml, "MacroConverter XML", ".xml", "MacroConverter XML (*.xml)|*.xml", true, true),
        new(MacroConversionFormat.RazerSynapseXml, "Razer Synapse XML", ".xml", "Razer Synapse XML (*.xml)|*.xml", true, true),
        new(MacroConversionFormat.Lua, "Lua / Logitech Lua", ".lua", "Lua macro (*.lua)|*.lua", true, true),
        new(MacroConversionFormat.XMouse, "XMouse", ".xmbcs", "XMouse profile (*.xmbcs;*.xml)|*.xmbcs;*.xml", true, true),
        new(MacroConversionFormat.QMacro, "QMacro / 按键精灵", ".mq", "QMacro script (*.mq;*.txt)|*.mq;*.txt", true, true)
    ];

    public static IReadOnlyList<MacroFormatInfo> GetFormats()
    {
        return FormatInfos;
    }

    public static MacroConversionFormat DetectFormat(string content, string? fileName = null)
    {
        var trimmed = content.TrimStart();
        var extension = Path.GetExtension(fileName ?? string.Empty);

        if (extension.Equals(".mcrx", StringComparison.OrdinalIgnoreCase)
            || (trimmed.StartsWith('{') && Regex.IsMatch(content, "\"steps\"\\s*:", RegexOptions.IgnoreCase)))
        {
            return MacroConversionFormat.MacroHidMcrx;
        }

        if (Regex.IsMatch(content, "<MacroConverterMacro\\b", RegexOptions.IgnoreCase))
        {
            return MacroConversionFormat.MacroConverterXml;
        }

        if (Regex.IsMatch(content, "<Macro>\\s*<Name>[\\s\\S]*?<MacroEvents>", RegexOptions.IgnoreCase)
            || Regex.IsMatch(content, "<Version>\\s*4\\s*</Version>", RegexOptions.IgnoreCase)
            && Regex.IsMatch(content, "<MacroEvents>", RegexOptions.IgnoreCase))
        {
            return MacroConversionFormat.RazerSynapseXml;
        }

        if (Regex.IsMatch(content, "\\b(macro\\.(begin|move|click|wait|key|text|loop)|PressMouseButton|ReleaseMouseButton|Sleep|PressAndReleaseKey|OnEvent)\\b", RegexOptions.IgnoreCase)
            || extension.Equals(".lua", StringComparison.OrdinalIgnoreCase))
        {
            return MacroConversionFormat.Lua;
        }

        if (Regex.IsMatch(content, "<XMouse(Profile|ButtonControl)?\\b", RegexOptions.IgnoreCase)
            || Regex.IsMatch(content, "\\{(?:WAITMS:\\d+|LMB|RMB|MMB|LMBD|LMBU|RMBD|RMBU|MMBD|MMBU)\\}", RegexOptions.IgnoreCase)
            || extension.Equals(".xmbcs", StringComparison.OrdinalIgnoreCase))
        {
            return MacroConversionFormat.XMouse;
        }

        if (Regex.IsMatch(content, "^\\s*(MoveTo|LeftClick|RightClick|MiddleClick|LeftDown|LeftUp|RightDown|RightUp|Delay|KeyPress|SayString)\\b", RegexOptions.IgnoreCase | RegexOptions.Multiline)
            || extension.Equals(".mq", StringComparison.OrdinalIgnoreCase))
        {
            return MacroConversionFormat.QMacro;
        }

        return MacroConversionFormat.Auto;
    }

    public static MacroImportResult ImportToMcrx(MacroImportRequest request)
    {
        var diagnostics = new List<MacroConversionDiagnostic>();
        var format = request.Format == MacroConversionFormat.Auto
            ? DetectFormat(request.Content, request.FileName)
            : request.Format;

        if (format == MacroConversionFormat.Auto)
        {
            throw new FormatException("Could not detect macro format.");
        }

        var document = format switch
        {
            MacroConversionFormat.MacroHidMcrx => McrxParser.Parse(request.Content),
            MacroConversionFormat.MacroConverterXml => ImportMacroConverterXml(request.Content, diagnostics),
            MacroConversionFormat.RazerSynapseXml => ImportRazerXml(request.Content, request.AuxiliaryFiles ?? [], diagnostics),
            MacroConversionFormat.Lua => ImportLua(request.Content, diagnostics),
            MacroConversionFormat.XMouse => ImportXMouse(request.Content, diagnostics),
            MacroConversionFormat.QMacro => ImportQMacro(request.Content, diagnostics),
            _ => throw new NotSupportedException($"Unsupported import format '{format}'.")
        };

        return new MacroImportResult(document, format, diagnostics);
    }

    public static MacroExportResult ExportFromMcrx(MacroDocument document, MacroConversionFormat targetFormat, string? sourceFileName = null)
    {
        if (targetFormat == MacroConversionFormat.Auto)
        {
            throw new ArgumentException("Export target format cannot be Auto.", nameof(targetFormat));
        }

        var diagnostics = new List<MacroConversionDiagnostic>();
        var output = targetFormat switch
        {
            MacroConversionFormat.MacroHidMcrx => McrxSerializer.Serialize(document),
            MacroConversionFormat.MacroConverterXml => ExportMacroConverterXml(document, diagnostics),
            MacroConversionFormat.RazerSynapseXml => ExportRazerXml(document, diagnostics),
            MacroConversionFormat.Lua => ExportLua(document, diagnostics),
            MacroConversionFormat.XMouse => ExportXMouse(document, diagnostics),
            MacroConversionFormat.QMacro => ExportQMacro(document, diagnostics),
            _ => throw new NotSupportedException($"Unsupported export format '{targetFormat}'.")
        };

        return new MacroExportResult(output, targetFormat, diagnostics, BuildExportFileName(document.Name, targetFormat, sourceFileName));
    }

    public static string BuildExportFileName(string macroName, MacroConversionFormat format, string? sourceFileName = null)
    {
        var baseName = Path.GetFileNameWithoutExtension(sourceFileName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = SanitizeFileName(macroName);
        }

        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "macro";
        }

        return baseName + GetDefaultExtension(format);
    }

    public static string GetDefaultExtension(MacroConversionFormat format)
    {
        return FormatInfos.FirstOrDefault(item => item.Format == format)?.DefaultExtension ?? ".txt";
    }

    private static MacroDocument ImportQMacro(string content, List<MacroConversionDiagnostic> diagnostics)
    {
        var steps = new List<MacroStep>();
        foreach (var rawLine in content.SplitLines())
        {
            var line = NormalizeLine(rawLine);
            if (line.Length == 0 || line.StartsWith('\''))
            {
                continue;
            }

            Match match;
            if ((match = Regex.Match(line, "^MoveTo\\s+(-?\\d+)\\s*,\\s*(-?\\d+)", RegexOptions.IgnoreCase)).Success)
            {
                steps.Add(new MouseMoveStep(MouseMoveMode.Absolute, ToInt(match.Groups[1].Value), ToInt(match.Groups[2].Value), TimeSpan.Zero));
                continue;
            }

            if ((match = Regex.Match(line, "^(Left|Right|Middle)Click\\s*(\\d+)?", RegexOptions.IgnoreCase)).Success)
            {
                AddRepeated(steps, ToInt(match.Groups[2].Value, 1), new MouseButtonStep(ParseMouseButton(match.Groups[1].Value), ButtonActionKind.Click, TimeSpan.Zero));
                continue;
            }

            if ((match = Regex.Match(line, "^(Left|Right|Middle)(Down|Up)\\b", RegexOptions.IgnoreCase)).Success)
            {
                steps.Add(new MouseButtonStep(ParseMouseButton(match.Groups[1].Value), match.Groups[2].Value.Equals("Down", StringComparison.OrdinalIgnoreCase) ? ButtonActionKind.Down : ButtonActionKind.Up, TimeSpan.Zero));
                continue;
            }

            if ((match = Regex.Match(line, "^Delay\\s+(\\d+)", RegexOptions.IgnoreCase)).Success)
            {
                steps.Add(new WaitStep(TimeSpan.FromMilliseconds(ToInt(match.Groups[1].Value))));
                continue;
            }

            if ((match = Regex.Match(line, "^KeyPress\\s+\"?([^\",]+)\"?\\s*,?\\s*(\\d+)?", RegexOptions.IgnoreCase)).Success)
            {
                if (TryParseKey(match.Groups[1].Value, out var key, out var modifiers))
                {
                    AddRepeated(steps, ToInt(match.Groups[2].Value, 1), new KeyStep(KeyActionKind.Tap, key, modifiers, TimeSpan.Zero));
                }
                else
                {
                    diagnostics.Warning("qmacro.unsupportedKey", $"Unsupported QMacro key '{match.Groups[1].Value}' was skipped.");
                }

                continue;
            }

            if ((match = Regex.Match(line, "^SayString\\s+\"?(.+?)\"?$", RegexOptions.IgnoreCase)).Success)
            {
                steps.Add(new TextStep(UnescapeText(match.Groups[1].Value)));
                continue;
            }
        }

        return new MacroDocument(1, "QMacro script", steps);
    }

    private static MacroDocument ImportLua(string content, List<MacroConversionDiagnostic> diagnostics)
    {
        var name = Regex.Match(content, "macro\\.begin\\(\"([^\"]+)\"\\)", RegexOptions.IgnoreCase).Groups[1].Value;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = Regex.IsMatch(content, "\\bOnEvent\\b", RegexOptions.IgnoreCase) ? "Logitech Lua script" : "Lua script";
        }

        var lines = content.SplitLines()
            .Select(line => Regex.Replace(line, "--.*$", string.Empty).Trim())
            .Where(line => line.Length > 0)
            .ToArray();
        var index = 0;
        var steps = ParseLuaBlock(lines, ref index, diagnostics, stopAtEndLoop: false);

        if (Regex.IsMatch(content, "\\b(OnEvent|IsMouseButtonPressed|while\\s+)\\b", RegexOptions.IgnoreCase))
        {
            diagnostics.Warning("lua.dynamicControl", "Dynamic Lua control flow was flattened to the visible action sequence.");
        }

        return new MacroDocument(1, name, steps);
    }

    private static List<MacroStep> ParseLuaBlock(string[] lines, ref int index, List<MacroConversionDiagnostic> diagnostics, bool stopAtEndLoop)
    {
        var steps = new List<MacroStep>();
        for (; index < lines.Length; index++)
        {
            var line = lines[index];
            if (stopAtEndLoop && Regex.IsMatch(line, "^macro\\.endLoop\\(\\)", RegexOptions.IgnoreCase))
            {
                return steps;
            }

            if (Regex.IsMatch(line, "^(macro\\.begin|macro\\.finish|function|end\\b|return\\b)", RegexOptions.IgnoreCase))
            {
                continue;
            }

            Match match;
            if ((match = Regex.Match(line, "^macro\\.loop\\((\\d+)\\)", RegexOptions.IgnoreCase)).Success)
            {
                index++;
                var body = ParseLuaBlock(lines, ref index, diagnostics, stopAtEndLoop: true);
                steps.Add(new RepeatStep(ToInt(match.Groups[1].Value, 1), body));
                continue;
            }

            if (Regex.IsMatch(line, "^if\\s+macro\\.condition", RegexOptions.IgnoreCase))
            {
                diagnostics.Warning("lua.flowIf", "Lua flow.if cannot be represented directly in MCRX import; inner safe actions are kept when visible.");
                continue;
            }

            if ((match = Regex.Match(line, "^macro\\.move\\((-?\\d+)\\s*,\\s*(-?\\d+)\\)", RegexOptions.IgnoreCase)).Success)
            {
                steps.Add(new MouseMoveStep(MouseMoveMode.Absolute, ToInt(match.Groups[1].Value), ToInt(match.Groups[2].Value), TimeSpan.Zero));
                continue;
            }

            if ((match = Regex.Match(line, "^macro\\.click\\(\"([^\"]+)\"\\s*,\\s*(\\d+)\\)", RegexOptions.IgnoreCase)).Success)
            {
                AddRepeated(steps, ToInt(match.Groups[2].Value, 1), new MouseButtonStep(ParseMouseButton(match.Groups[1].Value), ButtonActionKind.Click, TimeSpan.Zero));
                continue;
            }

            if ((match = Regex.Match(line, "^macro\\.(mouseDown|mouseUp)\\(\"([^\"]+)\"\\)", RegexOptions.IgnoreCase)).Success)
            {
                steps.Add(new MouseButtonStep(ParseMouseButton(match.Groups[2].Value), match.Groups[1].Value.EndsWith("Down", StringComparison.OrdinalIgnoreCase) ? ButtonActionKind.Down : ButtonActionKind.Up, TimeSpan.Zero));
                continue;
            }

            if ((match = Regex.Match(line, "^macro\\.wait\\((\\d+)\\)", RegexOptions.IgnoreCase)).Success
                || (match = Regex.Match(line, "^Sleep\\((\\d+)\\)", RegexOptions.IgnoreCase)).Success)
            {
                steps.Add(new WaitStep(TimeSpan.FromMilliseconds(ToInt(match.Groups[1].Value))));
                continue;
            }

            if ((match = Regex.Match(line, "^macro\\.key\\(\"([^\"]+)\"\\s*,\\s*(\\d+)\\)", RegexOptions.IgnoreCase)).Success
                || (match = Regex.Match(line, "^PressAndReleaseKey\\(\"?([^\"\\)]+)\"?\\)", RegexOptions.IgnoreCase)).Success)
            {
                if (TryParseKey(match.Groups[1].Value, out var key, out var modifiers))
                {
                    AddRepeated(steps, ToInt(match.Groups.Count > 2 ? match.Groups[2].Value : string.Empty, 1), new KeyStep(KeyActionKind.Tap, key, modifiers, TimeSpan.Zero));
                }
                else
                {
                    diagnostics.Warning("lua.unsupportedKey", $"Unsupported Lua key '{match.Groups[1].Value}' was skipped.");
                }

                continue;
            }

            if ((match = Regex.Match(line, "^macro\\.(keyDown|keyUp)\\(\"([^\"]+)\"\\)", RegexOptions.IgnoreCase)).Success)
            {
                if (TryParseKey(match.Groups[2].Value, out var key, out var modifiers))
                {
                    steps.Add(new KeyStep(match.Groups[1].Value.EndsWith("Down", StringComparison.OrdinalIgnoreCase) ? KeyActionKind.Down : KeyActionKind.Up, key, modifiers, TimeSpan.Zero));
                }

                continue;
            }

            if ((match = Regex.Match(line, "^macro\\.text\\(\"([^\"]*)\"\\)", RegexOptions.IgnoreCase)).Success)
            {
                steps.Add(new TextStep(UnescapeText(match.Groups[1].Value)));
                continue;
            }

            if ((match = Regex.Match(line, "^PressMouseButton\\((\\d+)\\)", RegexOptions.IgnoreCase)).Success)
            {
                steps.Add(new MouseButtonStep(LogitechButton(ToInt(match.Groups[1].Value)), ButtonActionKind.Down, TimeSpan.Zero));
                continue;
            }

            if ((match = Regex.Match(line, "^ReleaseMouseButton\\((\\d+)\\)", RegexOptions.IgnoreCase)).Success)
            {
                steps.Add(new MouseButtonStep(LogitechButton(ToInt(match.Groups[1].Value)), ButtonActionKind.Up, TimeSpan.Zero));
            }
        }

        return steps;
    }

    private static MacroDocument ImportXMouse(string content, List<MacroConversionDiagnostic> diagnostics)
    {
        if (!Regex.IsMatch(content, "<XMouse(Profile|ButtonControl)?\\b", RegexOptions.IgnoreCase))
        {
            return new MacroDocument(1, "XMouse simulated keystrokes", ParseXMouseTokens(content, diagnostics));
        }

        var root = XDocument.Parse(content).Root ?? throw new FormatException("Invalid XMouse XML.");
        var name = Attribute(root, "name", "XMouse Profile");
        var steps = new List<MacroStep>();

        foreach (var action in root.Descendants("Action"))
        {
            var type = Attribute(action, "type");
            if (type.Equals("wait", StringComparison.OrdinalIgnoreCase))
            {
                steps.Add(new WaitStep(TimeSpan.FromMilliseconds(ToInt(Attribute(action, "ms")))));
            }
            else if (type.Equals("simulated-keystrokes", StringComparison.OrdinalIgnoreCase))
            {
                steps.AddRange(ParseXMouseTokens(Attribute(action, "keys"), diagnostics));
            }
            else if (type.Equals("text", StringComparison.OrdinalIgnoreCase))
            {
                steps.Add(new TextStep(Attribute(action, "text")));
            }
            else
            {
                diagnostics.Warning("xmouse.unsupportedAction", $"Unsupported XMouse action '{type}' was skipped.");
            }
        }

        return new MacroDocument(1, name, steps);
    }

    private static List<MacroStep> ParseXMouseTokens(string input, List<MacroConversionDiagnostic> diagnostics)
    {
        var steps = new List<MacroStep>();
        var modifiers = HidModifier.None;

        foreach (Match tokenMatch in Regex.Matches(input, "\\{[^}]+\\}|[^{}]"))
        {
            var token = tokenMatch.Value;
            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            var command = token.StartsWith('{') && token.EndsWith('}')
                ? token[1..^1]
                : token;
            var normalized = command.ToUpperInvariant();

            var wait = Regex.Match(command, "^WAITMS:(\\d+)$", RegexOptions.IgnoreCase);
            if (wait.Success)
            {
                steps.Add(new WaitStep(TimeSpan.FromMilliseconds(ToInt(wait.Groups[1].Value))));
                continue;
            }

            if (TryParseModifierToken(normalized, out var modifier))
            {
                modifiers |= modifier;
                continue;
            }

            if (normalized is "LMB" or "RMB" or "MMB")
            {
                steps.Add(new MouseButtonStep(XMouseButton(normalized), ButtonActionKind.Click, TimeSpan.Zero));
                continue;
            }

            if (normalized is "LMBD" or "RMBD" or "MMBD")
            {
                steps.Add(new MouseButtonStep(XMouseButton(normalized), ButtonActionKind.Down, TimeSpan.Zero));
                continue;
            }

            if (normalized is "LMBU" or "RMBU" or "MMBU")
            {
                steps.Add(new MouseButtonStep(XMouseButton(normalized), ButtonActionKind.Up, TimeSpan.Zero));
                continue;
            }

            if (TryParseKey(command, out var key, out var keyModifiers))
            {
                steps.Add(new KeyStep(KeyActionKind.Tap, key, modifiers | keyModifiers, TimeSpan.Zero));
                modifiers = HidModifier.None;
                continue;
            }

            if (token.Length == 1)
            {
                steps.Add(new TextStep(token));
            }
            else
            {
                diagnostics.Warning("xmouse.unsupportedToken", $"Unsupported XMouse token '{token}' was skipped.");
            }

            modifiers = HidModifier.None;
        }

        return steps;
    }

    private static MacroDocument ImportMacroConverterXml(string content, List<MacroConversionDiagnostic> diagnostics)
    {
        var root = XDocument.Parse(content).Root ?? throw new FormatException("Invalid MacroConverter XML.");
        if (!root.Name.LocalName.Equals("MacroConverterMacro", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException("Not a MacroConverter XML macro.");
        }

        var nodes = root.Descendants("Node").ToDictionary(element => Attribute(element, "id"));
        var edges = root.Descendants("Edge")
            .Select(element => new ConverterEdge(Attribute(element, "source"), Attribute(element, "target"), Attribute(element, "kind", "default")))
            .ToList();
        var steps = new List<MacroStep>();
        var current = nodes.Values.FirstOrDefault(element => Attribute(element, "type").Equals("start", StringComparison.OrdinalIgnoreCase));
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (current is not null)
        {
            current = NextNode(current, nodes, edges);
        }

        while (current is not null && visited.Add(Attribute(current, "id")))
        {
            var type = Attribute(current, "type");
            if (type.Equals("end", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            var converted = ConvertMacroXmlNode(current, diagnostics);
            steps.AddRange(converted);
            current = NextNode(current, nodes, edges);
        }

        if (steps.Count == 0)
        {
            foreach (var node in nodes.Values)
            {
                var type = Attribute(node, "type");
                if (!type.Equals("start", StringComparison.OrdinalIgnoreCase) && !type.Equals("end", StringComparison.OrdinalIgnoreCase))
                {
                    steps.AddRange(ConvertMacroXmlNode(node, diagnostics));
                }
            }
        }

        return new MacroDocument(1, Attribute(root, "name", "MacroConverter XML"), steps);
    }

    private static IEnumerable<MacroStep> ConvertMacroXmlNode(XElement node, List<MacroConversionDiagnostic> diagnostics)
    {
        var type = Attribute(node, "type");
        var data = node.Element("Data");
        switch (type.ToLowerInvariant())
        {
            case "mouse.move":
                return [new MouseMoveStep(MouseMoveMode.Absolute, ToInt(Attribute(data, "x")), ToInt(Attribute(data, "y")), TimeSpan.FromMilliseconds(ToInt(Attribute(data, "durationMs"))))];
            case "mouse.click":
                return RepeatIfNeeded(ToInt(Attribute(data, "count"), 1), new MouseButtonStep(ParseMouseButton(Attribute(data, "button", "left")), ButtonActionKind.Click, TimeSpan.FromMilliseconds(ToInt(Attribute(data, "holdMs")))));
            case "mouse.down":
                return [new MouseButtonStep(ParseMouseButton(Attribute(data, "button", "left")), ButtonActionKind.Down, TimeSpan.Zero)];
            case "mouse.up":
                return [new MouseButtonStep(ParseMouseButton(Attribute(data, "button", "left")), ButtonActionKind.Up, TimeSpan.Zero)];
            case "keyboard.press":
                if (TryParseKey(Attribute(data, "key"), out var key, out var modifiers))
                {
                    return RepeatIfNeeded(ToInt(Attribute(data, "count"), 1), new KeyStep(KeyActionKind.Tap, key, modifiers, TimeSpan.FromMilliseconds(ToInt(Attribute(data, "holdMs")))));
                }

                diagnostics.Warning("macroxml.unsupportedKey", $"Unsupported MacroConverter key '{Attribute(data, "key")}' was skipped.");
                return [];
            case "keyboard.text":
                return [new TextStep(Attribute(data, "text"))];
            case "wait":
                return [new WaitStep(TimeSpan.FromMilliseconds(ToInt(Attribute(data, "ms"))))];
            case "flow.loop":
                diagnostics.Warning("macroxml.loopShape", "MacroConverter XML loop graph was imported as an empty repeat marker because the graph body is not linear.");
                return [new RepeatStep(ToInt(Attribute(data, "count"), 1), [])];
            case "flow.if":
                diagnostics.Warning("macroxml.flowIf", "MacroConverter XML flow.if cannot be represented directly in MCRX and was skipped.");
                return [];
            default:
                diagnostics.Warning("macroxml.unsupportedNode", $"Unsupported MacroConverter node '{type}' was skipped.");
                return [];
        }
    }

    private static MacroDocument ImportRazerXml(string content, IReadOnlyList<AuxiliaryMacroFile> auxiliaryFiles, List<MacroConversionDiagnostic> diagnostics)
    {
        var root = XDocument.Parse(content).Root ?? throw new FormatException("Invalid Razer XML.");
        var modules = auxiliaryFiles
            .Select(file => TryReadRazerModule(file, diagnostics))
            .Where(module => module is not null)
            .ToDictionary(module => module!.Guid, module => module!, StringComparer.OrdinalIgnoreCase);
        var events = ExpandRazerModules(root.Descendants("MacroEvent").ToList(), modules, diagnostics, []);
        var index = 0;
        var steps = ParseRazerEventBlock(events, ref index, diagnostics, stopAtLoopEnd: false);

        return new MacroDocument(1, ElementValue(root, "Name", "Razer Macro"), steps);
    }

    private static List<MacroStep> ParseRazerEventBlock(IReadOnlyList<XElement> events, ref int index, List<MacroConversionDiagnostic> diagnostics, bool stopAtLoopEnd)
    {
        var steps = new List<MacroStep>();
        for (; index < events.Count; index++)
        {
            var current = events[index];
            var type = ElementValue(current, "Type");
            if (type.Equals("actionBar", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (type == "6")
            {
                var state = ToInt(ElementValue(current.Element("LoopEvent"), "State", "-1"), -1);
                if (stopAtLoopEnd && state == 1)
                {
                    return steps;
                }

                if (state == 0)
                {
                    var count = ToInt(ElementValue(current, "Number", "1"), 1);
                    index++;
                    steps.Add(new RepeatStep(count, ParseRazerEventBlock(events, ref index, diagnostics, stopAtLoopEnd: true)));
                }

                continue;
            }

            if (type == "0")
            {
                steps.Add(new WaitStep(TimeSpan.FromMilliseconds(ToMillisecondsFromSeconds(ElementValue(current, "Number")))));
                continue;
            }

            if (type == "2" && current.Element("MouseEvent") is { } mouse)
            {
                var state = ToInt(ElementValue(mouse, "State", "-1"), -1);
                if (state != 0)
                {
                    continue;
                }

                var button = RazerMouseButton(ToInt(ElementValue(mouse, "MouseButton")));
                var holdMs = 0.0;
                if (index + 1 < events.Count && ElementValue(events[index + 1], "Type") == "0")
                {
                    holdMs = ToMillisecondsFromSeconds(ElementValue(events[index + 1], "Number"));
                    index++;
                }

                if (index + 1 < events.Count && IsRazerMouseUp(events[index + 1], button))
                {
                    index++;
                }
                else
                {
                    diagnostics.Warning("razer.mouseReleaseMissing", "Razer mouse down event had no matching release; imported as a click.");
                }

                steps.Add(new MouseButtonStep(button, ButtonActionKind.Click, TimeSpan.FromMilliseconds(holdMs)));
                continue;
            }

            if (type == "1" && current.Element("KeyEvent") is { } keyEvent)
            {
                var state = ToInt(ElementValue(keyEvent, "State", "-1"), -1);
                if (state != 0)
                {
                    continue;
                }

                var makecode = ToInt(ElementValue(keyEvent, "Makecode"));
                var holdMs = 0.0;
                if (index + 1 < events.Count && ElementValue(events[index + 1], "Type") == "0")
                {
                    holdMs = ToMillisecondsFromSeconds(ElementValue(events[index + 1], "Number"));
                    index++;
                }

                if (index + 1 < events.Count && IsRazerKeyUp(events[index + 1], makecode))
                {
                    index++;
                }
                else
                {
                    diagnostics.Warning("razer.keyReleaseMissing", $"Razer key makecode {makecode} had no matching release; imported as a tap.");
                }

                if (TryParseKey(KeyNameFromMakecode(makecode), out var key, out var modifiers))
                {
                    steps.Add(new KeyStep(KeyActionKind.Tap, key, modifiers, TimeSpan.FromMilliseconds(holdMs)));
                }
                else
                {
                    diagnostics.Warning("razer.unsupportedKey", $"Unsupported Razer makecode {makecode} was skipped.");
                }

                continue;
            }

            if (type == "7")
            {
                continue;
            }

            diagnostics.Warning("razer.unsupportedEvent", $"Unsupported Razer event type '{type}' was skipped.");
        }

        return steps;
    }

    private static string ExportMacroConverterXml(MacroDocument document, List<MacroConversionDiagnostic> diagnostics)
    {
        var nodes = new List<XElement>
        {
            new("Node",
                new XAttribute("id", "start"),
                new XAttribute("type", "start"),
                new XAttribute("label", "Start"),
                new XAttribute("x", 0),
                new XAttribute("y", 120))
        };
        var edges = new List<XElement>();
        var previous = "start";
        var flatSteps = FlattenForExport(document.Steps, diagnostics, "macroxml");

        for (var index = 0; index < flatSteps.Count; index++)
        {
            var id = $"node-{index + 1}";
            nodes.Add(MacroXmlNode(id, flatSteps[index], index));
            edges.Add(new XElement("Edge",
                new XAttribute("id", $"e-{previous}-{id}"),
                new XAttribute("source", previous),
                new XAttribute("target", id),
                new XAttribute("kind", "default")));
            previous = id;
        }

        nodes.Add(new XElement("Node",
            new XAttribute("id", "end"),
            new XAttribute("type", "end"),
            new XAttribute("label", "End"),
            new XAttribute("x", 160 + flatSteps.Count * 150),
            new XAttribute("y", 120)));
        edges.Add(new XElement("Edge",
            new XAttribute("id", $"e-{previous}-end"),
            new XAttribute("source", previous),
            new XAttribute("target", "end"),
            new XAttribute("kind", "default")));

        return new XDocument(
            new XElement("MacroConverterMacro",
                new XAttribute("version", "1"),
                new XAttribute("name", document.Name),
                new XElement("Nodes", nodes),
                new XElement("Edges", edges))).ToString();
    }

    private static XElement MacroXmlNode(string id, MacroStep step, int index)
    {
        var x = 160 + index * 150;
        return step switch
        {
            MouseMoveStep move => Node("mouse.move", "Move mouse", Data(
                ("x", move.X),
                ("y", move.Y),
                ("durationMs", ToMilliseconds(move.Duration)))),
            MouseButtonStep button => Node(button.Kind == ButtonActionKind.Down ? "mouse.down" : button.Kind == ButtonActionKind.Up ? "mouse.up" : "mouse.click", "Mouse button", Data(
                ("button", ButtonName(button.Button)),
                ("count", 1),
                ("holdMs", ToMilliseconds(button.Hold)))),
            KeyStep key => Node("keyboard.press", "Key", Data(
                ("key", KeyName(key.Key, key.Modifiers)),
                ("count", 1),
                ("holdMs", ToMilliseconds(key.Hold)))),
            TextStep text => Node("keyboard.text", "Text", Data(("text", text.Text))),
            WaitStep wait => Node("wait", "Wait", Data(("ms", ToMilliseconds(wait.Duration)))),
            _ => Node("wait", "Unsupported", Data(("ms", 0)))
        };

        XElement Node(string type, string label, XElement data)
        {
            return new XElement("Node",
                new XAttribute("id", id),
                new XAttribute("type", type),
                new XAttribute("label", label),
                new XAttribute("x", x),
                new XAttribute("y", 120),
                data);
        }
    }

    private static string ExportLua(MacroDocument document, List<MacroConversionDiagnostic> diagnostics)
    {
        var builder = new StringBuilder();
        builder.AppendLine("-- MacroHID Lua Export");
        builder.AppendLine(CultureInfo.InvariantCulture, $"macro.begin(\"{EscapeLua(document.Name)}\")");
        foreach (var line in LuaLines(document.Steps, diagnostics, 0))
        {
            builder.AppendLine(line);
        }

        builder.Append("macro.finish()");
        return builder.ToString();
    }

    private static IEnumerable<string> LuaLines(IReadOnlyList<MacroStep> steps, List<MacroConversionDiagnostic> diagnostics, int indent)
    {
        var prefix = new string(' ', indent * 2);
        foreach (var step in steps)
        {
            switch (step)
            {
                case MouseMoveStep move:
                    yield return $"{prefix}macro.move({move.X}, {move.Y})";
                    break;
                case MouseButtonStep button:
                    if (button.Kind == ButtonActionKind.Click)
                    {
                        yield return $"{prefix}macro.click(\"{ButtonName(button.Button)}\", 1)";
                    }
                    else
                    {
                        yield return $"{prefix}macro.{(button.Kind == ButtonActionKind.Down ? "mouseDown" : "mouseUp")}(\"{ButtonName(button.Button)}\")";
                    }

                    break;
                case KeyStep key:
                    yield return key.Kind switch
                    {
                        KeyActionKind.Down => $"{prefix}macro.keyDown(\"{EscapeLua(KeyName(key.Key, key.Modifiers))}\")",
                        KeyActionKind.Up => $"{prefix}macro.keyUp(\"{EscapeLua(KeyName(key.Key, key.Modifiers))}\")",
                        _ => $"{prefix}macro.key(\"{EscapeLua(KeyName(key.Key, key.Modifiers))}\", 1)"
                    };
                    break;
                case TextStep text:
                    yield return $"{prefix}macro.text(\"{EscapeLua(text.Text)}\")";
                    break;
                case WaitStep wait:
                    yield return $"{prefix}macro.wait({ToMilliseconds(wait.Duration)})";
                    break;
                case RepeatStep repeat:
                    yield return $"{prefix}macro.loop({repeat.Count})";
                    foreach (var line in LuaLines(repeat.Steps, diagnostics, indent + 1))
                    {
                        yield return line;
                    }

                    yield return $"{prefix}macro.endLoop()";
                    break;
                default:
                    diagnostics.Warning("lua.unsupportedStep", $"Step '{step.GetType().Name}' cannot be exported to Lua and was skipped.");
                    break;
            }
        }
    }

    private static string ExportQMacro(MacroDocument document, List<MacroConversionDiagnostic> diagnostics)
    {
        return string.Join(Environment.NewLine, QMacroLines(document.Steps, diagnostics, 0));
    }

    private static IEnumerable<string> QMacroLines(IReadOnlyList<MacroStep> steps, List<MacroConversionDiagnostic> diagnostics, int indent)
    {
        var prefix = new string(' ', indent * 2);
        foreach (var step in steps)
        {
            switch (step)
            {
                case MouseMoveStep move:
                    yield return $"{prefix}MoveTo {move.X}, {move.Y}";
                    break;
                case MouseButtonStep { Kind: ButtonActionKind.Click } button:
                    yield return $"{prefix}{QMacroClickName(button.Button)} 1";
                    break;
                case MouseButtonStep button:
                    yield return $"{prefix}{QMacroButtonName(button.Button)}{(button.Kind == ButtonActionKind.Down ? "Down" : "Up")} 1";
                    break;
                case KeyStep key:
                    yield return $"{prefix}KeyPress \"{EscapeQuoted(KeyName(key.Key, key.Modifiers))}\", 1";
                    break;
                case TextStep text:
                    yield return $"{prefix}SayString \"{EscapeQuoted(text.Text)}\"";
                    break;
                case WaitStep wait:
                    yield return $"{prefix}Delay {ToMilliseconds(wait.Duration)}";
                    break;
                case RepeatStep repeat:
                    yield return $"{prefix}For i = 1 To {repeat.Count}";
                    foreach (var line in QMacroLines(repeat.Steps, diagnostics, indent + 1))
                    {
                        yield return line;
                    }

                    yield return $"{prefix}Next";
                    break;
                default:
                    diagnostics.Warning("qmacro.unsupportedStep", $"Step '{step.GetType().Name}' cannot be exported to QMacro and was skipped.");
                    break;
            }
        }
    }

    private static string ExportXMouse(MacroDocument document, List<MacroConversionDiagnostic> diagnostics)
    {
        var tokens = XMouseTokens(document.Steps, diagnostics);
        return string.Concat(tokens);
    }

    private static IEnumerable<string> XMouseTokens(IReadOnlyList<MacroStep> steps, List<MacroConversionDiagnostic> diagnostics)
    {
        foreach (var step in steps)
        {
            switch (step)
            {
                case MouseButtonStep { Kind: ButtonActionKind.Click } button:
                    yield return $"{{{XMouseButtonToken(button.Button)}}}";
                    break;
                case MouseButtonStep button:
                    yield return $"{{{XMouseButtonToken(button.Button)}{(button.Kind == ButtonActionKind.Down ? "D" : "U")}}}";
                    break;
                case KeyStep key:
                    foreach (var token in ModifierTokens(key.Modifiers))
                    {
                        yield return token;
                    }

                    yield return XMouseKeyToken(key.Key);
                    break;
                case TextStep text:
                    yield return text.Text;
                    break;
                case WaitStep wait:
                    yield return $"{{WAITMS:{ToMilliseconds(wait.Duration)}}}";
                    break;
                case RepeatStep repeat:
                    for (var index = 0; index < repeat.Count; index++)
                    {
                        foreach (var token in XMouseTokens(repeat.Steps, diagnostics))
                        {
                            yield return token;
                        }
                    }

                    break;
                default:
                    diagnostics.Warning("xmouse.unsupportedStep", $"Step '{step.GetType().Name}' cannot be exported to XMouse and was skipped.");
                    break;
            }
        }
    }

    private static string ExportRazerXml(MacroDocument document, List<MacroConversionDiagnostic> diagnostics)
    {
        var events = new List<XElement>
        {
            new("MacroEvent",
                new XElement("Type", "actionBar"),
                new XElement("recordProfile", new XElement("mmtSetting", 0)),
                new XElement("selected", false))
        };
        AddRazerEvents(document.Steps, events, diagnostics);

        return new XDocument(
            new XElement("Macro",
                new XElement("Name", document.Name),
                new XElement("MacroEvents", events),
                new XElement("DelaySetting", 0),
                new XElement("Guid", Guid.NewGuid()),
                new XElement("Version", 4),
                new XElement("MouseMoveType", "none"))).ToString();
    }

    private static void AddRazerEvents(IReadOnlyList<MacroStep> steps, List<XElement> events, List<MacroConversionDiagnostic> diagnostics)
    {
        foreach (var step in steps)
        {
            switch (step)
            {
                case MouseButtonStep { Kind: ButtonActionKind.Click } button:
                    events.Add(RazerMouseEvent(button.Button, 0, events.Count));
                    if (button.Hold > TimeSpan.Zero)
                    {
                        events.Add(RazerDelayEvent(button.Hold.TotalMilliseconds));
                    }

                    events.Add(RazerMouseEvent(button.Button, 1, events.Count));
                    break;
                case MouseButtonStep button:
                    events.Add(RazerMouseEvent(button.Button, button.Kind == ButtonActionKind.Down ? 0 : 1, events.Count));
                    break;
                case KeyStep key:
                    events.Add(RazerKeyEvent(key.Key, 0, events.Count));
                    if (key.Hold > TimeSpan.Zero)
                    {
                        events.Add(RazerDelayEvent(key.Hold.TotalMilliseconds));
                    }

                    events.Add(RazerKeyEvent(key.Key, 1, events.Count));
                    break;
                case WaitStep wait:
                    events.Add(RazerDelayEvent(wait.Duration.TotalMilliseconds));
                    break;
                case RepeatStep repeat:
                    events.Add(RazerLoopEvent(repeat.Count, 0));
                    AddRazerEvents(repeat.Steps, events, diagnostics);
                    events.Add(RazerLoopEvent(repeat.Count, 1));
                    break;
                default:
                    diagnostics.Warning("razer.unsupportedStep", $"Step '{step.GetType().Name}' cannot be exported to Razer Synapse XML and was skipped.");
                    break;
            }
        }
    }

    private static List<MacroStep> FlattenForExport(IReadOnlyList<MacroStep> steps, List<MacroConversionDiagnostic> diagnostics, string target)
    {
        var result = new List<MacroStep>();
        foreach (var step in steps)
        {
            switch (step)
            {
                case RepeatStep repeat:
                    result.AddRange(FlattenForExport(repeat.Steps, diagnostics, target));
                    break;
                case PixelWhenStep:
                case ConsumerStep:
                case MouseWheelStep:
                    diagnostics.Warning($"{target}.unsupportedStep", $"Step '{step.GetType().Name}' cannot be exported to {target} and was skipped.");
                    break;
                default:
                    result.Add(step);
                    break;
            }
        }

        return result;
    }

    private static XElement Data(params (string Name, object? Value)[] values)
    {
        var element = new XElement("Data");
        foreach (var (name, value) in values)
        {
            element.SetAttributeValue(name, value ?? string.Empty);
        }

        return element;
    }

    private static XElement? NextNode(XElement current, IReadOnlyDictionary<string, XElement> nodes, IReadOnlyList<ConverterEdge> edges)
    {
        var id = Attribute(current, "id");
        var target = edges.FirstOrDefault(edge => edge.Source.Equals(id, StringComparison.OrdinalIgnoreCase) && edge.Kind.Equals("default", StringComparison.OrdinalIgnoreCase))?.Target
            ?? edges.FirstOrDefault(edge => edge.Source.Equals(id, StringComparison.OrdinalIgnoreCase))?.Target;
        return target is not null && nodes.TryGetValue(target, out var node) ? node : null;
    }

    private static IEnumerable<MacroStep> RepeatIfNeeded(int count, MacroStep step)
    {
        return count <= 1 ? [step] : [new RepeatStep(count, [step])];
    }

    private static void AddRepeated(List<MacroStep> steps, int count, MacroStep step)
    {
        if (count <= 1)
        {
            steps.Add(step);
        }
        else
        {
            steps.Add(new RepeatStep(count, [step]));
        }
    }

    private static RazerModule? TryReadRazerModule(AuxiliaryMacroFile file, List<MacroConversionDiagnostic> diagnostics)
    {
        try
        {
            var root = XDocument.Parse(file.Content).Root;
            var guid = ElementValue(root, "Guid");
            if (string.IsNullOrWhiteSpace(guid))
            {
                diagnostics.Warning("razer.moduleGuidMissing", $"Razer module '{file.FileName}' does not contain a Guid and was ignored.");
                return null;
            }

            return new RazerModule(guid, ElementValue(root, "Name", file.FileName), root!);
        }
        catch (Exception ex)
        {
            diagnostics.Warning("razer.moduleInvalid", $"Razer module '{file.FileName}' could not be parsed: {ex.Message}");
            return null;
        }
    }

    private static List<XElement> ExpandRazerModules(IReadOnlyList<XElement> events, IReadOnlyDictionary<string, RazerModule> modules, List<MacroConversionDiagnostic> diagnostics, IReadOnlyList<string> ancestry)
    {
        var expanded = new List<XElement>();
        foreach (var item in events)
        {
            if (ElementValue(item, "Type") != "7")
            {
                expanded.Add(item);
                continue;
            }

            var guid = ElementValue(item, "guid");
            if (string.IsNullOrWhiteSpace(guid) || !modules.TryGetValue(guid, out var module))
            {
                diagnostics.Warning("razer.moduleMissing", $"Razer module '{guid}' was referenced but not provided.");
                continue;
            }

            if (ancestry.Contains(guid, StringComparer.OrdinalIgnoreCase))
            {
                diagnostics.Warning("razer.moduleCircular", $"Razer module '{module.Name}' has a circular reference and was skipped.");
                continue;
            }

            expanded.AddRange(ExpandRazerModules(module.Root.Descendants("MacroEvent").ToList(), modules, diagnostics, [.. ancestry, guid]));
        }

        return expanded;
    }

    private static bool IsRazerMouseUp(XElement element, MouseButton button)
    {
        return ElementValue(element, "Type") == "2"
            && element.Element("MouseEvent") is { } mouse
            && ToInt(ElementValue(mouse, "State", "-1"), -1) == 1
            && RazerMouseButton(ToInt(ElementValue(mouse, "MouseButton"))) == button;
    }

    private static bool IsRazerKeyUp(XElement element, int makecode)
    {
        return ElementValue(element, "Type") == "1"
            && element.Element("KeyEvent") is { } key
            && ToInt(ElementValue(key, "State", "-1"), -1) == 1
            && ToInt(ElementValue(key, "Makecode")) == makecode;
    }

    private static XElement RazerMouseEvent(MouseButton button, int state, int id)
    {
        return new XElement("MacroEvent",
            new XElement("Type", 2),
            new XElement("Id", id),
            new XElement("MouseEvent",
                new XElement("MouseButton", button switch
                {
                    MouseButton.Right => 1,
                    MouseButton.Middle => 2,
                    _ => 0
                }),
                new XElement("State", state)),
            new XElement("selected", false),
            new XElement("isPairing", false));
    }

    private static XElement RazerKeyEvent(HidKey key, int state, int id)
    {
        return new XElement("MacroEvent",
            new XElement("Type", 1),
            new XElement("Id", id),
            new XElement("KeyEvent",
                new XElement("Makecode", MakecodeFromKey(key)),
                new XElement("State", state)),
            new XElement("flag", state),
            new XElement("selected", false),
            new XElement("isPairing", false));
    }

    private static XElement RazerDelayEvent(double ms)
    {
        return new XElement("MacroEvent",
            new XElement("Type", 0),
            new XElement("Number", (ms / 1000.0).ToString("0.######", CultureInfo.InvariantCulture)),
            new XElement("selected", false));
    }

    private static XElement RazerLoopEvent(int count, int state)
    {
        return new XElement("MacroEvent",
            new XElement("Type", 6),
            new XElement("Id", string.Empty),
            new XElement("Number", count),
            new XElement("LoopEvent", new XElement("State", state)),
            new XElement("selected", false),
            new XElement("isPairing", false));
    }

    private static bool TryParseModifierToken(string token, out HidModifier modifier)
    {
        modifier = token switch
        {
            "CTRL" or "CONTROL" => HidModifier.LeftCtrl,
            "SHIFT" => HidModifier.LeftShift,
            "ALT" => HidModifier.LeftAlt,
            "WIN" or "GUI" => HidModifier.LeftGui,
            _ => HidModifier.None
        };
        return modifier != HidModifier.None;
    }

    private static bool TryParseKey(string value, out HidKey key, out HidModifier modifiers)
    {
        key = HidKey.None;
        modifiers = HidModifier.None;
        var normalized = value.Trim().Trim('{', '}');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        var parts = normalized.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var keyPart = parts[^1];
        foreach (var part in parts.Take(parts.Length - 1))
        {
            if (TryParseModifierToken(part.ToUpperInvariant(), out var modifier))
            {
                modifiers |= modifier;
            }
        }

        keyPart = keyPart.ToUpperInvariant() switch
        {
            "CTRL" or "CONTROL" => "LeftControl",
            "SHIFT" => "LeftShift",
            "ALT" => "LeftAlt",
            "WIN" or "GUI" => "LeftGui",
            "ESC" => "Escape",
            "RETURN" => "Enter",
            "PGUP" => "PageUp",
            "PGDN" => "PageDown",
            "DEL" => "Delete",
            "INS" => "Insert",
            _ => keyPart
        };

        if (keyPart.Length == 1 && char.IsDigit(keyPart[0]))
        {
            keyPart = "D" + keyPart;
        }

        return Enum.TryParse(keyPart, ignoreCase: true, out key);
    }

    private static string KeyName(HidKey key, HidModifier modifiers)
    {
        var parts = new List<string>();
        if ((modifiers & (HidModifier.LeftCtrl | HidModifier.RightCtrl)) != 0) parts.Add("Ctrl");
        if ((modifiers & (HidModifier.LeftShift | HidModifier.RightShift)) != 0) parts.Add("Shift");
        if ((modifiers & (HidModifier.LeftAlt | HidModifier.RightAlt)) != 0) parts.Add("Alt");
        if ((modifiers & (HidModifier.LeftGui | HidModifier.RightGui)) != 0) parts.Add("Win");

        var keyName = DisplayKeyName(key);
        parts.Add(keyName);
        return string.Join("+", parts);
    }

    private static string XMouseKeyToken(HidKey key)
    {
        var name = key.ToString();
        if (name.Length == 1 && char.IsLetterOrDigit(name[0]))
        {
            return name.ToUpperInvariant();
        }

        if (name.Length == 2 && name[0] == 'D' && char.IsDigit(name[1]))
        {
            return name[1].ToString();
        }

        return key switch
        {
            HidKey.Enter or HidKey.Return => "{ENTER}",
            HidKey.Escape => "{ESC}",
            HidKey.Space => "{SPACE}",
            HidKey.Tab => "{TAB}",
            HidKey.Backspace => "{BACKSPACE}",
            _ => "{" + key.ToString().ToUpperInvariant() + "}"
        };
    }

    private static IEnumerable<string> ModifierTokens(HidModifier modifiers)
    {
        if ((modifiers & (HidModifier.LeftCtrl | HidModifier.RightCtrl)) != 0) yield return "{CTRL}";
        if ((modifiers & (HidModifier.LeftShift | HidModifier.RightShift)) != 0) yield return "{SHIFT}";
        if ((modifiers & (HidModifier.LeftAlt | HidModifier.RightAlt)) != 0) yield return "{ALT}";
        if ((modifiers & (HidModifier.LeftGui | HidModifier.RightGui)) != 0) yield return "{WIN}";
    }

    private static string KeyNameFromMakecode(int makecode)
    {
        return makecode switch
        {
            8 => "Backspace",
            9 => "Tab",
            13 => "Enter",
            16 => "LeftShift",
            17 => "LeftControl",
            18 => "LeftAlt",
            20 => "CapsLock",
            27 => "Escape",
            32 => "Space",
            _ when makecode is >= 48 and <= 57 => ((char)makecode).ToString(),
            _ when makecode is >= 65 and <= 90 => ((char)makecode).ToString(),
            _ => makecode.ToString(CultureInfo.InvariantCulture)
        };
    }

    private static int MakecodeFromKey(HidKey key)
    {
        var name = key.ToString();
        if (name.Length == 1 && char.IsLetterOrDigit(name[0]))
        {
            return char.ToUpperInvariant(name[0]);
        }

        if (name.Length == 2 && name[0] == 'D' && char.IsDigit(name[1]))
        {
            return name[1];
        }

        return key switch
        {
            HidKey.Backspace => 8,
            HidKey.Tab => 9,
            HidKey.Enter or HidKey.Return => 13,
            HidKey.LeftShift or HidKey.RightShift => 16,
            HidKey.LeftControl or HidKey.RightControl => 17,
            HidKey.LeftAlt or HidKey.RightAlt => 18,
            HidKey.CapsLock => 20,
            HidKey.Escape => 27,
            HidKey.Space => 32,
            _ => 0
        };
    }

    private static string DisplayKeyName(HidKey key)
    {
        var name = key.ToString();
        return name.Length == 2 && name[0] == 'D' && char.IsDigit(name[1])
            ? name[1].ToString()
            : name;
    }

    private static MouseButton ParseMouseButton(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "right" or "rmb" => MouseButton.Right,
            "middle" or "mmb" => MouseButton.Middle,
            "x1" or "button4" => MouseButton.X1,
            "x2" or "button5" => MouseButton.X2,
            _ => MouseButton.Left
        };
    }

    private static MouseButton LogitechButton(int value)
    {
        return value switch
        {
            2 => MouseButton.Middle,
            3 => MouseButton.Right,
            _ => MouseButton.Left
        };
    }

    private static MouseButton XMouseButton(string token)
    {
        return token.StartsWith("RMB", StringComparison.OrdinalIgnoreCase)
            ? MouseButton.Right
            : token.StartsWith("MMB", StringComparison.OrdinalIgnoreCase)
                ? MouseButton.Middle
                : MouseButton.Left;
    }

    private static MouseButton RazerMouseButton(int value)
    {
        return value switch
        {
            1 => MouseButton.Right,
            2 => MouseButton.Middle,
            _ => MouseButton.Left
        };
    }

    private static string ButtonName(MouseButton button)
    {
        return button switch
        {
            MouseButton.Right => "right",
            MouseButton.Middle => "middle",
            MouseButton.X1 => "x1",
            MouseButton.X2 => "x2",
            _ => "left"
        };
    }

    private static string QMacroClickName(MouseButton button)
    {
        return button switch
        {
            MouseButton.Right => "RightClick",
            MouseButton.Middle => "MiddleClick",
            _ => "LeftClick"
        };
    }

    private static string QMacroButtonName(MouseButton button)
    {
        return button switch
        {
            MouseButton.Right => "Right",
            MouseButton.Middle => "Middle",
            _ => "Left"
        };
    }

    private static string XMouseButtonToken(MouseButton button)
    {
        return button switch
        {
            MouseButton.Right => "RMB",
            MouseButton.Middle => "MMB",
            _ => "LMB"
        };
    }

    private static string Attribute(XElement? element, string name, string defaultValue = "")
    {
        return element?.Attribute(name)?.Value ?? defaultValue;
    }

    private static string ElementValue(XElement? element, string name, string defaultValue = "")
    {
        return element?.Element(name)?.Value ?? defaultValue;
    }

    private static int ToInt(string? value, int defaultValue = 0)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : defaultValue;
    }

    private static double ToMillisecondsFromSeconds(string? value)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? seconds * 1000
            : 0.0;
    }

    private static int ToMilliseconds(TimeSpan duration)
    {
        return (int)Math.Round(duration.TotalMilliseconds, MidpointRounding.AwayFromZero);
    }

    private static string NormalizeLine(string line)
    {
        return Regex.Replace(line.Trim(), "\\s+", " ");
    }

    private static string EscapeLua(string value)
    {
        return EscapeQuoted(value).Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
    }

    private static string EscapeQuoted(string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    private static string UnescapeText(string value)
    {
        return value.Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            builder.Append(invalid.Contains(ch) ? '_' : ch);
        }

        return builder.ToString().Trim();
    }

    private static string[] SplitLines(this string value)
    {
        return value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
    }

    private static void Warning(this List<MacroConversionDiagnostic> diagnostics, string code, string message)
    {
        diagnostics.Add(new MacroConversionDiagnostic(MacroDiagnosticSeverity.Warning, code, message));
    }

    private sealed record ConverterEdge(string Source, string Target, string Kind);

    private sealed record RazerModule(string Guid, string Name, XElement Root);
}
