using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MacroHid.Core;
using MacroHid.Runtime;
using MacroStudio.Services;

namespace MacroStudio.Controls;

public partial class ConditionDirectivePanel : UserControl
{
    private const string ConditionClipboardPrefix = "MacroHID.Conditions.v1";
    private const string ConditionDragFormat = "MacroHID.ConditionIndexes";
    private const string MacroLibraryDragFormat = "MacroHID.MacroLibraryItem";
    private const int WM_MOUSEWHEEL_LOW_LEVEL = 0x020A;
    private const int WH_MOUSE_LL = 14;
    private const double WheelDelta = 120.0;
    private const double AutoScrollEdgeSize = 42;

    private List<ConditionalDirective> conditions = [];
    private IReadOnlyList<StepChoice> stepChoices = [];
    private MacroEditorState? editorState;
    private int selectedIndex = -1;
    private bool loadingEditor;
    private bool conditionBoxSelectionActive;
    private bool suppressConditionSelectionChanged;
    private Point conditionBoxSelectionStartPoint;
    private readonly HashSet<int> conditionBoxSelectionBaseIndexes = [];
    private Point conditionDragStartPoint;
    private bool conditionDragStarted;
    private int conditionDragStartIndex = -1;
    private bool thenActionSequenceActive;
    private bool isReadOnly;
    private bool isRecording;
    private bool conditionListDragInProgress;
    private ScrollViewer? conditionListScrollViewer;
    private IntPtr conditionDragMouseHookHandle;
    private readonly LowLevelMouseProc conditionDragMouseHookProc;

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
    public event Action<MacroRecordingMode>? RecordingStartRequested;
    public event Action? RecordingStopRequested;
    public event Action<string>? StatusMessageRequested;
    public event Action<IReadOnlyList<ConditionalDirective>, string>? ExtractConditionPackRequested;
    public event Action<string>? MacroLibraryDroppedOnConditionList;
    public event Action? InsertSelectedLibraryConditionMacroRequested;

    public ConditionDirectivePanel()
    {
        InitializeComponent();
        conditionDragMouseHookProc = ConditionDragMouseHookCallback;
        Unloaded += ConditionDirectivePanel_Unloaded;
        OcrStatusText.Text = PaddleOcrBridge.DefaultStatusText;
        PaddleOcrBridge.RecognitionCompleted += OnOcrRecognitionCompleted;
        ThenActionPalette.ActionClicked += OnThenActionPaletteClicked;
        ThenActionPalette.ApplyLocalization();
    }

    private void ConditionDirectivePanel_Unloaded(object sender, RoutedEventArgs e)
    {
        EndConditionListDragInteraction();
    }

    public IReadOnlyList<ConditionalDirective> Conditions => conditions;
    public bool HasValidationErrors => selectedIndex >= 0
        && selectedIndex < conditions.Count
        && conditions[selectedIndex].ExecutionMode != ConditionExecutionMode.GateMainSequence
        && (!conditions[selectedIndex].HasActivationConstraint
            || !TryReadTimeWindow(out _, out _)
            || conditions[selectedIndex].Condition is TextMatcher { UseRegex: true } text
            && !PaddleOcrBridge.IsValidRegex(text.ExpectedText, out _));
    public bool CanRecordThenActions => !isReadOnly
        && selectedIndex >= 0
        && selectedIndex < conditions.Count;

    public void ApplyLocalization()
    {
        ConditionSequenceTitleText.Text = LocalizationService.Get("ConditionListTitle");
        AddConditionButton.Content = LocalizationService.Get("AddCondition");
        DeleteConditionButton.Content = LocalizationService.Get("DeleteCondition");
        ConditionLibraryMenuButton.Content = LocalizationService.Get("ConditionLibraryMenu");
        ConditionLibraryMenuButton.ToolTip = LocalizationService.Get("ConditionLibraryMenuHelp");
        InsertLibraryConditionMacroMenuItem.Header = LocalizationService.Get("InsertLibraryConditionMacro");
        InsertLibraryConditionMacroMenuItem.ToolTip = LocalizationService.Get("InsertLibraryConditionMacroHelp");
        ExtractConditionPackMenuItem.Header = LocalizationService.Get("ExtractConditionPack");
        ExtractConditionPackMenuItem.ToolTip = LocalizationService.Get("ExtractConditionPackHelp");
        EmptyConditionHintText.Text = LocalizationService.Get("EmptyConditionHint");
        ThenActionsLabelText.Text = LocalizationService.Get("ThenActionsTitle");
        ThenActionsHintText.Text = LocalizationService.Get("ThenActionsHint");
        AddThenActionButton.Content = LocalizationService.Get("AddThenAction");
        ThenActionEmptyHintText.Text = LocalizationService.Get("ThenActionEmptyHint");
        GateModeHintText.Text = LocalizationService.Get("ConditionGateModeHint");
        ConditionStepRangeLabelText.Text = LocalizationService.Get("ConditionStepRangeTitle");
        ConditionStepRangeHintText.Text = LocalizationService.Get("ConditionStepRangeOrTimeHint");
        ConditionTimeWindowLabelText.Text = LocalizationService.Get("ConditionTimeWindowTitle");
        TimeBasePlaybackItem.Content = LocalizationService.Get("ConditionTimeBasePlayback");
        TimeBaseAfterPreviousItem.Content = LocalizationService.Get("ConditionTimeBaseAfterPrevious");
        TimeBaseMainIterationItem.Content = LocalizationService.Get("ConditionTimeBaseMainIteration");
        TimeWindowErrorText.Text = LocalizationService.Get("ConditionActivationInvalid");
        ConditionRegionLabelText.Text = LocalizationService.Get("ConditionRegionTitle");
        PickRegionButton.Content = LocalizationService.Get("PickScreenRegion");
        SinglePixelModeCheckBox.Content = LocalizationService.Get("ConditionRegionSinglePixelMode");
        RegionTopLeftLabelText.Text = LocalizationService.Get("ConditionRegionTopLeft");
        RegionBottomRightLabelText.Text = LocalizationService.Get("ConditionRegionBottomRight");
        UpdateTimeBaseHint();
        if (selectedIndex >= 0 && selectedIndex < conditions.Count)
        {
            SetStepChoices(stepChoices);
        }
        RecordThenActionsButton.Content = LocalizationService.Get(isRecording ? "StopRecording" : "StartRecording");
        RecordThenActionsButton.ToolTip = LocalizationService.Get("RecordingHelp");
        ThenActionPalette.ApplyLocalization();
        UpdateThenActionEmptyHint();
        RefreshList();
    }

    public void SetReadOnly(bool value)
    {
        isReadOnly = value;
        AddConditionButton.IsEnabled = !value;
        DeleteConditionButton.IsEnabled = !value;
        ConditionLibraryMenuButton.IsEnabled = !value;
        InsertLibraryConditionMacroMenuItem.IsEnabled = !value;
        ExtractConditionPackMenuItem.IsEnabled = !value;
        ConditionList.AllowDrop = !value;
        ConditionListDropHost.AllowDrop = !value;
        CondNameBox.IsReadOnly = value;
        CondTypeCombo.IsEnabled = !value;
        ExecutionModeCombo.IsEnabled = !value;
        TimeBaseCombo.IsEnabled = !value;
        StartStepCombo.IsEnabled = !value;
        EndStepCombo.IsEnabled = !value;
        WindowStartMsBox.IsReadOnly = value;
        WindowEndMsBox.IsReadOnly = value;
        PickRegionButton.IsEnabled = !value;
        SinglePixelModeCheckBox.IsEnabled = !value;
        RegionXBox.IsReadOnly = value;
        RegionYBox.IsReadOnly = value;
        RegionLeftBox.IsReadOnly = value;
        RegionTopBox.IsReadOnly = value;
        RegionRightBox.IsReadOnly = value;
        RegionBottomBox.IsReadOnly = value;
        ColorRBox.IsReadOnly = value;
        ColorGBox.IsReadOnly = value;
        ColorBBox.IsReadOnly = value;
        ToleranceBox.IsReadOnly = value;
        PickConditionColorButton.IsEnabled = !value;
        ExpectedTextBox.IsReadOnly = value;
        ContainsCheckBox.IsEnabled = !value;
        RegexCheckBox.IsEnabled = !value;
        UpdateRegexValidity();
        AddThenActionButton.IsEnabled = !value;
        ThenActionSequence.SetReadOnly(value || isRecording);
        ThenActionPalette.SetReadOnly(value || isRecording);
        UpdateRecordingButtonState();
        if (value)
        {
            ThenActionPalettePopup.IsOpen = false;
            FinishConditionBoxSelection();
        }
    }

    public void SetRecordingState(bool recording)
    {
        isRecording = recording;
        ConditionList.IsEnabled = !recording;
        AddThenActionButton.IsEnabled = !isReadOnly && !recording;
        ThenActionSequence.SetReadOnly(isReadOnly || recording);
        ThenActionPalette.SetReadOnly(isReadOnly || recording);
        if (recording)
        {
            ThenActionPalettePopup.IsOpen = false;
        }

        ApplyLocalization();
        UpdateRecordingButtonState();
    }

    public bool InsertRecordedThenSteps(IReadOnlyList<MacroStep> steps)
    {
        if (isReadOnly || steps.Count == 0 || !TryGetSelectedCondition(out _, out _))
        {
            return false;
        }

        thenActionSequenceActive = true;
        ThenActionSequence.InsertSteps(steps);
        return true;
    }

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
            var unlimited = CreateUnlimitedStepChoice();
            var items = new List<StepChoice> { unlimited };
            items.AddRange(choices);
            StartStepCombo.ItemsSource = items;
            EndStepCombo.ItemsSource = items;
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

    private static StepChoice CreateUnlimitedStepChoice() =>
        new(-1, LocalizationService.Get("ConditionStepRangeUnlimited"), []);

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
        var restoreIndex = selectedIndex;
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
                StatusBrush = c.ExecutionMode == ConditionExecutionMode.GateMainSequence
                    ? new SolidColorBrush(Color.FromRgb(14, 165, 233))
                    : Brushes.Gray,
                StatusText = c.ExecutionMode switch
                {
                    ConditionExecutionMode.GateMainSequence => LocalizationService.Get("ConditionGateBadge"),
                    ConditionExecutionMode.PauseMainTimeline => LocalizationService.Get("ConditionPauseBadge"),
                    _ => LocalizationService.Get("ConditionReadyBadge")
                }
            });
        }

        suppressConditionSelectionChanged = true;
        try
        {
            ConditionList.ItemsSource = items;
            selectedIndex = ClampConditionIndex(restoreIndex);
            ConditionList.SelectedIndex = selectedIndex >= 0 && selectedIndex < ConditionList.Items.Count
                ? selectedIndex
                : -1;
        }
        finally
        {
            suppressConditionSelectionChanged = false;
        }

        UpdateRecordingButtonState();

        EmptyConditionHintText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string DescribeMatcher(IConditionMatcher matcher) => matcher switch
    {
        PixelMatcher p => $"像素 ({p.Region.TopLeft.X},{p.Region.TopLeft.Y}) RGB=({p.Expected.R},{p.Expected.G},{p.Expected.B})",
        TextMatcher t => t.UseRegex ? $"文字正则: /{t.ExpectedText}/" : $"文字: \"{t.ExpectedText}\"",
        TemplateMatcher or PixelHashMatcher => "已移除的条件类型",
        _ => "未知条件"
    };

    private static string GetTypeIcon(string type) => type switch
    {
        "pixel" => "🎯",
        "text" => "T",
        _ => "?"
    };

    private void AddCondition_Click(object sender, RoutedEventArgs e)
    {
        if (isReadOnly) return;
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
        Dispatcher.BeginInvoke(new Action(ScrollThenActionsIntoView), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void ConditionLibraryMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (ConditionLibraryContextMenu is null)
        {
            return;
        }

        ConditionLibraryContextMenu.PlacementTarget = ConditionLibraryMenuButton;
        ConditionLibraryContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        ConditionLibraryContextMenu.IsOpen = true;
    }

    private void ExtractConditionPack_Click(object sender, RoutedEventArgs e)
    {
        if (isReadOnly)
        {
            return;
        }

        var indexes = GetSelectedConditionIndexes();
        var source = indexes.Count > 0
            ? indexes.Select(index => conditions[index]).ToList()
            : conditions.ToList();
        if (source.Count == 0)
        {
            StatusMessageRequested?.Invoke(LocalizationService.Get("ExtractConditionPackEmpty"));
            return;
        }

        var name = source.Count == 1 && !string.IsNullOrWhiteSpace(source[0].Name)
            ? source[0].Name.Trim()
            : LocalizationService.Format("ExtractedConditionPackName", source.Count);
        var pack = ConditionPackCloner.CloneWithNewIds(source);
        ExtractConditionPackRequested?.Invoke(pack, name);
    }

    private void InsertLibraryConditionMacro_Click(object sender, RoutedEventArgs e)
    {
        if (isReadOnly)
        {
            return;
        }

        InsertSelectedLibraryConditionMacroRequested?.Invoke();
    }

    public void InsertConditionPack(IReadOnlyList<ConditionalDirective> pack, string? packDisplayName = null)
    {
        if (isReadOnly || pack.Count == 0)
        {
            return;
        }

        var cloned = ConditionPackCloner.CloneWithNewIds(pack);
        var firstChoice = stepChoices.FirstOrDefault();
        var packName = string.IsNullOrWhiteSpace(packDisplayName) ? null : packDisplayName.Trim();
        for (var i = 0; i < cloned.Count; i++)
        {
            var condition = cloned[i];
            var preferredName = packName is null
                ? condition.Name
                : cloned.Count == 1
                    ? packName
                    : $"{packName} ({i + 1})";
            var startIndex = firstChoice?.Index ?? condition.StartStepIndex;
            var endIndex = firstChoice?.Index ?? condition.EndStepIndex;
            var startPath = firstChoice?.Path ?? condition.StartStepPath;
            var endPath = firstChoice?.Path ?? condition.EndStepPath;
            conditions.Add(condition with
            {
                Name = EnsureUniqueConditionName(preferredName),
                StartStepIndex = startIndex,
                EndStepIndex = endIndex,
                StartStepPath = startPath,
                EndStepPath = endPath
            });
        }

        RefreshList();
        ConditionList.SelectedIndex = conditions.Count - 1;
        ConditionsModified?.Invoke(this, EventArgs.Empty);
        Dispatcher.BeginInvoke(new Action(ScrollThenActionsIntoView), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void DeleteCondition_Click(object sender, RoutedEventArgs e)
    {
        if (isReadOnly) return;
        var indexes = GetSelectedConditionIndexes();
        DeleteConditions(indexes);
    }

    private void ConditionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressConditionSelectionChanged)
        {
            return;
        }

        selectedIndex = ConditionList.SelectedIndex >= 0
            ? ConditionList.SelectedIndex
            : GetSelectedConditionIndexes().FirstOrDefault(-1);
        if (TryGetSelectedCondition(out var index, out var directive))
        {
            LoadEditor(directive);
            EditorBorder.Visibility = Visibility.Visible;
            EditorGrid.Visibility = Visibility.Visible;
            UpdateThenActionEmptyHint();
            ConditionSelectionChanged?.Invoke(this,
                new ConditionSelectionChangedEventArgs(index, directive));
            Dispatcher.BeginInvoke(new Action(ScrollThenActionsIntoView), System.Windows.Threading.DispatcherPriority.Loaded);
        }
        else
        {
            EditorBorder.Visibility = Visibility.Collapsed;
            EditorGrid.Visibility = Visibility.Collapsed;
            ThenActionSequence.SetSteps([]);
            UpdateThenActionEmptyHint();
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
        if (isReadOnly && !conditionBoxSelectionActive) return;
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
        BeginConditionListDragInteraction();
        try
        {
            DragDrop.DoDragDrop(ConditionList, new DataObject(ConditionDragFormat, string.Join(",", indexes)), DragDropEffects.Move);
        }
        finally
        {
            EndConditionListDragInteraction();
        }

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
        if (isReadOnly)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (e.Data.GetDataPresent(ConditionDragFormat))
        {
            BeginConditionListDragInteraction();
            AutoScrollConditionList(e.GetPosition(ConditionList));
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            return;
        }

        if (TryReadLibraryMacroIds(e.Data, out _))
        {
            BeginConditionListDragInteraction();
            AutoScrollConditionList(e.GetPosition(ConditionList));
            e.Effects = (e.AllowedEffects & DragDropEffects.Copy) != 0
                ? DragDropEffects.Copy
                : DragDropEffects.Move;
            e.Handled = true;
        }
    }

    private void ConditionList_Drop(object sender, DragEventArgs e)
    {
        try
        {
            if (isReadOnly)
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            if (TryReadLibraryMacroIds(e.Data, out var macroIds))
            {
                foreach (var macroId in macroIds)
                {
                    MacroLibraryDroppedOnConditionList?.Invoke(macroId);
                }

                e.Handled = true;
                return;
            }

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
        finally
        {
            EndConditionListDragInteraction();
        }
    }

    private static bool TryReadLibraryMacroIds(IDataObject data, out string[] macroIds)
    {
        if (!data.GetDataPresent(MacroLibraryDragFormat))
        {
            macroIds = [];
            return false;
        }

        switch (data.GetData(MacroLibraryDragFormat))
        {
            case string id when !string.IsNullOrWhiteSpace(id):
                macroIds = [id];
                return true;
            case string[] ids when ids.Length > 0:
                macroIds = ids.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                return macroIds.Length > 0;
            default:
                macroIds = [];
                return false;
        }
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
        if (isReadOnly) return false;
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
        if (isReadOnly) return false;
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

    private string EnsureUniqueConditionName(string name)
    {
        var baseName = string.IsNullOrWhiteSpace(name) ? "条件" : name.Trim();
        if (!conditions.Any(condition => string.Equals(condition.Name, baseName, StringComparison.CurrentCultureIgnoreCase)))
        {
            return baseName;
        }

        var counter = 2;
        while (true)
        {
            var candidate = $"{baseName} ({counter++})";
            if (!conditions.Any(condition => string.Equals(condition.Name, candidate, StringComparison.CurrentCultureIgnoreCase)))
            {
                return candidate;
            }
        }
    }

    private void DeleteConditions(IReadOnlyList<int> indexes)
    {
        if (isReadOnly) return;
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
        if (isReadOnly) return;
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
        ExecutionModeCombo.SelectedIndex = cond.ExecutionMode switch
        {
            ConditionExecutionMode.PauseMainTimeline => 1,
            ConditionExecutionMode.GateMainSequence => 2,
            _ => 0
        };
        TimeBaseCombo.SelectedIndex = cond.TimeBase switch
        {
            ConditionTimeBase.AfterPreviousCondition => 1,
            ConditionTimeBase.MainIteration => 2,
            _ => 0
        };
        UpdateTimeBaseHint();
        UpdateExecutionModeVisibility(cond.ExecutionMode);

        var typeIndex = cond.Condition.Type switch
        {
            "pixel" => 0,
            "text" => 1,
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
            RegexCheckBox.IsChecked = tm.UseRegex;
            UpdateRegexValidity();
        }

        RegionInfoText.Text = $"({cond.Condition switch
        {
            PixelMatcher p => FormatRegionSummary(p.Region),
            TemplateMatcher t => FormatRegionSummary(t.Region),
            PixelHashMatcher h => FormatRegionSummary(h.Region),
            TextMatcher tx => FormatRegionSummary(tx.Region),
            _ => LocalizationService.Get("ConditionRegionUnset")
        }})";
        LoadRegionEditor(GetMatcherRegion(cond.Condition));

        ThenActionSequence.SetSteps(cond.ThenSteps);
        loadingEditor = false;
        UpdateThenActionEmptyHint();
    }

    private void UpdateTypeVisibility(string type)
    {
        PixelOptionsPanel.Visibility = type == "pixel" ? Visibility.Visible : Visibility.Collapsed;
        TextOptionsPanel.Visibility = type == "text" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CondNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (isReadOnly) return;
        if (loadingEditor || !TryGetSelectedCondition(out var editIndex, out var c)) return;
        var name = CondNameBox.Text;
        conditions[editIndex] = c with { Name = name };
        if (editIndex >= 0
            && editIndex < ConditionList.Items.Count
            && ConditionList.Items[editIndex] is ConditionDisplayItem item)
        {
            item.Name = name;
        }

        ConditionsModified?.Invoke(this, EventArgs.Empty);
    }

    private void CondTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (isReadOnly) return;
        if (loadingEditor || !TryGetSelectedCondition(out var editIndex, out var condition)) return;
        if (CondTypeCombo.SelectedItem is not ComboBoxItem item) return;
        var type = item.Tag?.ToString() ?? "pixel";
        conditions[editIndex] = condition with
        {
            Condition = CreateMatcherForSelectedType(type, condition.Condition)
        };
        UpdateTypeVisibility(type);
        LoadEditor(conditions[editIndex]);
        ConditionsModified?.Invoke(this, EventArgs.Empty);
        ConditionSelectionChanged?.Invoke(this,
            new ConditionSelectionChangedEventArgs(editIndex, conditions[editIndex]));
    }

    private void ExecutionModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (isReadOnly) return;
        if (loadingEditor || !TryGetSelectedCondition(out var editIndex, out var condition)) return;
        var mode = ExecutionModeCombo.SelectedItem is ComboBoxItem { Tag: string tag }
            && Enum.TryParse<ConditionExecutionMode>(tag, out var parsed)
            ? parsed
            : ConditionExecutionMode.Parallel;
        var isGate = mode == ConditionExecutionMode.GateMainSequence;
        conditions[editIndex] = condition with { ExecutionMode = mode };
        if (isGate)
        {
            conditions[editIndex] = conditions[editIndex] with
            {
                StartStepIndex = -1,
                EndStepIndex = -1,
                StartStepPath = null,
                EndStepPath = null,
                WindowStart = null,
                WindowEnd = null,
                TimeBase = ConditionTimeBase.PlaybackTrigger
            };
        }

        UpdateExecutionModeVisibility(mode);
        RefreshList();
        selectedIndex = editIndex;
        ConditionList.SelectedIndex = editIndex;
        LoadEditor(conditions[editIndex]);
        ConditionsModified?.Invoke(this, EventArgs.Empty);
        ConditionSelectionChanged?.Invoke(this,
            new ConditionSelectionChangedEventArgs(editIndex, conditions[editIndex]));
    }

    private void TimeBaseCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (isReadOnly) return;
        if (loadingEditor || !TryGetSelectedCondition(out var editIndex, out var condition)) return;
        var timeBase = TimeBaseCombo.SelectedItem is ComboBoxItem { Tag: string tag }
            && Enum.TryParse<ConditionTimeBase>(tag, out var parsed)
            ? parsed
            : ConditionTimeBase.PlaybackTrigger;
        conditions[editIndex] = condition with { TimeBase = timeBase };
        UpdateTimeBaseHint();
        RefreshList();
        selectedIndex = editIndex;
        ConditionList.SelectedIndex = editIndex;
        ConditionsModified?.Invoke(this, EventArgs.Empty);
        ConditionSelectionChanged?.Invoke(this,
            new ConditionSelectionChangedEventArgs(editIndex, conditions[editIndex]));
    }

    private void UpdateTimeBaseHint()
    {
        var timeBase = TimeBaseCombo.SelectedItem is ComboBoxItem { Tag: string tag }
            && Enum.TryParse<ConditionTimeBase>(tag, out var parsed)
            ? parsed
            : ConditionTimeBase.PlaybackTrigger;
        ConditionTimeWindowHintText.Text = timeBase switch
        {
            ConditionTimeBase.AfterPreviousCondition => LocalizationService.Get("ConditionTimeBaseAfterPreviousHint"),
            ConditionTimeBase.MainIteration => LocalizationService.Get("ConditionTimeBaseMainIterationHint"),
            _ => LocalizationService.Get("ConditionTimeBasePlaybackHint")
        };
    }

    private void UpdateExecutionModeVisibility(ConditionExecutionMode mode)
    {
        var isGate = mode == ConditionExecutionMode.GateMainSequence;
        ThenActionsSection.Visibility = Visibility.Visible;
        GateModeHintText.Visibility = isGate ? Visibility.Visible : Visibility.Collapsed;
        ThenActionsHintText.Visibility = isGate ? Visibility.Collapsed : Visibility.Visible;
        StepRangeSection.Visibility = isGate ? Visibility.Collapsed : Visibility.Visible;
        TimeWindowSection.Visibility = isGate ? Visibility.Collapsed : Visibility.Visible;
        if (isGate)
        {
            TimeWindowErrorText.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateThenActionsVisibility(ConditionExecutionMode mode)
        => UpdateExecutionModeVisibility(mode);

    private void StepRange_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (isReadOnly) return;
        if (loadingEditor || !TryGetSelectedCondition(out var editIndex, out var directive)) return;
        if (StartStepCombo.SelectedItem is not StepChoice startChoice
            || EndStepCombo.SelectedItem is not StepChoice endChoice)
        {
            return;
        }

        if (startChoice.Index < 0 || endChoice.Index < 0)
        {
            loadingEditor = true;
            try
            {
                SelectStepChoice(StartStepCombo, string.Empty, -1);
                SelectStepChoice(EndStepCombo, string.Empty, -1);
            }
            finally
            {
                loadingEditor = false;
            }

            conditions[editIndex] = directive with
            {
                StartStepIndex = -1,
                EndStepIndex = -1,
                StartStepPath = null,
                EndStepPath = null
            };
            RefreshActivationValidity(editIndex);
            ConditionsModified?.Invoke(this, EventArgs.Empty);
            ConditionSelectionChanged?.Invoke(this,
                new ConditionSelectionChangedEventArgs(editIndex, conditions[editIndex]));
            return;
        }

        var start = startChoice.Index;
        var end = endChoice.Index;
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
        conditions[editIndex] = directive with
        {
            StartStepIndex = start,
            EndStepIndex = end,
            StartStepPath = startChoice.Path,
            EndStepPath = effectiveEndChoice.Path
        };
        RefreshActivationValidity(editIndex);
        ConditionsModified?.Invoke(this, EventArgs.Empty);
        ConditionSelectionChanged?.Invoke(this,
            new ConditionSelectionChangedEventArgs(editIndex, conditions[editIndex]));
    }

    private void TimeWindowBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (isReadOnly) return;
        if (loadingEditor || !TryGetSelectedCondition(out var editIndex, out var c)) return;
        if (!TryReadTimeWindow(out var windowStart, out var windowEnd))
        {
            SetTimeWindowValidity(false);
            return;
        }

        SetTimeWindowValidity(true);
        conditions[editIndex] = c with
        {
            WindowStart = windowStart,
            WindowEnd = windowEnd
        };
        RefreshActivationValidity(editIndex);
        ConditionsModified?.Invoke(this, EventArgs.Empty);
        ConditionSelectionChanged?.Invoke(this,
            new ConditionSelectionChangedEventArgs(editIndex, conditions[editIndex]));
    }

    private void RefreshActivationValidity(int editIndex)
    {
        if (editIndex < 0 || editIndex >= conditions.Count)
        {
            return;
        }

        if (conditions[editIndex].ExecutionMode == ConditionExecutionMode.GateMainSequence)
        {
            SetTimeWindowValidity(true);
            TimeWindowErrorText.Visibility = Visibility.Collapsed;
            return;
        }

        var valid = conditions[editIndex].HasActivationConstraint && TryReadTimeWindow(out _, out _);
        SetTimeWindowValidity(valid);
        if (!conditions[editIndex].HasActivationConstraint)
        {
            TimeWindowErrorText.Text = LocalizationService.Get("ConditionActivationInvalid");
            TimeWindowErrorText.Visibility = Visibility.Visible;
        }
    }

    private void PickRegion_Click(object sender, RoutedEventArgs e)
    {
        if (isReadOnly) return;
        PickRegionRequested?.Invoke(this, EventArgs.Empty);
    }

    private void SinglePixelModeCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (loadingEditor) return;
        var singlePixel = SinglePixelModeCheckBox.IsChecked == true;
        UpdateRegionModeVisibility(singlePixel);
        if (singlePixel)
        {
            var x = ReadInt(RegionLeftBox.Text, ReadInt(RegionXBox.Text, 0));
            var y = ReadInt(RegionTopBox.Text, ReadInt(RegionYBox.Text, 0));
            RegionXBox.Text = x.ToString();
            RegionYBox.Text = y.ToString();
        }
        else
        {
            var x = ReadInt(RegionXBox.Text, ReadInt(RegionLeftBox.Text, 0));
            var y = ReadInt(RegionYBox.Text, ReadInt(RegionTopBox.Text, 0));
            RegionLeftBox.Text = x.ToString();
            RegionTopBox.Text = y.ToString();
            if (string.IsNullOrWhiteSpace(RegionRightBox.Text))
            {
                RegionRightBox.Text = (x + 1).ToString();
            }

            if (string.IsNullOrWhiteSpace(RegionBottomBox.Text))
            {
                RegionBottomBox.Text = (y + 1).ToString();
            }
        }

        UpdateRegionFromEditor();
    }

    private void RegionCoordBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateRegionFromEditor();
    }

    private void UpdateRegionModeVisibility(bool singlePixel)
    {
        SinglePixelCoordPanel.Visibility = singlePixel ? Visibility.Visible : Visibility.Collapsed;
        RectCoordPanel.Visibility = singlePixel ? Visibility.Collapsed : Visibility.Visible;
    }

    private void LoadRegionEditor(ScreenRegion region)
    {
        var singlePixel = IsSinglePixelRegion(region);
        SinglePixelModeCheckBox.IsChecked = singlePixel;
        UpdateRegionModeVisibility(singlePixel);
        if (singlePixel)
        {
            RegionXBox.Text = region.TopLeft.X.ToString();
            RegionYBox.Text = region.TopLeft.Y.ToString();
        }
        else
        {
            RegionLeftBox.Text = region.TopLeft.X.ToString();
            RegionTopBox.Text = region.TopLeft.Y.ToString();
            RegionRightBox.Text = region.BottomRight.X.ToString();
            RegionBottomBox.Text = region.BottomRight.Y.ToString();
        }
    }

    private void UpdateRegionFromEditor()
    {
        if (isReadOnly) return;
        if (loadingEditor || !TryGetSelectedCondition(out var editIndex, out var condition))
        {
            return;
        }

        var region = BuildRegionFromEditor();
        var updatedMatcher = condition.Condition switch
        {
            PixelMatcher pixel => (IConditionMatcher)(pixel with { Region = region }),
            TextMatcher text => text with { Region = region },
            TemplateMatcher template => template with { Region = region },
            PixelHashMatcher hash => hash with { Region = region },
            _ => new PixelMatcher(region, new RgbColor(255, 0, 0), 10)
        };

        if (updatedMatcher.Equals(condition.Condition))
        {
            return;
        }

        conditions[editIndex] = condition with { Condition = updatedMatcher };
        RegionInfoText.Text = $"({FormatRegionSummary(region)})";
        RefreshList();
        selectedIndex = editIndex;
        ConditionList.SelectedIndex = editIndex;
        ConditionsModified?.Invoke(this, EventArgs.Empty);
        ConditionSelectionChanged?.Invoke(this,
            new ConditionSelectionChangedEventArgs(editIndex, conditions[editIndex]));
    }

    private ScreenRegion BuildRegionFromEditor()
    {
        if (SinglePixelModeCheckBox.IsChecked == true)
        {
            return ScreenRegion.FromSinglePixel(
                ReadInt(RegionXBox.Text, 0),
                ReadInt(RegionYBox.Text, 0));
        }

        var left = ReadInt(RegionLeftBox.Text, 0);
        var top = ReadInt(RegionTopBox.Text, 0);
        var right = ReadInt(RegionRightBox.Text, left);
        var bottom = ReadInt(RegionBottomBox.Text, top);
        if (right < left)
        {
            (left, right) = (right, left);
        }

        if (bottom < top)
        {
            (top, bottom) = (bottom, top);
        }

        if (right - left < 1 && bottom - top < 1)
        {
            return ScreenRegion.FromSinglePixel(left, top);
        }

        return ScreenRegion.FromRect(left, top, right, bottom);
    }

    private static bool IsSinglePixelRegion(ScreenRegion region)
        => region.Width <= 1 && region.Height <= 1;

    private static string FormatRegionSummary(ScreenRegion region)
        => IsSinglePixelRegion(region)
            ? $"{region.TopLeft.X},{region.TopLeft.Y}"
            : $"{region.TopLeft.X},{region.TopLeft.Y} ~ {region.BottomRight.X},{region.BottomRight.Y}";

    private async void PickConditionColor_Click(object sender, RoutedEventArgs e)
    {
        if (isReadOnly) return;
        if (!TryGetSelectedCondition(out _, out var directive)) return;
        if (directive.Condition is not PixelMatcher)
        {
            return;
        }

        var owner = Window.GetWindow(this);
        var result = await ScreenPixelSampler.PickScreenPixelAsync(owner);
        if (!result.Ok)
        {
            StatusMessageRequested?.Invoke(LocalizationService.Get("ColorPickFailedHint"));
            return;
        }

        ApplyPickedConditionColor(result.X, result.Y, result.Color);
        StatusMessageRequested?.Invoke(
            LocalizationService.Format("ColorPickedStatus", result.Color.R, result.Color.G, result.Color.B));
    }

    private void ApplyPickedConditionColor(int x, int y, RgbColor color)
    {
        if (isReadOnly) return;
        if (!TryGetSelectedCondition(out var editIndex, out var condition)) return;
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
        selectedIndex = editIndex;
        ConditionList.SelectedIndex = editIndex;
        LoadEditor(conditions[editIndex]);
        ConditionsModified?.Invoke(this, EventArgs.Empty);
        ConditionSelectionChanged?.Invoke(this,
            new ConditionSelectionChangedEventArgs(editIndex, conditions[editIndex]));
    }

    private void ColorRBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateSelectedMatcherFromEditor();
    }

    private void ExpectedTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateRegexValidity();
        UpdateSelectedMatcherFromEditor();
    }

    private void ContainsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        UpdateSelectedMatcherFromEditor();
    }

    private void RegexCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        UpdateRegexValidity();
        UpdateSelectedMatcherFromEditor();
    }

    private void UpdateRegexValidity()
    {
        var useRegex = RegexCheckBox.IsChecked == true;
        string? error = null;
        var valid = !useRegex || PaddleOcrBridge.IsValidRegex(ExpectedTextBox.Text, out error);
        RegexErrorText.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
        RegexErrorText.Text = valid ? "正则表达式无效" : $"正则表达式无效：{error}";
        ExpectedTextBox.BorderBrush = valid ? null : Brushes.Red;
        ExpectedTextBox.ToolTip = valid ? null : error;
        ContainsCheckBox.IsEnabled = !isReadOnly && !useRegex;
    }

    private void UpdateSelectedMatcherFromEditor()
    {
        if (isReadOnly) return;
        if (loadingEditor || !TryGetSelectedCondition(out var editIndex, out var condition))
        {
            return;
        }

        var updatedMatcher = condition.Condition switch
        {
            PixelMatcher pixel => pixel with
            {
                Expected = new RgbColor(
                    ReadByte(ColorRBox.Text, pixel.Expected.R),
                    ReadByte(ColorGBox.Text, pixel.Expected.G),
                    ReadByte(ColorBBox.Text, pixel.Expected.B)),
                Tolerance = ReadByte(ToleranceBox.Text, pixel.Tolerance)
            },
            TextMatcher text => text with
            {
                ExpectedText = ExpectedTextBox.Text ?? string.Empty,
                Contains = ContainsCheckBox.IsChecked != false,
                UseRegex = RegexCheckBox.IsChecked == true
            },
            _ => condition.Condition
        };

        if (ReferenceEquals(updatedMatcher, condition.Condition)
            || updatedMatcher.Equals(condition.Condition))
        {
            return;
        }

        conditions[editIndex] = condition with { Condition = updatedMatcher };
        if (updatedMatcher is PixelMatcher updatedPixel)
        {
            ColorPreview.Background = new SolidColorBrush(Color.FromRgb(
                updatedPixel.Expected.R,
                updatedPixel.Expected.G,
                updatedPixel.Expected.B));
        }

        RefreshList();
        selectedIndex = editIndex;
        ConditionList.SelectedIndex = editIndex;
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
        if (isReadOnly || !IsThenActionSequenceActive)
        {
            return false;
        }

        ThenActionSequence.InsertSteps(MacroActionTemplateFactory.CreateSteps(kind));
        return true;
    }

    private void AddThenAction_Click(object sender, RoutedEventArgs e)
    {
        if (isReadOnly) return;
        if (!TryGetSelectedCondition(out _, out _))
        {
            StatusMessageRequested?.Invoke(LocalizationService.Get("SelectConditionBeforeThenAction"));
            return;
        }

        thenActionSequenceActive = true;
        ThenActionPalettePopup.IsOpen = true;
        ScrollThenActionsIntoView();
    }

    private void RecordThenActions_Click(object sender, RoutedEventArgs e)
    {
        if (isRecording)
        {
            RecordingStopRequested?.Invoke();
        }
        else if (CanRecordThenActions)
        {
            RecordingModeMenu.Show(RecordThenActionsButton, mode => RecordingStartRequested?.Invoke(mode));
        }
    }

    private void UpdateRecordingButtonState()
    {
        RecordThenActionsButton.IsEnabled = isRecording || CanRecordThenActions;
    }

    private void OnThenActionPaletteClicked(MacroActionTemplateKind kind)
    {
        if (isReadOnly) return;
        if (!TryGetSelectedCondition(out _, out _))
        {
            ThenActionPalettePopup.IsOpen = false;
            return;
        }

        thenActionSequenceActive = true;
        ThenActionSequence.InsertSteps(MacroActionTemplateFactory.CreateSteps(kind));
        ThenActionPalettePopup.IsOpen = false;
        UpdateThenActionEmptyHint();
    }

    private void ThenActionTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (isReadOnly) return;
        if (sender is not MenuItem item
            || item.Tag is not string tag
            || !Enum.TryParse<MacroActionTemplateKind>(tag, ignoreCase: true, out var kind))
        {
            return;
        }

        thenActionSequenceActive = true;
        ThenActionSequence.InsertSteps(MacroActionTemplateFactory.CreateSteps(kind));
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

        if (isReadOnly)
        {
            if ((modifiers & ModifierKeys.Control) != 0
                && (modifiers & (ModifierKeys.Alt | ModifierKeys.Shift)) == 0)
            {
                return key switch
                {
                    Key.A => SelectAllConditions(),
                    Key.C => CopySelectedConditionsToClipboard(),
                    Key.X or Key.V => true,
                    _ => false
                };
            }

            return key == Key.Delete && modifiers == ModifierKeys.None;
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

    private void OnOcrRecognitionCompleted(object? sender, OcrRecognitionResult result)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(() => UpdateOcrStatus(result));
            return;
        }

        UpdateOcrStatus(result);
    }

    private void UpdateOcrStatus(OcrRecognitionResult result)
    {
        var recognizedText = OcrDisplayText.Format(result);
        var displayText = string.IsNullOrWhiteSpace(recognizedText) ? "空" : recognizedText;
        OcrStatusText.Text = result.Success
            ? $"OCR {result.BackendName}: recognizedText={displayText}"
            : $"OCR {result.BackendName}: {result.Error ?? "recognition empty"}; recognizedText={displayText}";
    }

    private void OnThenActionSequenceStepsChanged()
    {
        if (isReadOnly || loadingEditor || !TryGetSelectedCondition(out var editIndex, out var condition))
        {
            return;
        }

        conditions[editIndex] = condition with { ThenSteps = ThenActionSequence.Steps.ToList() };
        // Avoid RefreshList/SelectedIndex here: rebuilding the condition list reloads the
        // then-action editor and clears multi-step insert selection mid-edit.
        UpdateThenActionEmptyHint();
        ConditionsModified?.Invoke(this, EventArgs.Empty);
        ConditionSelectionChanged?.Invoke(this,
            new ConditionSelectionChangedEventArgs(editIndex, conditions[editIndex]));
    }

    private void UpdateThenActionEmptyHint()
    {
        var showEmpty = EditorBorder.Visibility == Visibility.Visible
            && ThenActionSequence.Steps.Count == 0;
        ThenActionEmptyHintText.Visibility = showEmpty ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ScrollThenActionsIntoView()
    {
        if (ThenActionsSection is null || ConditionEditorScrollViewer is null)
        {
            return;
        }

        ThenActionsSection.BringIntoView();
        ConditionEditorScrollViewer.UpdateLayout();
        var offset = ThenActionsSection.TranslatePoint(new Point(0, 0), ConditionEditorScrollViewer).Y
            + ConditionEditorScrollViewer.VerticalOffset
            - 8;
        if (offset < 0)
        {
            offset = 0;
        }

        ConditionEditorScrollViewer.ScrollToVerticalOffset(offset);
    }

    private void OnThenActionTemplateDropped(MacroActionTemplateKind kind, string parentPathText, int insertIndex)
    {
        if (isReadOnly) return;
        ThenActionSequence.InsertStepsAtPath(MacroActionTemplateFactory.CreateSteps(kind), parentPathText, insertIndex);
    }

    private void OnThenMacroLibraryDropped(string macroId, string parentPathText, int insertIndex)
    {
        if (isReadOnly || editorState is null)
        {
            return;
        }

        try
        {
            var item = editorState.LibraryStore.TryGetItem(macroId);
            var document = editorState.LibraryStore.ReadMacro(macroId);
            if (item?.IsConditionMacro == true || document.IsConditionMacro)
            {
                StatusMessageRequested?.Invoke(LocalizationService.Get("ConditionPackNotForThenActions"));
                return;
            }

            ThenActionSequence.InsertStepsAtPath(
                [new MacroCallStep(document.Name)],
                parentPathText,
                insertIndex);
        }
        catch
        {
        }
    }

    public void SetRegion(ScreenRegion region)
    {
        if (isReadOnly) return;
        if (!TryGetSelectedCondition(out var editIndex, out var c)) return;
        var newMatcher = c.Condition switch
        {
            PixelMatcher pm => (IConditionMatcher)(pm with { Region = region }),
            TextMatcher txm => txm with { Region = region },
            _ => new PixelMatcher(region, new RgbColor(255, 0, 0), 10)
        };
        conditions[editIndex] = c with { Condition = newMatcher };
        RefreshList();
        selectedIndex = editIndex;
        ConditionList.SelectedIndex = editIndex;
        LoadEditor(conditions[editIndex]);
        ConditionsModified?.Invoke(this, EventArgs.Empty);
        ConditionSelectionChanged?.Invoke(this,
            new ConditionSelectionChangedEventArgs(editIndex, conditions[editIndex]));
    }

    private bool TryGetSelectedCondition(out int index, out ConditionalDirective directive)
    {
        index = selectedIndex;
        if (index < 0 || index >= conditions.Count)
        {
            index = ConditionList.SelectedIndex;
        }

        if (index < 0 || index >= conditions.Count)
        {
            directive = default!;
            return false;
        }

        selectedIndex = index;
        directive = conditions[index];
        return true;
    }

    private int ClampConditionIndex(int index)
    {
        if (conditions.Count == 0 || index < 0)
        {
            return -1;
        }

        return Math.Clamp(index, 0, conditions.Count - 1);
    }

    private static IConditionMatcher CreateMatcherForSelectedType(string type, IConditionMatcher current)
    {
        var region = GetMatcherRegion(current);
        return type switch
        {
            "text" => current is TextMatcher text
                ? text
                : new TextMatcher(region, string.Empty, Contains: true),
            _ => current is PixelMatcher pixel
                ? pixel
                : new PixelMatcher(region, new RgbColor(255, 0, 0), 10)
        };
    }

    private static ScreenRegion GetMatcherRegion(IConditionMatcher matcher)
    {
        return matcher switch
        {
            PixelMatcher pixel => pixel.Region,
            TemplateMatcher template => template.Region,
            PixelHashMatcher hash => hash.Region,
            TextMatcher text => text.Region,
            _ => ScreenRegion.FromSinglePixel(0, 0)
        };
    }

    private string DescribeRange(ConditionalDirective directive)
    {
        if (directive.ExecutionMode == ConditionExecutionMode.GateMainSequence)
        {
            return LocalizationService.Get("ConditionGateRangeHint");
        }

        var baseLabel = directive.TimeBase switch
        {
            ConditionTimeBase.AfterPreviousCondition => LocalizationService.Get("ConditionTimeBaseAfterPreviousShort"),
            ConditionTimeBase.MainIteration => LocalizationService.Get("ConditionTimeBaseMainIterationShort"),
            _ => LocalizationService.Get("ConditionTimeBasePlaybackShort")
        };
        string? timePart = null;
        if (directive.HasTimeRange)
        {
            var start = directive.WindowStart is { } windowStart ? FormatMs(windowStart) : "0";
            var end = directive.WindowEnd is { } windowEnd ? FormatMs(windowEnd) : LocalizationService.Get("ConditionTimeRangeOpenEnd");
            timePart = $"{baseLabel} {start}~{end} ms";
        }

        if (!directive.HasStepRange)
        {
            return timePart ?? LocalizationService.Get("ConditionStepRangeUnlimited");
        }

        var startLabel = FindStepChoiceLabel(directive.StartStepPathText, directive.StartStepIndex);
        var endLabel = FindStepChoiceLabel(directive.EndStepPathText, directive.EndStepIndex);
        var range = $"{startLabel} ~ {endLabel}";
        if (timePart is null)
        {
            return range;
        }

        return $"{range} {LocalizationService.Get("ConditionRangeOr")} {timePart}";
    }

    private string FindStepChoiceLabel(string pathText, int stepIndex)
    {
        if (stepIndex < 0)
        {
            return LocalizationService.Get("ConditionStepRangeUnlimited");
        }

        var match = stepChoices.FirstOrDefault(choice =>
            (!string.IsNullOrWhiteSpace(pathText)
                && string.Equals(choice.PathText, pathText, StringComparison.Ordinal))
            || choice.Index == stepIndex);
        return match?.Label ?? $"步骤 #{stepIndex + 1}";
    }

    private static void SelectStepChoice(ComboBox comboBox, string pathText, int stepIndex)
    {
        if (stepIndex < 0)
        {
            var unlimited = comboBox.Items.OfType<StepChoice>().FirstOrDefault(choice => choice.Index < 0);
            if (unlimited is not null)
            {
                comboBox.SelectedItem = unlimited;
                return;
            }
        }

        foreach (var choice in comboBox.Items.OfType<StepChoice>())
        {
            if (choice.Index < 0)
            {
                continue;
            }

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

    private static byte ReadByte(string value, byte fallback)
    {
        return byte.TryParse(value.Trim(), out var parsed)
            ? parsed
            : fallback;
    }

    private static int ReadInt(string value, int fallback)
        => int.TryParse(value.Trim(), out var parsed) ? parsed : fallback;

    private void ThenActionsResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var next = ThenActionSequenceHost.Height + e.VerticalChange;
        if (double.IsNaN(ThenActionSequenceHost.Height) || ThenActionSequenceHost.Height <= 0)
        {
            next = ThenActionSequenceHost.ActualHeight + e.VerticalChange;
        }

        ThenActionSequenceHost.Height = Math.Clamp(next, 160, 900);
    }

    private void BeginConditionListDragInteraction()
    {
        conditionListDragInProgress = true;
        StartConditionDragWheelHook();
    }

    private void EndConditionListDragInteraction()
    {
        conditionListDragInProgress = false;
        StopConditionDragWheelHook();
    }

    private void StartConditionDragWheelHook()
    {
        if (conditionDragMouseHookHandle != IntPtr.Zero)
        {
            return;
        }

        conditionDragMouseHookHandle = SetWindowsHookEx(WH_MOUSE_LL, conditionDragMouseHookProc, IntPtr.Zero, 0);
    }

    private void StopConditionDragWheelHook()
    {
        if (conditionDragMouseHookHandle == IntPtr.Zero)
        {
            return;
        }

        UnhookWindowsHookEx(conditionDragMouseHookHandle);
        conditionDragMouseHookHandle = IntPtr.Zero;
    }

    private IntPtr ConditionDragMouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0
            && wParam.ToInt32() == WM_MOUSEWHEEL_LOW_LEVEL
            && conditionListDragInProgress
            && conditionDragMouseHookHandle != IntPtr.Zero)
        {
            var data = Marshal.PtrToStructure<MouseLowLevelHookStruct>(lParam);
            if (IsScreenPointInsideConditionList(data.Point.X, data.Point.Y))
            {
                var delta = unchecked((short)((data.MouseData >> 16) & 0xFFFF));
                Dispatcher.BeginInvoke(
                    new Action(() => ScrollConditionListByWheelDelta(delta)),
                    DispatcherPriority.Input);
                return new IntPtr(1);
            }
        }

        return CallNextHookEx(conditionDragMouseHookHandle, nCode, wParam, lParam);
    }

    private bool IsScreenPointInsideConditionList(double screenX, double screenY)
    {
        if (!ConditionList.IsLoaded || !ConditionList.IsVisible)
        {
            return false;
        }

        try
        {
            var local = ConditionList.PointFromScreen(new Point(screenX, screenY));
            return local.X >= 0
                && local.Y >= 0
                && local.X <= ConditionList.ActualWidth
                && local.Y <= ConditionList.ActualHeight;
        }
        catch
        {
            return false;
        }
    }

    private void ScrollConditionListByWheelDelta(int delta)
    {
        var scrollViewer = GetConditionListScrollViewer();
        if (scrollViewer is null)
        {
            return;
        }

        var lines = SystemParameters.WheelScrollLines <= 0 ? 3 : SystemParameters.WheelScrollLines;
        var offsetDelta = -(delta / WheelDelta) * lines;
        var target = Math.Clamp(scrollViewer.VerticalOffset + offsetDelta, 0, scrollViewer.ScrollableHeight);
        scrollViewer.ScrollToVerticalOffset(target);
    }

    private bool AutoScrollConditionList(Point point)
    {
        var scrollViewer = GetConditionListScrollViewer();
        if (scrollViewer is null || scrollViewer.ScrollableHeight <= 0)
        {
            return false;
        }

        const double maxDelta = 28;
        double delta = 0;
        if (point.Y < AutoScrollEdgeSize)
        {
            delta = -ScaleAutoScrollDelta(AutoScrollEdgeSize - point.Y, AutoScrollEdgeSize, maxDelta);
        }
        else if (point.Y > ConditionList.ActualHeight - AutoScrollEdgeSize)
        {
            delta = ScaleAutoScrollDelta(point.Y - (ConditionList.ActualHeight - AutoScrollEdgeSize), AutoScrollEdgeSize, maxDelta);
        }

        if (Math.Abs(delta) < 0.5)
        {
            return false;
        }

        scrollViewer.ScrollToVerticalOffset(
            Math.Clamp(scrollViewer.VerticalOffset + delta, 0, scrollViewer.ScrollableHeight));
        return true;
    }

    private ScrollViewer? GetConditionListScrollViewer()
    {
        return conditionListScrollViewer ??= FindVisualChild<ScrollViewer>(ConditionList);
    }

    private static double ScaleAutoScrollDelta(double distanceIntoEdge, double edgeSize, double maxDelta)
    {
        var ratio = Math.Clamp(distanceIntoEdge / edgeSize, 0.15, 1.0);
        return maxDelta * ratio;
    }

    private static T? FindVisualChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                return match;
            }

            var nested = FindVisualChild<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
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

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseLowLevelHookStruct
    {
        public NativePoint Point;
        public int MouseData;
        public int Flags;
        public int Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int idHook,
        LowLevelMouseProc lpfn,
        IntPtr hmod,
        uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(
        IntPtr hhk,
        int nCode,
        IntPtr wParam,
        IntPtr lParam);
}

public sealed class ConditionDisplayItem : System.ComponentModel.INotifyPropertyChanged
{
    private string name = "";

    public string Name
    {
        get => name;
        set
        {
            if (string.Equals(name, value, StringComparison.Ordinal))
            {
                return;
            }

            name = value ?? "";
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Name)));
        }
    }

    public string Description { get; set; } = "";
    public string RangeBadge { get; set; } = "";
    public string TypeIcon { get; set; } = "";
    public Brush ColorBrush { get; set; } = Brushes.Gray;
    public Brush StatusBrush { get; set; } = Brushes.Gray;
    public string StatusText { get; set; } = "";

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
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
