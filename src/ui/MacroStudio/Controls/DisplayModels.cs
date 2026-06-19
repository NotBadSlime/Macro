using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using MacroHid.Core;
using MacroStudio.Services;

namespace MacroStudio.Controls;

public sealed class BoolToThicknessConverter : IValueConverter
{
    public static readonly BoolToThicknessConverter Instance = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is Visibility v && v == Visibility.Visible)
            return new Thickness(1.5);
        return new Thickness(0);
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class MacroLibraryListEntry
{
    private static readonly Brush HeaderBrush = new SolidColorBrush(Color.FromRgb(110, 110, 115));
    private static readonly Brush MacroBrush = new SolidColorBrush(Color.FromRgb(52, 199, 89));
    private static readonly Brush TextBrush = new SolidColorBrush(Color.FromRgb(29, 29, 31));

    private MacroLibraryListEntry(MacroLibraryItem? item, string title, string subtitle, string icon, Brush accent, FontWeight weight, Brush titleBrush)
    {
        Item = item;
        Title = title;
        Subtitle = subtitle;
        Icon = icon;
        Accent = accent;
        Weight = weight;
        TitleBrush = titleBrush;
    }

    public MacroLibraryItem? Item { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public string Icon { get; }
    public Brush Accent { get; }
    public FontWeight Weight { get; }
    public Brush TitleBrush { get; }

    public static MacroLibraryListEntry Header(string title)
    {
        return new MacroLibraryListEntry(null, title, string.Empty, "F", HeaderBrush, FontWeights.SemiBold, HeaderBrush);
    }

    public static MacroLibraryListEntry Macro(MacroLibraryItem item)
    {
        return Macro(item, null);
    }

    public static MacroLibraryListEntry Macro(MacroLibraryItem item, MacroDocument? document)
    {
        var subtitle = item.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        if (document?.Playback.Trigger is { } trigger)
        {
            subtitle = $"{trigger} · {FormatPlaybackMode(document.Playback)} · {subtitle}";
        }

        if (!string.IsNullOrWhiteSpace(document?.Playback.ProcessFilter))
        {
            subtitle = $"{document.Playback.ProcessFilter} · {subtitle}";
        }

        return new MacroLibraryListEntry(item, item.Name, subtitle, "M", MacroBrush, FontWeights.Normal, TextBrush);
    }

    private static string FormatPlaybackMode(PlaybackSettings settings)
    {
        return settings.Mode switch
        {
            PlaybackMode.ToggleLoop => LocalizationService.Get("ModeToggleLoop"),
            PlaybackMode.HoldLoop => LocalizationService.Get("ModeHoldLoop"),
            PlaybackMode.FixedCount => FormatFixedCountMode(settings.Count),
            _ => settings.Mode.ToString()
        };
    }

    private static string FormatFixedCountMode(int count)
    {
        var label = LocalizationService.Get("ModeFixedCount");
        return label.Contains('N', StringComparison.Ordinal)
            ? label.Replace("N", count.ToString(CultureInfo.CurrentCulture), StringComparison.Ordinal)
            : string.Format(CultureInfo.CurrentCulture, "{0}: {1}", label, count);
    }
}

public sealed record MacroLibraryListenState(
    bool IsListening,
    bool IsConflict,
    string Trigger,
    string Mode,
    string ProcessFilter)
{
    public string BadgeText
    {
        get
        {
            if (IsConflict) return "冲突";
            if (IsListening) return "监听中";
            if (!string.IsNullOrWhiteSpace(Trigger)) return "未监听";
            return string.Empty;
        }
    }

    public Brush BadgeBrush
    {
        get
        {
            if (IsConflict) return new SolidColorBrush(Color.FromRgb(255, 59, 48));
            if (IsListening) return new SolidColorBrush(Color.FromRgb(52, 199, 89));
            if (!string.IsNullOrWhiteSpace(Trigger)) return new SolidColorBrush(Color.FromRgb(142, 142, 147));
            return Brushes.Transparent;
        }
    }
}

public sealed class MacroLibraryTreeNode : INotifyPropertyChanged
{
    private static readonly Brush HeaderBrush = new SolidColorBrush(Color.FromRgb(110, 110, 115));
    private static readonly Brush FolderBrush = new SolidColorBrush(Color.FromRgb(142, 142, 147));

    private MacroLibraryTreeNode(
        MacroLibraryItem? item,
        string folderName,
        string title,
        string subtitle,
        string icon,
        Brush accent,
        FontWeight weight,
        Brush titleBrush,
        MacroLibraryListenState? listenState,
        IReadOnlyList<MacroLibraryTreeNode> children)
    {
        Item = item;
        FolderName = folderName;
        Title = title;
        Subtitle = subtitle;
        Icon = icon;
        Accent = accent;
        Weight = weight;
        TitleBrush = titleBrush;
        ListenState = listenState;
        Children = children;
        IsExpanded = IsFolder;
        RenameText = title;
    }

    public MacroLibraryItem? Item { get; }
    public string FolderName { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public string Icon { get; }
    public Brush Accent { get; }
    public FontWeight Weight { get; }
    public Brush TitleBrush { get; }
    public MacroLibraryListenState? ListenState { get; }
    public string ListeningBadgeText => ListenState?.BadgeText ?? string.Empty;
    public Brush ListeningBadgeBrush => ListenState?.BadgeBrush ?? Brushes.Transparent;
    public Visibility ListeningBadgeVisibility => string.IsNullOrWhiteSpace(ListeningBadgeText)
        ? Visibility.Collapsed
        : Visibility.Visible;
    public IReadOnlyList<MacroLibraryTreeNode> Children { get; }
    public bool IsFolder => Item is null;
    public bool IsExpanded { get; set; }
    public bool IsSelected { get; set; }
    private string renameText = string.Empty;
    public string RenameText
    {
        get => renameText;
        set
        {
            if (renameText == value) return;
            renameText = value;
            OnPropertyChanged();
        }
    }

    private bool isRenaming;
    public bool IsRenaming
    {
        get => isRenaming;
        set
        {
            if (isRenaming == value) return;
            isRenaming = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public static MacroLibraryTreeNode Folder(string title, IReadOnlyList<MacroLibraryTreeNode> children)
    {
        return new MacroLibraryTreeNode(null, title, title, $"{children.Count} items", "F", FolderBrush, FontWeights.SemiBold, HeaderBrush, null, children);
    }

    public static MacroLibraryTreeNode Macro(MacroLibraryItem item, MacroDocument? document, MacroLibraryListenState? listenState = null)
    {
        var entry = MacroLibraryListEntry.Macro(item, document);
        var subtitle = entry.Subtitle;
        if (listenState is not null)
        {
            var parts = new[] { listenState.ProcessFilter, listenState.Trigger, listenState.Mode, subtitle }
                .Where(part => !string.IsNullOrWhiteSpace(part));
            subtitle = string.Join(" · ", parts);
        }

        return new MacroLibraryTreeNode(item, item.Folder, entry.Title, subtitle, entry.Icon, entry.Accent, entry.Weight, entry.TitleBrush, listenState, []);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public enum StepDisplayKind
{
    Normal,
    LoopStart,
    LoopEnd,
    ConditionStart,
    ConditionEnd,
    Delay
}

public sealed class StepDisplayItem
{
    private static readonly Brush GreenBrush = new SolidColorBrush(Color.FromRgb(52, 199, 89));
    private static readonly Brush BlueBrush = new SolidColorBrush(Color.FromRgb(0, 122, 255));
    private static readonly Brush OrangeBrush = new SolidColorBrush(Color.FromRgb(255, 149, 0));
    private static readonly Brush PinkBrush = new SolidColorBrush(Color.FromRgb(255, 45, 85));
    private static readonly Brush RedBrush = new SolidColorBrush(Color.FromRgb(255, 59, 48));
    private static readonly Brush GrayBrush = new SolidColorBrush(Color.FromRgb(142, 142, 147));
    private static readonly Brush LoopBrush = new SolidColorBrush(Color.FromRgb(239, 108, 0));

    private StepDisplayItem(int index, IReadOnlyList<int> stepPath, string icon, string actionIndicator, string title, string badge, Brush accentBrush, StepDisplayKind kind, int indent)
    {
        Index = index;
        StepPath = stepPath.ToArray();
        StepPathText = FormatPath(StepPath);
        Icon = icon;
        ActionIndicator = actionIndicator;
        Title = title;
        Badge = badge;
        AccentBrush = accentBrush;
        Kind = kind;
        Indent = indent;
        LeftMargin = new Thickness(indent * 20, 0, 0, 0);
    }

    public int Index { get; }
    public IReadOnlyList<int> StepPath { get; }
    public string StepPathText { get; }
    public string Icon { get; }
    public string ActionIndicator { get; }
    public string Title { get; }
    public string Badge { get; }
    public Brush AccentBrush { get; }
    public StepDisplayKind Kind { get; }
    public int Indent { get; }
    public Thickness LeftMargin { get; }
    public bool IsStructural => Kind is StepDisplayKind.LoopStart or StepDisplayKind.LoopEnd or StepDisplayKind.ConditionStart or StepDisplayKind.ConditionEnd;
    public bool IsConditionEndpoint { get; set; }
    public List<Brush> ConditionBars { get; set; } = [];

    public static List<StepDisplayItem> FlattenSteps(
        IReadOnlyList<MacroStep> steps,
        int indent = 0,
        IReadOnlyList<int>? parentPath = null,
        Func<string, string>? macroNameResolver = null)
    {
        var result = new List<StepDisplayItem>();
        var basePath = parentPath?.ToArray() ?? [];
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var stepPath = basePath.Concat([i]).ToArray();
            if (step is RepeatStep repeat)
            {
                result.Add(new StepDisplayItem(i, stepPath, "🔄", "", $"启动循环:  {repeat.Count}", FormatDuration(EstimateStepDuration(repeat)), LoopBrush, StepDisplayKind.LoopStart, indent));
                result.AddRange(FlattenSteps(repeat.Steps, indent + 1, stepPath, macroNameResolver));
                result.Add(new StepDisplayItem(i, stepPath, "🔄", "", $"结束循环:  {repeat.Count}", "", LoopBrush, StepDisplayKind.LoopEnd, indent));
            }
            else if (step is PixelWhenStep pixel)
            {
                var desc = $"像素条件 ({pixel.Condition.Coordinate.X},{pixel.Condition.Coordinate.Y})";
                result.Add(new StepDisplayItem(i, stepPath, "🎯", "", desc, "", PinkBrush, StepDisplayKind.ConditionStart, indent));
                result.AddRange(FlattenSteps(pixel.ThenSteps, indent + 1, stepPath, macroNameResolver));
                result.Add(new StepDisplayItem(i, stepPath, "🎯", "", "结束条件", "", PinkBrush, StepDisplayKind.ConditionEnd, indent));
            }
            else
            {
                result.Add(FromStep(i, step, indent, stepPath, macroNameResolver));
            }
        }
        return result;
    }

    public static StepDisplayItem FromStep(
        int index,
        MacroStep step,
        int indent = 0,
        IReadOnlyList<int>? stepPath = null,
        Func<string, string>? macroNameResolver = null)
    {
        var path = stepPath?.ToArray() ?? [index];
        return step switch
        {
            KeyStep key => new StepDisplayItem(index, path, "🔤", GetKeyIndicator(key), DescribeKey(key), FormatDuration(key.Hold), GreenBrush, StepDisplayKind.Normal, indent),
            TextStep text => new StepDisplayItem(index, path, "📝", "", $"文本: \"{TrimText(text.Text)}\"", "", BlueBrush, StepDisplayKind.Normal, indent),
            MouseMoveStep move => new StepDisplayItem(index, path, "🖱", "↗", $"移动 ({move.X}, {move.Y})", FormatDuration(move.Duration), OrangeBrush, StepDisplayKind.Normal, indent),
            MouseButtonStep button => new StepDisplayItem(index, path, "🖱", GetMouseIndicator(button), DescribeMouseButton(button), FormatDuration(button.Hold), OrangeBrush, StepDisplayKind.Normal, indent),
            MouseWheelStep wheel => new StepDisplayItem(index, path, "🖱", "⟳", $"滚轮 V={wheel.Vertical} H={wheel.Horizontal}", "", BlueBrush, StepDisplayKind.Normal, indent),
            ConsumerStep consumer => new StepDisplayItem(index, path, "🎵", GetConsumerIndicator(consumer), $"媒体 {consumer.Control}", FormatDuration(consumer.Hold), PinkBrush, StepDisplayKind.Normal, indent),
            WaitStep wait => new StepDisplayItem(index, path, "⏱", "", FormatWait(wait), "", GrayBrush, StepDisplayKind.Delay, indent),
            MacroCallStep macro => new StepDisplayItem(index, path, "📦", "▶", $"调用宏: {ResolveMacroDisplayName(macro.Macro, macroNameResolver)}", "", GreenBrush, StepDisplayKind.Normal, indent),
            _ => new StepDisplayItem(index, path, "?", "", step.GetType().Name, "", GrayBrush, StepDisplayKind.Normal, indent)
        };
    }

    public static StepDisplayItem Error(string message)
    {
        return new StepDisplayItem(-1, [], "⚠", "", message, "", RedBrush, StepDisplayKind.Normal, 0);
    }

    private static string FormatPath(IReadOnlyList<int> path) => string.Join(".", path);

    private static string GetKeyIndicator(KeyStep key) => key.Kind switch
    {
        KeyActionKind.Down => "↓",
        KeyActionKind.Up => "↑",
        KeyActionKind.Tap => "↕",
        _ => ""
    };

    private static string GetMouseIndicator(MouseButtonStep button) => button.Kind switch
    {
        ButtonActionKind.Down => "↓",
        ButtonActionKind.Up => "↑",
        ButtonActionKind.Click => "↕",
        _ => ""
    };

    private static string GetConsumerIndicator(ConsumerStep consumer) => consumer.Kind switch
    {
        ButtonActionKind.Down => "↓",
        ButtonActionKind.Up => "↑",
        _ => "↕"
    };

    private static string DescribeKey(KeyStep key)
    {
        var modifiers = FormatModifiers(key.Modifiers);
        var action = key.Kind switch
        {
            KeyActionKind.Down => "按下",
            KeyActionKind.Up => "抬起",
            _ => "单击"
        };
        return $"{modifiers}{key.Key}{action}";
    }

    private static string DescribeMouseButton(MouseButtonStep button)
    {
        var btnName = button.Button switch
        {
            MouseButton.Left => "左键",
            MouseButton.Right => "右键",
            MouseButton.Middle => "滚轮",
            MouseButton.X1 => "鼠标按键 4",
            MouseButton.X2 => "鼠标按键 5",
            MouseButton.Button6 => "鼠标按键 6",
            MouseButton.Button7 => "鼠标按键 7",
            MouseButton.Button8 => "鼠标按键 8",
            _ => button.Button.ToString()
        };
        var action = button.Kind switch
        {
            ButtonActionKind.Down => "按下",
            ButtonActionKind.Up => "抬起",
            ButtonActionKind.Click => "单击",
            _ => ""
        };
        var coordinate = button.HasCoordinate ? $" @{button.X},{button.Y}" : string.Empty;
        return $"{btnName}{action}{coordinate}";
    }

    private static string FormatModifiers(HidModifier modifiers)
    {
        if (modifiers == HidModifier.None)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        if ((modifiers & (HidModifier.LeftCtrl | HidModifier.RightCtrl)) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((modifiers & (HidModifier.LeftShift | HidModifier.RightShift)) != 0)
        {
            parts.Add("Shift");
        }

        if ((modifiers & (HidModifier.LeftAlt | HidModifier.RightAlt)) != 0)
        {
            parts.Add("Alt");
        }

        if ((modifiers & (HidModifier.LeftGui | HidModifier.RightGui)) != 0)
        {
            parts.Add("Win");
        }

        return string.Join("+", parts) + "+";
    }

    private static string TrimText(string value) => value.Length <= 24 ? value : $"{value[..21]}...";

    private static string ResolveMacroDisplayName(string value, Func<string, string>? macroNameResolver)
    {
        var fallback = string.IsNullOrWhiteSpace(value) ? "未选择" : value;
        var resolved = macroNameResolver?.Invoke(value);
        return string.IsNullOrWhiteSpace(resolved) ? fallback : resolved;
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return "";
        return duration.TotalSeconds >= 1 ? $"{duration.TotalSeconds:0.###}s" : $"{duration.TotalMilliseconds:0.###}ms";
    }

    private static string FormatWait(WaitStep wait)
    {
        if (wait.IsRandom && wait.MaxDuration is { } max)
        {
            return $"{FormatDuration(wait.MinDuration)}-{FormatDuration(max)}";
        }

        return FormatDuration(wait.Duration);
    }

    private static TimeSpan EstimateStepDuration(MacroStep step) => step switch
    {
        KeyStep key => key.Hold,
        MouseMoveStep move => move.Duration,
        MouseButtonStep button => button.Hold,
        ConsumerStep consumer => consumer.Hold,
        WaitStep wait => wait.MaxDuration ?? wait.Duration,
        RepeatStep repeat => TimeSpan.FromTicks(repeat.Steps.Sum(s => EstimateStepDuration(s).Ticks) * Math.Max(1, repeat.Count)),
        PixelWhenStep pixel => TimeSpan.FromTicks(pixel.ThenSteps.Sum(s => EstimateStepDuration(s).Ticks)),
        _ => TimeSpan.Zero
    };
}
