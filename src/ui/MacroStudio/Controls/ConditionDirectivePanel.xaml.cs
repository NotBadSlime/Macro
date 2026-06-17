using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MacroHid.Core;
using MacroStudio.Services;

namespace MacroStudio.Controls;

public partial class ConditionDirectivePanel : UserControl
{
    private const string ConditionClipboardPrefix = "MacroHID.Conditions.v1";
    private const string ConditionDragFormat = "MacroHID.ConditionIndexes";

    private List<ConditionalDirective> conditions = [];
    private IReadOnlyList<StepChoice> stepChoices = [];
    private MacroEditorState? editorState;
    private int selectedIndex = -1;
    private bool loadingEditor;
    private bool conditionBoxSelectionActive;
    private Point conditionBoxSelectionStartPoint;
    private readonly HashSet<int> conditionBoxSelectionBaseIndexes = [];
    private Point conditionDragStartPoint;
    private bool conditionDragStarted;
    private int conditionDragStartIndex = -1;
    private bool thenActionSequenceActive;

    private static readonly Brush[] conditionColors =
    [
        new SolidColorBrush(Color.FromRgb(99, 102, 241)),
        new SolidColorBrush(Color.FromRgb(16, 185, 129)),
        new SolidColorBrush(Color.FromRgb(245, 158, 11)),
        new SolidColorBrush(Color.FromRgb(239, 68, 68)),
        new SolidColorBrush(Color.FromRgb(139, 92, 246)),
        new SolidColorBrush(Color.FromRgb(6, 182, 212)),
    ];

    public event EventHandler<ConditionSelectionChangedEventArgs>? ConditionSelectionChanged;
    public event EventHandler? ConditionsModified;
    public event EventHandler? PickRegionRequested;

    public ConditionDirectivePanel()
    {
        InitializeComponent();
    }

    public IReadOnlyList<ConditionalDirective> Conditions => conditions;

    public void Initialize(MacroEditorState state)
    {
        editorState = state;
        ThenActionSequence.Initialize(state);
        ThenActionSequence.SetTitle("触发动作");
        ThenActionSequence.Activated += () => thenActionSequenceActive = true;
        ThenActionSequence.BeforeStepsChanged += () => { };
        ThenActionSequence.StepsChanged += OnThenActionSequenceStepsChanged;
        ThenActionSequence.ActionTemplateDropped += OnThenActionTemplateDropped;
        ThenActionSequence.MacroLibraryDropped += OnThenMacroLibraryDropped;
    }

    public void SetStepChoices(IReadOnlyList<StepChoice> choices)
    {
        var previousLoadingEditor = loadingEditor;
        loadingEditor = true;
        try
        {
            stepChoices = choices;
            StartStepCombo.ItemsSource = stepChoices;
            EndStepCombo.ItemsSource = stepChoices;
            if (selectedIndex >= 0 && selectedIndex < conditions.Count)
            {
                SelectStepChoice(StartStepCombo, conditions[selectedIndex].StartStepPathText, conditions[selectedIndex].StartStepIndex);
                SelectStepChoice(EndStepCombo, conditions[selectedIndex].EndStepPathText, conditions[selectedIndex].EndStepIndex);
            }
        }
        finally
        {
            loadingEditor = previousLoadingEditor;
        }
    }

    public void LoadConditions(IReadOnlyList<ConditionalDirective>? directives)
    {
        conditions = directives != null ? new List<ConditionalDirective>(directives) : [];
        RefreshList();
        if (conditions.Count == 0)
        {
            selectedIndex = -1;
            EditorBorder.Visibility = Visibility.Collapsed;
            EditorGrid.Visibility = Visibility.Collapsed;
            ThenActionSequence.SetSteps([]);
            return;
        }

        ConditionList.SelectedIndex = 0;
    }

    public Brush GetConditionColor(int index)
    {
        return conditionColors[index % conditionColors.Length];
    }

    private void RefreshList()
    {
        var items = new List<ConditionDisplayItem>();
        for (int i = 0; i < conditions.Count; i++)
        {
            var c = conditions[i];
            items.Add(new ConditionDisplayItem
            {
                Name = c.Name,
                Description = DescribeMatcher(c.Condition),
                RangeBadge = DescribeRange(c),
                TypeIcon = GetTypeIcon(c.Condition.Type),
                ColorBrush = GetConditionColor(i),
                StatusBrush = Brushes.Gray,
                StatusText = "就绪"
            });
        }
        ConditionList.ItemsSource = items;
        EmptyConditionHintText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string DescribeMatcher(IConditionMatcher matcher) => matcher switch
    {
        PixelMatcher p => $"像素 ({p.Region.TopLeft.X},{p.Region.TopLeft.Y}) RGB=({p.Expected.R},{p.Expected.G},{p.Expected.B})",
        TemplateMatcher => "模板图像匹配",
        PixelHashMatcher => "像素哈希比对",
        TextMatcher t => $"文字: \"{t.ExpectedText}\"",
        _ => "未知条件"
    };

    private static string GetTypeIcon(string type) => type switch
    {
        "pixel" => "🎯",
        "template" => "🖼",
        "pixelHash" => "#",
        "text" => "T",
        _ => "?"
    };

    private void AddCondition_Click(object sender, RoutedEventArgs e)
    {
        var firstChoice = stepChoices.FirstOrDefault();
        var firstPath = firstChoice?.Path;
        var newCond = new ConditionalDirective(
            ConditionalDirective.NewId(),
            $"条件 {conditions.Count + 1}",
            firstChoice?.Index ?? 0, firstChoice?.Index ?? 0,
            new PixelMatcher(ScreenRegion.FromSinglePixel(0, 0), new RgbColor(255, 0, 0), 10),
            [],
            StartStepPath: firstPath,
            EndStepPath: firstPath);
        conditions.Add(newCond);
        RefreshList();
        ConditionList.SelectedIndex = conditions.Count - 1;
        ConditionsModified?.Invoke(this, EventArgs.Empty);
    }

    private void DeleteCondition_Click(object sender, RoutedEventArgs e)
    {
        var indexes = GetSelectedConditionIndexes();
        DeleteConditions(indexes);
    }

    private void ConditionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        selectedIndex = ConditionList.SelectedIndex >= 0
            ? ConditionList.SelectedIndex
            : GetSelectedConditionIndexes().FirstOrDefault(-1);
        if (selectedIndex >= 0 && selectedIndex < conditions.Count)
        {
            LoadEditor(conditions[selectedIndex]);
            EditorBorder.Visibility = Visibility.Visible;
            EditorGrid.Visibility = Visibility.Visible;
            ConditionSelectionChanged?.Invoke(this,
                new ConditionSelectionChangedEventArgs(selectedIndex, conditions[selectedIndex]));
        }
        else
        {
            EditorBorder.Visibility = Visibility.Collapsed;
            EditorGrid.Visibility = Visibility.Collapsed;
            ThenActionSequence.SetSteps([]);
            ConditionSelectionChanged?.Invoke(this, new ConditionSelectionChangedEventArgs(-1, null));
        }
    }

    private void ConditionList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        conditionDragStartPoint = e.GetPosition(ConditionList);
        conditionDragStarted = false;
        conditionDragStartIndex = GetConditionIndexFromSource(source);
        if (!ShouldStartConditionBoxSelection(source))
        {
            return;
        }

        BeginConditionBoxSelection(e.GetPosition(ConditionList), (Keyboard.Modifiers & ModifierKeys.Control) != 0);
        e.Handled = true;
    }

    private void ConditionList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (conditionBoxSelectionActive)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                FinishConditionBoxSelection();
                return;
            }

            UpdateConditionBoxSelection(e.GetPosition(ConditionList));
            e.Handled = true;
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed || conditionDragStartIndex < 0 || conditionDragStarted)
        {
            return;
        }

        var current = e.GetPosition(ConditionList);
        var moved = Math.Abs(current.X - conditionDragStartPoint.X) > SystemParameters.MinimumHorizontalDragDistance
            || Math.Abs(current.Y - conditionDragStartPoint.Y) > SystemParameters.MinimumVerticalDragDistance;
        if (!moved)
        {
            return;
        }

        var indexes = GetSelectedConditionIndexes();
        if (!indexes.Contains(conditionDragStartIndex))
        {
            indexes = [conditionDragStartIndex];
        }

        conditionDragStarted = true;
        DragDrop.DoDragDrop(ConditionList, new DataObject(ConditionDragFormat, string.Join(",", indexes)), DragDropEffects.Move);
        conditionDragStarted = false;
        conditionDragStartIndex = -1;
        e.Handled = true;
    }

    private void ConditionList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!conditionBoxSelectionActive)
        {
            return;
        }

        UpdateConditionBoxSelection(e.GetPosition(ConditionList));
        FinishConditionBoxSelection();
        e.Handled = true;
    }

    private void ConditionList_LostMouseCapture(object sender, MouseEventArgs e)
    {
        FinishConditionBoxSelection();
    }

    private void ConditionList_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(ConditionDragFormat))
        {
            return;
        }

        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void ConditionList_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(ConditionDragFormat)
            || e.Data.GetData(ConditionDragFormat) is not string payload)
        {
            return;
        }

        var indexes = payload
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => int.TryParse(value, out var index) ? index : -1)
            .Where(index => index >= 0 && index < conditions.Count)
            .Distinct()
            .Order()
            .ToList();
        if (indexes.Count == 0)
        {
            return;
        }

        MoveSelectedConditionsToIndex(indexes, GetConditionDropIndex(e.GetPosition(ConditionList)));
        e.Handled = true;
    }

    private IReadOnlyList<int> GetSelectedConditionIndexes()
    {
        var indexes = ConditionList.SelectedItems
            .Cast<object>()
            .Select(item => ConditionList.Items.IndexOf(item))
            .Where(index => index >= 0 && index < conditions.Count)
            .Distinct()
            .Order()
            .ToList();

        return indexes;
    }

    public bool SelectAllConditions()
    {
        ConditionList.SelectedItems.Clear();
        foreach (var item in ConditionList.Items)
        {
            ConditionList.SelectedItems.Add(item);
        }

        return ConditionList.SelectedItems.Count > 0;
    }

    public bool CopySelectedConditionsToClipboard()
    {
        var indexes = GetSelectedConditionIndexes();
        if (indexes.Count == 0)
        {
            return false;
        }

        try
        {
            var copied = indexes.Select(index => conditions[index]).ToList();
            var payload = McrxSerializer.Serialize(new MacroDocument(1, "condition-clipboard", PlaybackSettings.Default, [], copied));
            Clipboard.SetText($"{ConditionClipboardPrefix}{Environment.NewLine}{payload}");
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool CutSelectedConditionsToClipboard()
    {
        var indexes = GetSelectedConditionIndexes();
        if (indexes.Count == 0 || !CopySelectedConditionsToClipboard())
        {
            return false;
        }

        DeleteConditions(indexes);
        return true;
    }

    public bool PasteConditionsFromClipboard()
    {
        if (!TryReadClipboardConditions(out var pasted) || pasted.Count == 0)
        {
            return false;
        }

        var insertIndex = selectedIndex >= 0 && selectedIndex < conditions.Count
            ? selectedIndex + 1
            : conditions.Count;
        var clones = pasted.Select(CloneConditionForPaste).ToList();
        conditions.InsertRange(Math.Clamp(insertIndex, 0, conditions.Count), clones);
        RefreshList();
        SelectConditionIndexes(Enumerable.Range(insertIndex, clones.Count).ToList());
        ConditionsModified?.Invoke(this, EventArgs.Empty);
        if (selectedIndex >= 0 && selectedIndex < conditions.Count)
        {
            ConditionSelectionChanged?.Invoke(this,
                new ConditionSelectionChangedEventArgs(selectedIndex, conditions[selectedIndex]));
        }

        return true;
    }

    private static bool TryReadClipboardConditions(out IReadOnlyList<ConditionalDirective> directives)
    {
        directives = [];
        try
        {
            if (!Clipboard.ContainsText())
            {
                return false;
            }

            var text = Clipboard.GetText();
            var json = text.StartsWith(ConditionClipboardPrefix, StringComparison.Ordinal)
                ? text[ConditionClipboardPrefix.Length..].Trim()
                : text.Trim();
            if (string.IsNullOrWhiteSpace(json))
            {
                return false;
            }

            var document = McrxParser.Parse(json);
            directives = document.EffectiveConditions;
            return directives.Count > 0;
        }
        catch
        {
            directives = [];
            return false;
        }
    }

    private ConditionalDirective CloneConditionForPaste(ConditionalDirective directive)
    {
        return directive with
        {
            Id = ConditionalDirective.NewId(),
            Name = CreateConditionCopyName(directive.Name)
        };
    }

    private string CreateConditionCopyName(string name)
    {
        var baseName = string.IsNullOrWhiteSpace(name) ? "条件" : name.Trim();
        var candidate = $"{baseName} 副本";
        var counter = 2;
        while (conditions.Any(condition => string.Equals(condition.Name, candidate, StringComparison.CurrentCultureIgnoreCase)))
        {
            candidate = $"{baseName} 副本 {counter++}";
        }

        return candidate;
    }

    private void DeleteConditions(IReadOnlyList<int> indexes)
    {
        if (indexes.Count == 0) return;

        var nextIndex = indexes[0];
        foreach (var index in indexes.OrderByDescending(index => index))
        {
            conditions.RemoveAt(index);
        }

        selectedIndex = -1;
        RefreshList();
        ConditionsModified?.Invoke(this, EventArgs.Empty);
        if (conditions.Count == 0)
        {
            EditorBorder.Visibility = Visibility.Collapsed;
            EditorGrid.Visibility = Visibility.Collapsed;
            ThenActionSequence.SetSteps([]);
            ConditionSelectionChanged?.Invoke(this, new ConditionSelectionChangedEventArgs(-1, null));
            return;
        }

        ConditionList.SelectedIndex = Math.Min(nextIndex, conditions.Count - 1);
    }

    private void MoveSelectedConditionsToIndex(IReadOnlyList<int> indexes, int targetIndex)
    {
        if (indexes.Count == 0)
        {
            return;
        }

        var normalized = indexes.Where(index => index >= 0 && index < conditions.Count).Distinct().Order().ToList();
        if (normalized.Count == 0)
        {
            return;
        }

        var moving = normalized.Select(index => conditions[index]).ToList();
        var remaining = conditions.Where((_, index) => !normalized.Contains(index)).ToList();
        var adjustedTarget = targetIndex;
        foreach (var index in normalized)
        {
            if (index < targetIndex)
            {
                adjustedTarget--;
            }
        }

        adjustedTarget = Math.Clamp(adjustedTarget, 0, remaining.Count);
        remaining.InsertRange(adjustedTarget, moving);
        conditions = remaining;
        RefreshList();
        SelectConditionIndexes(Enumerable.Range(adjustedTarget, moving.Count).ToList());
        ConditionsModified?.Invoke(this, EventArgs.Empty);
        if (selectedIndex >= 0 && selectedIndex < conditions.Count)
        {
            ConditionSelectionChanged?.Invoke(this,
                new ConditionSelectionChangedEventArgs(selectedIndex, conditions[selectedIndex]));
        }
    }

    private void SelectConditionIndexes(IReadOnlyList<int> indexes)
    {
        ConditionList.SelectedItems.Clear();
        foreach (var index in indexes.Where(index => index >= 0 && index < ConditionList.Items.Count))
        {
            ConditionList.SelectedItems.Add(ConditionList.Items[index]);
        }

        selectedIndex = indexes.FirstOrDefault(-1);
        if (selectedIndex >= 0 && selectedIndex < ConditionList.Items.Count)
        {
            ConditionList.SelectedIndex = selectedIndex;
        }
    }

    private void BeginConditionBoxSelection(Point origin, bool preserveExistingSelection)
    {
        conditionBoxSelectionActive = true;
        conditionBoxSelectionStartPoint = origin;
        conditionBoxSelectionBaseIndexes.Clear();

        if (preserveExistingSelection)
        {
            foreach (var index in GetSelectedConditionIndexes())
            {
                conditionBoxSelectionBaseIndexes.Add(index);
            }
        }
        else
        {
            ConditionList.SelectedItems.Clear();
        }

        ConditionList.CaptureMouse();
        UpdateConditionBoxSelection(origin);
    }

    private void UpdateConditionBoxSelection(Point current)
    {
        if (!conditionBoxSelectionActive)
        {
            return;
        }

        var bounds = CreateSelectionBounds(conditionBoxSelectionStartPoint, current);
        ShowConditionSelectionRectangle(bounds);
        SelectConditionItemsInsideBox(bounds);
    }

    private void FinishConditionBoxSelection()
    {
        if (!conditionBoxSelectionActive)
        {
            return;
        }

        conditionBoxSelectionActive = false;
        conditionBoxSelectionBaseIndexes.Clear();
        ConditionSelectionRectangle.Visibility = Visibility.Collapsed;
        if (ConditionList.IsMouseCaptured)
        {
            ConditionList.ReleaseMouseCapture();
        }
    }

    private void SelectConditionItemsInsideBox(Rect selectionBounds)
    {
        var selectedIndexes = new HashSet<int>(conditionBoxSelectionBaseIndexes);
        for (var i = 0; i < ConditionList.Items.Count; i++)
        {
            if (ConditionList.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem container)
            {
                continue;
            }

            if (GetElementBounds(container, ConditionList).IntersectsWith(selectionBounds))
            {
                selectedIndexes.Add(i);
            }
        }

        ConditionList.SelectedItems.Clear();
        foreach (var index in selectedIndexes.Where(index => index >= 0 && index < ConditionList.Items.Count).Order())
        {
            ConditionList.SelectedItems.Add(ConditionList.Items[index]);
        }
    }

    private void ShowConditionSelectionRectangle(Rect bounds)
    {
        ConditionSelectionRectangle.Width = Math.Max(1, bounds.Width);
        ConditionSelectionRectangle.Height = Math.Max(1, bounds.Height);
        if (ConditionSelectionRectangle.RenderTransform is not TranslateTransform transform)
        {
            transform = new TranslateTransform();
            ConditionSelectionRectangle.RenderTransform = transform;
        }

        transform.X = bounds.X;
        transform.Y = bounds.Y;
        ConditionSelectionRectangle.Visibility = Visibility.Visible;
    }

    private static Rect CreateSelectionBounds(Point start, Point end)
    {
        return new Rect(
            new Point(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y)),
            new Point(Math.Max(start.X, end.X), Math.Max(start.Y, end.Y)));
    }

    private static Rect GetElementBounds(FrameworkElement element, UIElement relativeTo)
    {
        var topLeft = element.TranslatePoint(new Point(0, 0), relativeTo);
        return new Rect(topLeft, new Size(element.ActualWidth, element.ActualHeight));
    }

    private static bool ShouldStartConditionBoxSelection(DependencyObject? source)
    {
        return FindVisualParent<ListBoxItem>(source) is null
            && FindVisualParent<System.Windows.Controls.Primitives.ButtonBase>(source) is null
            && FindVisualParent<System.Windows.Controls.Primitives.ScrollBar>(source) is null;
    }

    private int GetConditionIndexFromSource(DependencyObject? source)
    {
        var item = FindVisualParent<ListBoxItem>(source);
        if (item?.DataContext is null)
        {
            return -1;
        }

        var index = ConditionList.Items.IndexOf(item.DataContext);
        return index >= 0 && index < conditions.Count ? index : -1;
    }

    private int GetConditionDropIndex(Point point)
    {
        for (var i = 0; i < ConditionList.Items.Count; i++)
        {
            if (ConditionList.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem container)
            {
                continue;
            }

            var midPoint = container.TranslatePoint(new Point(0, container.ActualHeight / 2), ConditionList);
            var bottomPoint = container.TranslatePoint(new Point(0, container.ActualHeight), ConditionList);
            if (point.Y > bottomPoint.Y)
            {
                continue;
            }

            return point.Y < midPoint.Y ? i : i + 1;
        }

        return conditions.Count;
    }

    private void LoadEditor(ConditionalDirective cond)
    {
        loadingEditor = true;
        CondNameBox.Text = cond.Name;
        SelectStepChoice(StartStepCombo, cond.StartStepPathText, cond.StartStepIndex);
        SelectStepChoice(EndStepCombo, cond.EndStepPathText, cond.EndStepIndex);
        WindowStartMsBox.Text = cond.WindowStart is { } start ? FormatMs(start) : string.Empty;
        WindowEndMsBox.Text = cond.WindowEnd is { } end ? FormatMs(end) : string.Empty;
        SetTimeWindowValidity(true);

        var typeIndex = cond.Condition.Type switch
        {
            "pixel" => 0,
            "template" => 1,
            "pixelHash" => 2,
            "text" => 3,
            _ => 0
        };
        CondTypeCombo.SelectedIndex = typeIndex;
        UpdateTypeVisibility(cond.Condition.Type);

        if (cond.Condition is PixelMatcher pm)
        {
            ColorRBox.Text = pm.Expected.R.ToString();
            ColorGBox.Text = pm.Expected.G.ToString();
            ColorBBox.Text = pm.Expected.B.ToString();
            ToleranceBox.Text = pm.Tolerance.ToString();
            ColorPreview.Background = new SolidColorBrush(Color.FromRgb(pm.Expected.R, pm.Expected.G, pm.Expected.B));
        }
        else if (cond.Condition is TextMatcher tm)
        {
            ExpectedTextBox.Text = tm.ExpectedText;
            ContainsCheckBox.IsChecked = tm.Contains;
        }

        RegionInfoText.Text = $"({cond.Condition switch
        {
            PixelMatcher p => $"{p.Region.TopLeft.X},{p.Region.TopLeft.Y} ~ {p.Region.BottomRight.X},{p.Region.BottomRight.Y}",
            TemplateMatcher t => $"{t.Region.TopLeft.X},{t.Region.TopLeft.Y} ~ {t.Region.BottomRight.X},{t.Region.BottomRight.Y}",
            PixelHashMatcher h => $"{h.Region.TopLeft.X},{h.Region.TopLeft.Y} ~ {h.Region.BottomRight.X},{h.Region.BottomRight.Y}",
            TextMatcher tx => $"{tx.Region.TopLeft.X},{tx.Region.TopLeft.Y} ~ {tx.Region.BottomRight.X},{tx.Region.BottomRight.Y}",
            _ => "未设置"
        }})";

        ThenActionSequence.SetSteps(cond.ThenSteps);
        loadingEditor = false;
    }

    private void UpdateTypeVisibility(string type)
    {
        PixelOptionsPanel.Visibility = type == "pixel" ? Visibility.Visible : Visibility.Collapsed;
        TextOptionsPanel.Visibility = type == "text" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CondNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (loadingEditor || selectedIndex < 0 || selectedIndex >= conditions.Count) return;
        var c = conditions[selectedIndex];
        conditions[selectedIndex] = c with { Name = CondNameBox.Text };
        ConditionsModified?.Invoke(this, EventArgs.Empty);
    }

    private void CondTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loadingEditor || selectedIndex < 0 || selectedIndex >= conditions.Count) return;
        if (CondTypeCombo.SelectedItem is not ComboBoxItem item) return;
        var type = item.Tag?.ToString() ?? "pixel";
        UpdateTypeVisibility(type);
    }

    private void StepRange_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loadingEditor || selectedIndex < 0 || selectedIndex >= conditions.Count) return;
        if (StartStepCombo.SelectedItem is not StepChoice startChoice
            || EndStepCombo.SelectedItem is not StepChoice endChoice)
        {
            return;
        }

        var start = startChoice.Index;
        var end = endChoice.Index;
        var c = conditions[selectedIndex];
        if (end < start)
        {
            end = start;
            SelectStepChoice(EndStepCombo, startChoice.PathText, startChoice.Index);
        }
        else
        {
            startChoice = (StepChoice)StartStepCombo.SelectedItem;
        }

        var effectiveEndChoice = EndStepCombo.SelectedItem is StepChoice selectedEnd ? selectedEnd : startChoice;
        conditions[selectedIndex] = c with
        {
            StartStepIndex = start,
            EndStepIndex = end,
            StartStepPath = startChoice.Path,
            EndStepPath = effectiveEndChoice.Path
        };
        ConditionsModified?.Invoke(this, EventArgs.Empty);
        ConditionSelectionChanged?.Invoke(this,
            new ConditionSelectionChangedEventArgs(selectedIndex, conditions[selectedIndex]));
    }

    private void TimeWindowBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (loadingEditor || selectedIndex < 0 || selectedIndex >= conditions.Count) return;
        if (!TryReadTimeWindow(out var windowStart, out var windowEnd))
        {
            SetTimeWindowValidity(false);
            return;
        }

        SetTimeWindowValidity(true);
        var c = conditions[selectedIndex];
        conditions[selectedIndex] = c with
        {
            WindowStart = windowStart,
            WindowEnd = windowEnd
        };
        ConditionsModified?.Invoke(this, EventArgs.Empty);
        ConditionSelectionChanged?.Invoke(this,
            new ConditionSelectionChangedEventArgs(selectedIndex, conditions[selectedIndex]));
    }

    private void PickRegion_Click(object sender, RoutedEventArgs e)
    {
        PickRegionRequested?.Invoke(this, EventArgs.Empty);
    }

    private void PickConditionColor_Click(object sender, RoutedEventArgs e)
    {
        if (selectedIndex < 0 || selectedIndex >= conditions.Count) return;

        var picker = new ScreenCoordinatePickerWindow
        {
            Owner = Window.GetWindow(this)
        };

        if (picker.ShowDialog() != true) return;
        if (!ScreenPixelSampler.TryReadPixel(picker.SelectedX, picker.SelectedY, out var color)) return;

        ApplyPickedConditionColor(picker.SelectedX, picker.SelectedY, color);
    }

    private void ApplyPickedConditionColor(int x, int y, RgbColor color)
    {
        if (selectedIndex < 0 || selectedIndex >= conditions.Count) return;

        var editIndex = selectedIndex;
        var condition = conditions[editIndex];
        var region = ScreenRegion.FromSinglePixel(x, y);
        var tolerance = condition.Condition is PixelMatcher pixel
            ? pixel.Tolerance
            : (byte)10;

        conditions[editIndex] = condition with
        {
            Condition = new PixelMatcher(region, color, tolerance)
        };

        ColorRBox.Text = color.R.ToString();
        ColorGBox.Text = color.G.ToString();
        ColorBBox.Text = color.B.ToString();
        ToleranceBox.Text = tolerance.ToString();
        ColorPreview.Background = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
        RefreshList();
        ConditionList.SelectedIndex = editIndex;
        LoadEditor(conditions[editIndex]);
        ConditionsModified?.Invoke(this, EventArgs.Empty);
        ConditionSelectionChanged?.Invoke(this,
            new ConditionSelectionChangedEventArgs(editIndex, conditions[editIndex]));
    }

    public bool IsThenActionSequenceActive => thenActionSequenceActive
        && selectedIndex >= 0
        && selectedIndex < conditions.Count;

    public void DeactivateThenActionSequence()
    {
        thenActionSequenceActive = false;
        ThenActionSequence.DeactivateSequence();
    }

    public bool TryInsertActionTemplateIntoActiveCondition(MacroActionTemplateKind kind)
    {
        if (!IsThenActionSequenceActive)
        {
            return false;
        }

        ThenActionSequence.InsertSteps(MacroActionTemplateFactory.CreateSteps(kind));
        return true;
    }

    public bool HandleExplorerShortcut(Key key, ModifierKeys modifiers)
    {
        if (!IsKeyboardFocusWithin && !thenActionSequenceActive)
        {
            return false;
        }

        if (ThenActionSequence.IsActiveSequence && ThenActionSequence.HandleExplorerShortcut(key, modifiers))
        {
            return true;
        }

        if (key == Key.Delete && modifiers == ModifierKeys.None)
        {
            DeleteCondition_Click(this, new RoutedEventArgs());
            return true;
        }

        if ((modifiers & ModifierKeys.Control) == 0 || (modifiers & (ModifierKeys.Alt | ModifierKeys.Shift)) != 0)
        {
            return false;
        }

        return key switch
        {
            Key.A => SelectAllConditions(),
            Key.C => CopySelectedConditionsToClipboard(),
            Key.X => CutSelectedConditionsToClipboard(),
            Key.V => PasteConditionsFromClipboard(),
            _ => false
        };
    }

    public void CloseInlineStepEditorOnExternalPointerDown(DependencyObject? source)
    {
        ThenActionSequence.CloseInlineStepEditorOnExternalPointerDown(source);
    }

    private void OnThenActionSequenceStepsChanged()
    {
        if (loadingEditor || selectedIndex < 0 || selectedIndex >= conditions.Count)
        {
            return;
        }

        var editIndex = selectedIndex;
        conditions[editIndex] = conditions[editIndex] with { ThenSteps = ThenActionSequence.Steps.ToList() };
        RefreshList();
        ConditionList.SelectedIndex = editIndex;
        ConditionsModified?.Invoke(this, EventArgs.Empty);
        ConditionSelectionChanged?.Invoke(this,
            new ConditionSelectionChangedEventArgs(editIndex, conditions[editIndex]));
    }

    private void OnThenActionTemplateDropped(MacroActionTemplateKind kind, string parentPathText, int insertIndex)
    {
        ThenActionSequence.InsertStepsAtPath(MacroActionTemplateFactory.CreateSteps(kind), parentPathText, insertIndex);
    }

    private void OnThenMacroLibraryDropped(string macroId, string parentPathText, int insertIndex)
    {
        if (editorState is null)
        {
            return;
        }

        try
        {
            var document = editorState.LibraryStore.ReadMacro(macroId);
            ThenActionSequence.InsertStepsAtPath(document.Steps, parentPathText, insertIndex);
        }
        catch
        {
        }
    }

    public void SetRegion(ScreenRegion region)
    {
        if (selectedIndex < 0 || selectedIndex >= conditions.Count) return;
        var c = conditions[selectedIndex];
        var newMatcher = c.Condition switch
        {
            PixelMatcher pm => (IConditionMatcher)(pm with { Region = region }),
            TemplateMatcher tm => tm with { Region = region },
            PixelHashMatcher hm => hm with { Region = region },
            TextMatcher txm => txm with { Region = region },
            _ => c.Condition
        };
        conditions[selectedIndex] = c with { Condition = newMatcher };
        LoadEditor(conditions[selectedIndex]);
        ConditionsModified?.Invoke(this, EventArgs.Empty);
    }

    private string DescribeRange(ConditionalDirective directive)
    {
        var startLabel = FindStepChoiceLabel(directive.StartStepPathText, directive.StartStepIndex);
        var endLabel = FindStepChoiceLabel(directive.EndStepPathText, directive.EndStepIndex);
        var range = $"{startLabel} ~ {endLabel}";
        if (directive.WindowStart is null && directive.WindowEnd is null)
        {
            return range;
        }

        var start = directive.WindowStart is { } windowStart ? FormatMs(windowStart) : "0";
        var end = directive.WindowEnd is { } windowEnd ? FormatMs(windowEnd) : "结束";
        return $"{range} · 触发后 {start}~{end} ms";
    }

    private string FindStepChoiceLabel(string pathText, int stepIndex)
    {
        var match = stepChoices.FirstOrDefault(choice =>
            (!string.IsNullOrWhiteSpace(pathText)
                && string.Equals(choice.PathText, pathText, StringComparison.Ordinal))
            || choice.Index == stepIndex);
        return match?.Label ?? $"步骤 #{stepIndex + 1}";
    }

    private static void SelectStepChoice(ComboBox comboBox, string pathText, int stepIndex)
    {
        foreach (var choice in comboBox.Items.OfType<StepChoice>())
        {
            if ((!string.IsNullOrWhiteSpace(pathText)
                    && string.Equals(choice.PathText, pathText, StringComparison.Ordinal))
                || choice.Index == stepIndex)
            {
                comboBox.SelectedItem = choice;
                return;
            }
        }

        comboBox.SelectedIndex = comboBox.Items.Count > 0 ? 0 : -1;
    }

    private bool TryReadTimeWindow(out TimeSpan? start, out TimeSpan? end)
    {
        start = null;
        end = null;
        if (!TryReadOptionalMs(WindowStartMsBox.Text, out start)
            || !TryReadOptionalMs(WindowEndMsBox.Text, out end))
        {
            return false;
        }

        return start is null || end is null || end >= start;
    }

    private void SetTimeWindowValidity(bool valid)
    {
        TimeWindowErrorText.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
        WindowStartMsBox.BorderBrush = valid ? null : Brushes.Red;
        WindowEndMsBox.BorderBrush = valid ? null : Brushes.Red;
        WindowStartMsBox.ToolTip = valid ? null : "请输入非负毫秒，并确保结束时间不小于开始时间。";
        WindowEndMsBox.ToolTip = WindowStartMsBox.ToolTip;
    }

    private static bool TryReadOptionalMs(string value, out TimeSpan? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!double.TryParse(value.Trim(), out var ms) || ms < 0)
        {
            return false;
        }

        result = TimeSpan.FromMilliseconds(ms);
        return true;
    }

    private static string FormatMs(TimeSpan value)
    {
        var ms = value.TotalMilliseconds;
        return Math.Abs(ms - Math.Round(ms)) < 0.0001
            ? ((int)Math.Round(ms)).ToString()
            : ms.ToString("0.####");
    }

    private static T? FindVisualParent<T>(DependencyObject? source)
        where T : DependencyObject
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }
}

public sealed class ConditionDisplayItem
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string RangeBadge { get; set; } = "";
    public string TypeIcon { get; set; } = "";
    public Brush ColorBrush { get; set; } = Brushes.Gray;
    public Brush StatusBrush { get; set; } = Brushes.Gray;
    public string StatusText { get; set; } = "";
}

public sealed class ConditionSelectionChangedEventArgs : EventArgs
{
    public int Index { get; }
    public ConditionalDirective? Directive { get; }

    public ConditionSelectionChangedEventArgs(int index, ConditionalDirective? directive)
    {
        Index = index;
        Directive = directive;
    }
}
