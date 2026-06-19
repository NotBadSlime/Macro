using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MacroHid.Core;
using MacroStudio.Services;
using CoreMouseButton = MacroHid.Core.MouseButton;

namespace MacroStudio.Controls;

public partial class StepSequencePanel : UserControl
{
    private const string ActionTemplateDragFormat = "MacroHID.ActionTemplate";
    private const string MacroLibraryDragFormat = "MacroHID.MacroLibraryItem";
    private const string StepDragFormat = "MacroHID.StepPath";
    private const string StepDragPathSeparator = "|";
    private const string StepClipboardPrefix = "MacroHID.SequenceSteps.v1";

    private readonly DispatcherTimer boxSelectionAutoScrollTimer = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private readonly List<ListBoxItem> fadedStepDragContainers = [];
    private readonly HashSet<string> boxSelectionBasePaths = [];
    private List<ConditionRangeHighlight> conditionRanges = [];
    private IReadOnlyList<MacroStep> steps = [];
    private IReadOnlyList<string> dragSelectionSnapshot = [];
    private MacroEditorState? state;
    private Func<string, string>? macroNameResolver;
    private Point stepDragStartPoint;
    private DateTime stepDragStartTime;
    private string stepDragStartPath = string.Empty;
    private bool stepDragStarted;
    private ListBoxItem? stepDragSourceContainer;
    private DragGhostAdorner? stepDragGhost;
    private AdornerLayer? stepDragGhostLayer;
    private string inlineEditorPath = string.Empty;
    private string inlineKeyCapturePath = string.Empty;
    private bool inlineCoordinatePickerActive;
    private bool boxSelectionActive;
    private Point boxSelectionStartPoint;
    private Point boxSelectionLastPoint;
    private ScrollViewer? stepScrollViewer;

    public StepSequencePanel()
    {
        InitializeComponent();
        boxSelectionAutoScrollTimer.Tick += (_, _) =>
        {
            if (boxSelectionActive)
            {
                UpdateBoxSelection(boxSelectionLastPoint);
            }
        };
    }

    public event Action? Activated;
    public event Action? BeforeStepsChanged;
    public event Action? StepsChanged;
    public event Action? UndoRequested;
    public event Action? ClearRequested;
    public event Action<int>? StepSelectionChanged;
    public event Action<MacroActionTemplateKind, string, int>? ActionTemplateDropped;
    public event Action<string, string, int>? MacroLibraryDropped;

    public IReadOnlyList<MacroStep> Steps => steps;

    public int SelectedStepIndex
    {
        get => SelectedStepPath.Count == 1 ? SelectedStepPath[0] : -1;
        set => SelectStepIndex(value);
    }

    public IReadOnlyList<int> SelectedStepPath => StepList.SelectedItem is StepDisplayItem { StepPath.Count: > 0 } s
        ? s.StepPath
        : [];

    public bool IsActiveSequence { get; private set; }

    public void Initialize(MacroEditorState editorState)
    {
        state = editorState;
        InlineStepEditor.Initialize(editorState);
        InlineStepEditor.StepEdited += OnInlineStepEdited;
        InlineStepEditor.CoordinatePickerStarted += OnInlineCoordinatePickerStarted;
        InlineStepEditor.CoordinatePickerFinished += OnInlineCoordinatePickerFinished;
    }

    public void SetMacroNameResolver(Func<string, string>? resolver)
    {
        macroNameResolver = resolver;
        RefreshList(SelectedStepPath);
    }

    public void ApplyLocalization()
    {
        SequenceTitleText.Text = LocalizationService.Get("Sequence");
        StepUndoText.Text = LocalizationService.Get("Undo");
        StepUpText.Text = LocalizationService.Get("MoveUp");
        StepDownText.Text = LocalizationService.Get("MoveDown");
        StepDeleteText.Text = LocalizationService.Get("Delete");
        StepClearText.Text = LocalizationService.Get("ClearAll");
        InlineStepEditor.ApplyLocalization();
    }

    public void SetTitle(string title)
    {
        SequenceTitleText.Text = title;
    }

    public void SetCanUndo(bool canUndo)
    {
        StepUndoButton.IsEnabled = canUndo;
    }

    public void SetSteps(IReadOnlyList<MacroStep> newSteps)
    {
        var selectedPath = SelectedStepPath.ToArray();
        steps = newSteps.ToList();
        RefreshList(selectedPath);
    }

    public IReadOnlyList<StepChoice> GetStepChoices()
    {
        return StepDisplayItem.FlattenSteps(steps, macroNameResolver: ResolveMacroDisplayName)
            .Where(item => item.StepPath.Count > 0 && !IsContainerEnd(item))
            .Select((item, index) => new StepChoice(index, $"#{index + 1} {item.Title}", item.StepPath))
            .ToList();
    }

    public void InsertSteps(IReadOnlyList<MacroStep> stepsToInsert)
    {
        if (stepsToInsert.Count == 0) return;
        InsertStepsAt(stepsToInsert, GetDefaultInsertTarget());
    }

    public void InsertStepsAt(IReadOnlyList<MacroStep> stepsToInsert, int insertIndex)
    {
        InsertStepsAt(stepsToInsert, new StepDropTarget([], insertIndex));
    }

    public void InsertStepsAtPath(IReadOnlyList<MacroStep> stepsToInsert, string parentPathText, int insertIndex)
    {
        InsertStepsAt(stepsToInsert, new StepDropTarget(ParseStepPath(parentPathText), insertIndex));
    }

    private void InsertStepsAt(IReadOnlyList<MacroStep> stepsToInsert, StepDropTarget target)
    {
        var normalizedSteps = MacroStepNormalizer.NormalizeSteps(stepsToInsert);
        if (normalizedSteps.Count == 0) return;
        var insertAt = Math.Clamp(target.InsertIndex, 0, GetChildCount(steps, target.ParentPath));
        var updated = MacroStepTreeEditor.InsertAtPath(steps, target.ParentPath, insertAt, normalizedSteps);
        ApplyStepMutation(updated, Enumerable.Range(insertAt, normalizedSteps.Count)
            .Select(index => (IReadOnlyList<int>)target.ParentPath.Concat([index]).ToArray())
            .ToList());
    }

    public bool HandleExplorerShortcut(Key key, ModifierKeys modifiers)
    {
        if (key == Key.Delete && modifiers == ModifierKeys.None)
        {
            DeleteSelectedStep();
            return true;
        }

        if ((modifiers & ModifierKeys.Control) == 0 || (modifiers & (ModifierKeys.Alt | ModifierKeys.Shift)) != 0)
        {
            return false;
        }

        return key switch
        {
            Key.A => SelectAllSteps(),
            Key.C => CopySelectedStepsToClipboard(),
            Key.X => CutSelectedStepsToClipboard(),
            Key.V => PasteStepsFromClipboard(),
            _ => false
        };
    }

    public bool SelectAllSteps()
    {
        StepList.SelectedItems.Clear();
        foreach (var item in StepList.Items.OfType<StepDisplayItem>().Where(item => item.StepPath.Count > 0))
        {
            StepList.SelectedItems.Add(item);
        }

        return StepList.SelectedItems.Count > 0;
    }

    public bool CopySelectedStepsToClipboard()
    {
        return CopyStepPathsToClipboard(GetSelectedStepPaths());
    }

    public bool CutSelectedStepsToClipboard()
    {
        var selectedPaths = GetSelectedStepPaths();
        if (selectedPaths.Count == 0 || !CopyStepPathsToClipboard(selectedPaths))
        {
            return false;
        }

        DeleteSelectedStep();
        return true;
    }

    public bool PasteStepsFromClipboard()
    {
        if (!TryReadClipboardSteps(out var stepsToPaste) || stepsToPaste.Count == 0)
        {
            return false;
        }

        InsertSteps(stepsToPaste);
        return true;
    }

    public void MoveSelectedStep(int offset)
    {
        MoveSelectedSteps(offset);
    }

    public void MoveSelectedSteps(int offset)
    {
        var selectedPaths = GetSelectedStepPaths();
        if (selectedPaths.Count == 0 || offset == 0 || !AreSameParent(selectedPaths)) return;

        var parentPath = selectedPaths[0].Take(selectedPaths[0].Count - 1).ToArray();
        var childIndexes = selectedPaths.Select(path => path[^1]).Order().ToArray();
        var childCount = GetChildCount(steps, parentPath);
        if (offset < 0 && childIndexes[0] <= 0) return;
        if (offset > 0 && childIndexes[^1] >= childCount - 1) return;

        var insertIndex = offset < 0 ? childIndexes[0] - 1 : childIndexes[^1] + 2;
        MoveStepsToDropTarget(selectedPaths, new StepDropTarget(parentPath, insertIndex));
    }

    public void DeleteSelectedStep()
    {
        var paths = GetSelectedStepPaths();
        if (paths.Count == 0) return;
        if (paths.Count == 1)
        {
            DeleteStepAtPath(paths[0]);
            return;
        }

        var updated = steps;
        foreach (var path in paths.OrderByDescending(path => path, StepPathComparer.Instance))
        {
            updated = MacroStepTreeEditor.DeleteAtPath(updated, path);
        }

        ApplyStepMutation(updated, []);
    }

    public void ClearSteps()
    {
        if (steps.Count == 0) return;
        ApplyStepMutation([], []);
    }

    public void DeleteStepAtIndex(int stepIndex)
    {
        DeleteStepAtPath([stepIndex]);
    }

    public void DeleteStepAtPath(IReadOnlyList<int> stepPath)
    {
        if (stepPath.Count == 0) return;
        _ = MacroStepTreeEditor.GetAtPath(steps, stepPath);
        var updated = MacroStepTreeEditor.DeleteAtPath(steps, stepPath);
        BeforeStepsChanged?.Invoke();
        steps = updated.ToList();
        RefreshList();
        SelectAfterDelete(steps, stepPath);
        StepsChanged?.Invoke();
    }

    public void DuplicateStepAtIndex(int stepIndex)
    {
        DuplicateStepAtPath([stepIndex]);
    }

    public void DuplicateStepAtPath(IReadOnlyList<int> stepPath)
    {
        if (stepPath.Count == 0) return;
        var step = MacroStepTreeEditor.GetAtPath(steps, stepPath);
        var parentPath = stepPath.Take(stepPath.Count - 1).ToArray();
        var insertAt = stepPath[^1] + 1;
        var updated = MacroStepTreeEditor.InsertAtPath(steps, parentPath, insertAt, [step]);
        ApplyStepMutation(updated, [parentPath.Concat([insertAt]).ToArray()]);
    }

    public void ApplyEditedStep(MacroStep newStep)
    {
        if (StepList.SelectedItem is not StepDisplayItem { StepPath.Count: > 0 } selected) return;
        ApplyEditedStepAtPath(selected.StepPath, newStep);
    }

    public void CloseInlineStepEditorOnExternalPointerDown(DependencyObject? source)
    {
        if (!InlineStepEditorPopup.IsOpen || inlineCoordinatePickerActive)
        {
            return;
        }

        if (IsClickInsideInlineEditor(source) || IsClickInsideSequencePanel(source))
        {
            return;
        }

        CloseInlineStepEditorForOutsideClick();
    }

    public void DeactivateSequence()
    {
        IsActiveSequence = false;
    }

    private void ActivateSequence()
    {
        IsActiveSequence = true;
        Activated?.Invoke();
    }

    private void ApplyStepMutation(IReadOnlyList<MacroStep> newSteps, IReadOnlyList<IReadOnlyList<int>> selectPaths)
    {
        BeforeStepsChanged?.Invoke();
        steps = newSteps.ToList();
        RefreshList();
        SelectStepPaths(selectPaths);
        StepsChanged?.Invoke();
    }

    private void RefreshList(IReadOnlyList<int>? selectPath = null)
    {
        StepList.Items.Clear();
        foreach (var item in StepDisplayItem.FlattenSteps(steps, macroNameResolver: ResolveMacroDisplayName))
        {
            StepList.Items.Add(item);
        }

        ApplyConditionBarsToItems();
        if (selectPath is { Count: > 0 })
        {
            SelectStepPath(selectPath);
        }

        RefreshSelectedStepText();
    }

    private string ResolveMacroDisplayName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        if (macroNameResolver is not null)
        {
            return macroNameResolver(value);
        }

        var item = state?.LibrarySnapshot.Items.FirstOrDefault(item => item.MatchesReference(value));
        return item?.Name ?? value;
    }

    private void RefreshSelectedStepText()
    {
        SelectedStepText.Text = StepList.SelectedItem is StepDisplayItem { StepPath.Count: > 0 } selected
            ? selected.Title
            : LocalizationService.Get("DropActionsHint");
    }

    private void StepList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshSelectedStepText();
        StepSelectionChanged?.Invoke(SelectedStepIndex);
    }

    private void StepList_Drop(object sender, DragEventArgs e)
    {
        var target = GetStepDropTargetFromPoint(e.GetPosition(StepList));
        if (e.Data.GetDataPresent(StepDragFormat)
            && e.Data.GetData(StepDragFormat) is string sourcePathText)
        {
            MoveStepsToDropTarget(ParseStepPaths(sourcePathText), target);
            HideDropIndicator();
            e.Handled = true;
            return;
        }

        if (e.Data.GetDataPresent(ActionTemplateDragFormat) && e.Data.GetData(ActionTemplateDragFormat) is string template)
        {
            if (Enum.TryParse<MacroActionTemplateKind>(template, ignoreCase: true, out var kind))
            {
                ActionTemplateDropped?.Invoke(kind, target.ParentPathText, target.InsertIndex);
            }

            HideDropIndicator();
            e.Handled = true;
            return;
        }

        if (e.Data.GetDataPresent(MacroLibraryDragFormat) && e.Data.GetData(MacroLibraryDragFormat) is string macroId)
        {
            MacroLibraryDropped?.Invoke(macroId, target.ParentPathText, target.InsertIndex);
            HideDropIndicator();
            e.Handled = true;
        }
    }

    private void StepList_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(StepDragFormat)
            || e.Data.GetDataPresent(ActionTemplateDragFormat)
            || e.Data.GetDataPresent(MacroLibraryDragFormat))
        {
            var point = e.GetPosition(StepList);
            AutoScrollStepList(point);
            UpdateStepDragGhost(point);
            UpdateDropIndicator(GetStepDropTargetFromPoint(point));
            e.Effects = e.Data.GetDataPresent(StepDragFormat) ? DragDropEffects.Move : DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void StepList_DragLeave(object sender, DragEventArgs e)
    {
        HideDropIndicator();
    }

    private void SequenceRoot_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        ActivateSequence();
        if (!InlineStepEditorPopup.IsOpen || inlineCoordinatePickerActive)
        {
            return;
        }

        if (IsClickInsideInlineEditor(e.OriginalSource as DependencyObject))
        {
            return;
        }

        CloseInlineStepEditorForOutsideClick();
    }

    private void StepList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        ActivateSequence();
        var source = e.OriginalSource as DependencyObject;
        stepDragStartPoint = e.GetPosition(StepList);
        stepDragStartTime = DateTime.UtcNow;
        stepDragStarted = false;
        stepDragSourceContainer = FindVisualParent<ListBoxItem>(source);
        stepDragStartPath = FindVisualParent<Button>(source) is null
            ? GetStepPathFromSource(source)
            : string.Empty;
        dragSelectionSnapshot = string.IsNullOrEmpty(stepDragStartPath)
            ? []
            : GetSelectedStepPathTextsForDrag(stepDragStartPath);

        if (ShouldStartStepBoxSelection(source))
        {
            dragSelectionSnapshot = [];
            BeginBoxSelection(stepDragStartPoint, (Keyboard.Modifiers & ModifierKeys.Control) != 0);
            e.Handled = true;
            return;
        }

        if (dragSelectionSnapshot.Count > 1
            && IsSelectedStepPath(stepDragStartPath)
            && !IsMultiSelectionGesture())
        {
            e.Handled = true;
        }
    }

    private void StepList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (boxSelectionActive)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                FinishBoxSelection();
                return;
            }

            UpdateBoxSelection(e.GetPosition(StepList));
            e.Handled = true;
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed || string.IsNullOrEmpty(stepDragStartPath) || stepDragStarted)
        {
            return;
        }

        var current = e.GetPosition(StepList);
        var moved = Math.Abs(current.X - stepDragStartPoint.X) > SystemParameters.MinimumHorizontalDragDistance
            || Math.Abs(current.Y - stepDragStartPoint.Y) > SystemParameters.MinimumVerticalDragDistance;
        var heldLongEnough = (DateTime.UtcNow - stepDragStartTime).TotalMilliseconds >= 250;
        if (!moved || !heldLongEnough)
        {
            return;
        }

        var dragPathTexts = dragSelectionSnapshot.Count > 0
            ? dragSelectionSnapshot
            : GetSelectedStepPathTextsForDrag(stepDragStartPath);
        if (dragPathTexts.Count == 0)
        {
            return;
        }

        stepDragStarted = true;
        ShowStepDragGhost(current, dragPathTexts.Count);
        FadeStepDragSources(dragPathTexts);
        try
        {
            DragDrop.DoDragDrop(StepList, new DataObject(StepDragFormat, string.Join(StepDragPathSeparator, dragPathTexts)), DragDropEffects.Move);
        }
        finally
        {
            RestoreStepDragSources();
            RemoveStepDragGhost();
            HideDropIndicator();
            ResetStepDragState();
        }
        e.Handled = true;
    }

    private void StepList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!boxSelectionActive)
        {
            ResetStepDragState();
            return;
        }

        UpdateBoxSelection(e.GetPosition(StepList));
        FinishBoxSelection();
        e.Handled = true;
    }

    private void StepList_LostMouseCapture(object sender, MouseEventArgs e)
    {
        FinishBoxSelection();
    }

    private void ResetStepDragState()
    {
        stepDragStarted = false;
        stepDragStartPath = string.Empty;
        stepDragSourceContainer = null;
        dragSelectionSnapshot = [];
    }

    private void StepRow_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (FindVisualParent<Button>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        if (stepDragStarted || IsMultiSelectionGesture() || sender is not FrameworkElement { DataContext: StepDisplayItem item })
        {
            return;
        }

        BeginStepVisualEdit(item, sender as UIElement);
        e.Handled = true;
    }

    private void StepTitle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (stepDragStarted || IsMultiSelectionGesture() || sender is not FrameworkElement { DataContext: StepDisplayItem item })
        {
            return;
        }

        BeginStepVisualEdit(item, sender as UIElement);
        e.Handled = true;
    }

    private void BeginStepVisualEdit(StepDisplayItem item, UIElement? placementTarget)
    {
        if (item.StepPath.Count == 0 || StepList.SelectedItems.Count > 1)
        {
            return;
        }

        var current = MacroStepTreeEditor.GetAtPath(steps, item.StepPath);
        SelectStepPath(item.StepPath);
        OpenInlineStepEditor(item.StepPath, current, placementTarget);
    }

    private void OpenInlineStepEditor(IReadOnlyList<int> stepPath, MacroStep current, UIElement? placementTarget)
    {
        inlineCoordinatePickerActive = false;
        inlineEditorPath = ToPathText(stepPath);
        InlineStepEditor.RefreshMacroTargetBox(current is MacroCallStep macro ? macro.Macro : null);
        InlineStepEditor.ShowStep(current);
        InlineStepEditorPopup.PlacementTarget = placementTarget ?? StepList;
        InlineStepEditorPopup.IsOpen = true;
    }

    private void OnInlineStepEdited(MacroStep _)
    {
        if (string.IsNullOrEmpty(inlineEditorPath))
        {
            return;
        }

        try
        {
            var stepPath = ParseStepPath(inlineEditorPath);
            var current = MacroStepTreeEditor.GetAtPath(steps, stepPath);
            var edited = InlineStepEditor.BuildEditedStep(current);
            ApplyEditedStepAtPath(stepPath, edited);
            if (SelectedStepPath.Count > 0)
            {
                inlineEditorPath = ToPathText(SelectedStepPath);
                InlineStepEditor.ShowStep(MacroStepTreeEditor.GetAtPath(steps, SelectedStepPath));
            }

            CloseInlineStepEditorAfterApply();
        }
        catch (Exception ex)
        {
            SelectedStepText.Text = ex.Message;
        }
    }

    private void ApplyEditedStepAtPath(IReadOnlyList<int> stepPath, MacroStep newStep)
    {
        if (stepPath.Count == 0) return;
        var current = MacroStepTreeEditor.GetAtPath(steps, stepPath);
        var replacement = MacroStepNormalizer.NormalizeSteps([newStep]);
        if (replacement.Count == 0) return;
        if (replacement.Count == 1 && EqualityComparer<MacroStep>.Default.Equals(current, replacement[0])) return;

        var parentPath = stepPath.Take(stepPath.Count - 1).ToArray();
        var insertAt = stepPath[^1];
        var updated = replacement.Count == 1
            ? MacroStepTreeEditor.ReplaceAtPathWithLinkedPressRelease(steps, stepPath, replacement[0])
            : MacroStepTreeEditor.InsertAtPath(MacroStepTreeEditor.DeleteAtPath(steps, stepPath), parentPath, insertAt, replacement);
        ApplyStepMutation(updated, [parentPath.Concat([insertAt]).ToArray()]);
    }

    private void CloseInlineStepEditorAfterApply()
    {
        InlineStepEditorPopup.IsOpen = false;
        ResetStepDragState();
        StepList.Focus();
    }

    private void CloseInlineStepEditorForOutsideClick()
    {
        InlineStepEditorPopup.IsOpen = false;
        ResetStepDragState();
    }

    private void InlineStepEditorPopup_Closed(object sender, EventArgs e)
    {
        inlineEditorPath = string.Empty;
        inlineCoordinatePickerActive = false;
        ResetStepDragState();
    }

    private bool IsClickInsideInlineEditor(DependencyObject? source)
    {
        for (var current = source; current is not null; current = GetTreeParent(current))
        {
            if (ReferenceEquals(current, InlineStepEditor)
                || ReferenceEquals(current, InlineStepEditorPopup.Child))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsClickInsideSequencePanel(DependencyObject? source)
    {
        for (var current = source; current is not null; current = GetTreeParent(current))
        {
            if (ReferenceEquals(current, this)
                || ReferenceEquals(current, StepSequenceRoot))
            {
                return true;
            }
        }

        return false;
    }

    private static DependencyObject? GetTreeParent(DependencyObject current)
    {
        try
        {
            var visualParent = VisualTreeHelper.GetParent(current);
            if (visualParent is not null)
            {
                return visualParent;
            }
        }
        catch (InvalidOperationException)
        {
        }

        return LogicalTreeHelper.GetParent(current);
    }

    private void OnInlineCoordinatePickerStarted(object? sender, EventArgs e)
    {
        inlineCoordinatePickerActive = true;
        InlineStepEditorPopup.IsOpen = true;
    }

    private void OnInlineCoordinatePickerFinished(object? sender, EventArgs e)
    {
        inlineCoordinatePickerActive = false;
        InlineStepEditorPopup.IsOpen = true;
    }

    private void BeginInlineKeyCapture(IReadOnlyList<int> stepPath)
    {
        inlineKeyCapturePath = ToPathText(stepPath);
        SelectedStepText.Text = "请按下新的键位";
        AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(InlineKeyCapture_KeyDown), true);
    }

    private void InlineKeyCapture_KeyDown(object sender, KeyEventArgs e)
    {
        if (string.IsNullOrEmpty(inlineKeyCapturePath))
        {
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (IsModifierKey(key))
        {
            return;
        }

        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (GlobalKeyboardHook.TryMapVirtualKeyToHidKey(virtualKey, out var hidKey))
        {
            var stepPath = ParseStepPath(inlineKeyCapturePath);
            if (stepPath.Count > 0
                && MacroStepTreeEditor.GetAtPath(steps, stepPath) is KeyStep current)
            {
                ApplyEditedStepAtPath(stepPath, current with
                {
                    Key = hidKey,
                    Modifiers = ReadCurrentModifiers()
                });
            }
        }

        StopInlineKeyCapture();
        e.Handled = true;
    }

    private void StopInlineKeyCapture()
    {
        if (string.IsNullOrEmpty(inlineKeyCapturePath))
        {
            return;
        }

        inlineKeyCapturePath = string.Empty;
        RemoveHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(InlineKeyCapture_KeyDown));
    }

    private void MoveStepsToDropTarget(IReadOnlyList<IReadOnlyList<int>> sourcePaths, StepDropTarget target)
    {
        var normalizedSources = NormalizeSourcePaths(sourcePaths);
        if (normalizedSources.Count == 0)
        {
            return;
        }

        try
        {
            foreach (var sourcePath in normalizedSources)
            {
                _ = MacroStepTreeEditor.GetAtPath(steps, sourcePath);
                if (PathStartsWith(target.ParentPath, sourcePath))
                {
                    return;
                }
            }

            if (normalizedSources.Count == 1)
            {
                var sourcePath = normalizedSources[0];
                var sourceParentPath = sourcePath.Take(sourcePath.Count - 1).ToArray();
                if (PathsEqual(sourceParentPath, target.ParentPath)
                    && (target.InsertIndex == sourcePath[^1] || target.InsertIndex == sourcePath[^1] + 1))
                {
                    return;
                }
            }

            _ = GetChildCount(steps, target.ParentPath);
            var updated = MacroStepTreeEditor.MoveManyAtPath(steps, normalizedSources, target.ParentPath, target.InsertIndex);
            var movedSelectionPaths = GetMovedSelectionPaths(normalizedSources, target, updated);
            ApplyStepMutation(updated, movedSelectionPaths);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or ArgumentOutOfRangeException)
        {
        }
    }

    private StepDropTarget GetStepDropTargetFromPoint(Point point)
    {
        for (var i = 0; i < StepList.Items.Count; i++)
        {
            if (StepList.Items[i] is not StepDisplayItem { StepPath.Count: > 0 } item
                || StepList.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem container)
            {
                continue;
            }

            var midPoint = container.TranslatePoint(new Point(0, container.ActualHeight / 2), StepList);
            var bottomPoint = container.TranslatePoint(new Point(0, container.ActualHeight), StepList);
            if (point.Y > bottomPoint.Y)
            {
                continue;
            }

            if (IsContainerStart(item))
            {
                return point.Y < midPoint.Y
                    ? CreateTargetBeforeStep(item)
                    : CreateTargetInsideStep(item, append: false);
            }

            if (IsContainerEnd(item))
            {
                return point.Y < midPoint.Y
                    ? CreateTargetInsideStep(item, append: true)
                    : CreateTargetAfterStep(item);
            }

            return point.Y < midPoint.Y ? CreateTargetBeforeStep(item) : CreateTargetAfterStep(item);
        }

        return new StepDropTarget([], steps.Count);
    }

    private IReadOnlyList<string> GetSelectedStepPathTextsForDrag(string clickedPathText)
    {
        if (string.IsNullOrEmpty(clickedPathText))
        {
            return [];
        }

        var selectedTexts = StepList.SelectedItems
            .OfType<StepDisplayItem>()
            .Where(item => item.StepPath.Count > 0)
            .Select(item => item.StepPathText)
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);

        if (!selectedTexts.Contains(clickedPathText))
        {
            return [clickedPathText];
        }

        var ordered = StepList.Items
            .OfType<StepDisplayItem>()
            .Where(item => selectedTexts.Contains(item.StepPathText))
            .Select(item => item.StepPathText)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var selectedPaths = ordered.Select(ParseStepPath).ToList();
        var result = new List<string>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var isDescendantOfAnotherSelection = selectedPaths
                .Where((_, j) => i != j)
                .Any(other => PathStartsWith(selectedPaths[i], other));
            if (!isDescendantOfAnotherSelection)
            {
                result.Add(ordered[i]);
            }
        }

        return result;
    }

    private IReadOnlyList<IReadOnlyList<int>> GetSelectedStepPaths()
    {
        var paths = StepList.SelectedItems
            .OfType<StepDisplayItem>()
            .Where(item => item.StepPath.Count > 0)
            .Select(item => item.StepPath)
            .ToList();
        return NormalizeSourcePaths(paths);
    }

    private bool CopyStepPathsToClipboard(IReadOnlyList<IReadOnlyList<int>> selectedPaths)
    {
        if (selectedPaths.Count == 0)
        {
            return false;
        }

        try
        {
            var copiedSteps = selectedPaths
                .Select(path => MacroStepTreeEditor.GetAtPath(steps, path))
                .ToList();
            var payload = McrxSerializer.Serialize(new MacroDocument(1, "clipboard", copiedSteps));
            Clipboard.SetText($"{StepClipboardPrefix}{Environment.NewLine}{payload}");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadClipboardSteps(out IReadOnlyList<MacroStep> steps)
    {
        steps = [];
        try
        {
            if (!Clipboard.ContainsText())
            {
                return false;
            }

            var text = Clipboard.GetText();
            var json = text.StartsWith(StepClipboardPrefix, StringComparison.Ordinal)
                ? text[StepClipboardPrefix.Length..].Trim()
                : text.Trim();
            if (string.IsNullOrWhiteSpace(json))
            {
                return false;
            }

            var document = McrxParser.Parse(json);
            steps = document.Steps;
            return steps.Count > 0;
        }
        catch
        {
            steps = [];
            return false;
        }
    }

    private bool IsSelectedStepPath(string pathText)
    {
        return !string.IsNullOrEmpty(pathText)
            && StepList.SelectedItems
                .OfType<StepDisplayItem>()
                .Any(item => string.Equals(item.StepPathText, pathText, StringComparison.Ordinal));
    }

    private void BeginBoxSelection(Point origin, bool preserveExistingSelection)
    {
        boxSelectionActive = true;
        boxSelectionStartPoint = origin;
        boxSelectionLastPoint = origin;
        boxSelectionBasePaths.Clear();

        if (preserveExistingSelection)
        {
            foreach (var item in StepList.SelectedItems.OfType<StepDisplayItem>().Where(item => item.StepPath.Count > 0))
            {
                boxSelectionBasePaths.Add(item.StepPathText);
            }
        }
        else
        {
            StepList.SelectedItems.Clear();
        }

        StepList.CaptureMouse();
        boxSelectionAutoScrollTimer.Start();
        UpdateBoxSelection(origin);
    }

    private void UpdateBoxSelection(Point current)
    {
        if (!boxSelectionActive)
        {
            return;
        }

        var bounds = CreateSelectionBounds(boxSelectionStartPoint, current);
        boxSelectionLastPoint = current;
        AutoScrollStepList(current);
        ShowSelectionRectangle(bounds);
        SelectStepItemsInsideBox(bounds);
    }

    private void FinishBoxSelection()
    {
        if (!boxSelectionActive)
        {
            return;
        }

        boxSelectionActive = false;
        boxSelectionAutoScrollTimer.Stop();
        boxSelectionBasePaths.Clear();
        SelectionRectangle.Visibility = Visibility.Collapsed;
        if (StepList.IsMouseCaptured)
        {
            StepList.ReleaseMouseCapture();
        }
    }

    private bool AutoScrollStepList(Point point)
    {
        var scrollViewer = GetStepScrollViewer();
        if (scrollViewer is null || scrollViewer.ScrollableHeight <= 0 || StepList.ActualHeight <= 0)
        {
            return false;
        }

        const double edgeSize = 42;
        const double maxDelta = 28;
        var delta = 0d;
        if (point.Y < edgeSize)
        {
            delta = -ScaleAutoScrollDelta(edgeSize - point.Y, edgeSize, maxDelta);
        }
        else if (point.Y > StepList.ActualHeight - edgeSize)
        {
            delta = ScaleAutoScrollDelta(point.Y - (StepList.ActualHeight - edgeSize), edgeSize, maxDelta);
        }

        if (Math.Abs(delta) < 0.01)
        {
            return false;
        }

        var next = Math.Clamp(scrollViewer.VerticalOffset + delta, 0, scrollViewer.ScrollableHeight);
        if (Math.Abs(next - scrollViewer.VerticalOffset) < 0.01)
        {
            return false;
        }

        scrollViewer.ScrollToVerticalOffset(next);
        return true;
    }

    private ScrollViewer? GetStepScrollViewer()
    {
        return stepScrollViewer ??= FindVisualChild<ScrollViewer>(StepList);
    }

    private static double ScaleAutoScrollDelta(double distanceIntoEdge, double edgeSize, double maxDelta)
    {
        var ratio = Math.Clamp(distanceIntoEdge / edgeSize, 0.15, 1.0);
        return maxDelta * ratio;
    }

    private void SelectStepItemsInsideBox(Rect selectionBounds)
    {
        var selectedPathTexts = new HashSet<string>(boxSelectionBasePaths, StringComparer.Ordinal);
        for (var i = 0; i < StepList.Items.Count; i++)
        {
            if (StepList.Items[i] is not StepDisplayItem { StepPath.Count: > 0 } item
                || StepList.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem container)
            {
                continue;
            }

            if (GetElementBounds(container, StepList).IntersectsWith(selectionBounds))
            {
                selectedPathTexts.Add(item.StepPathText);
            }
        }

        StepList.SelectedItems.Clear();
        foreach (var item in StepList.Items.OfType<StepDisplayItem>())
        {
            if (item.StepPath.Count > 0 && selectedPathTexts.Contains(item.StepPathText))
            {
                StepList.SelectedItems.Add(item);
            }
        }
    }

    private void ShowSelectionRectangle(Rect bounds)
    {
        SelectionRectangle.Width = Math.Max(1, bounds.Width);
        SelectionRectangle.Height = Math.Max(1, bounds.Height);
        if (SelectionRectangle.RenderTransform is not TranslateTransform transform)
        {
            transform = new TranslateTransform();
            SelectionRectangle.RenderTransform = transform;
        }

        transform.X = bounds.X;
        transform.Y = bounds.Y;
        SelectionRectangle.Visibility = Visibility.Visible;
    }

    private void ShowStepDragGhost(Point position, int selectedCount)
    {
        if (stepDragSourceContainer is null)
        {
            return;
        }

        var layer = AdornerLayer.GetAdornerLayer(StepList);
        if (layer is null)
        {
            return;
        }

        var containerOrigin = stepDragSourceContainer.TranslatePoint(new Point(0, 0), StepList);
        var grabOffset = new Point(stepDragStartPoint.X - containerOrigin.X, stepDragStartPoint.Y - containerOrigin.Y);
        stepDragGhostLayer = layer;
        stepDragGhost = new DragGhostAdorner(
            StepList,
            stepDragSourceContainer,
            position,
            grabOffset,
            selectedCount > 1 ? selectedCount.ToString() : null);
        layer.Add(stepDragGhost);
    }

    private void UpdateStepDragGhost(Point position)
    {
        stepDragGhost?.UpdatePosition(position);
    }

    private void RemoveStepDragGhost()
    {
        if (stepDragGhost is null)
        {
            return;
        }

        var ghost = stepDragGhost;
        var layer = stepDragGhostLayer;
        ghost.FadeOut(() => layer?.Remove(ghost));
        stepDragGhost = null;
        stepDragGhostLayer = null;
    }

    private void FadeStepDragSources(IReadOnlyList<string> pathTexts)
    {
        RestoreStepDragSources();
        foreach (var pathText in pathTexts)
        {
            if (TryGetContainerByPath(ParseStepPath(pathText), out var container))
            {
                container.Opacity = 0.35;
                fadedStepDragContainers.Add(container);
            }
        }
    }

    private void RestoreStepDragSources()
    {
        foreach (var container in fadedStepDragContainers)
        {
            container.Opacity = 1;
        }

        fadedStepDragContainers.Clear();
    }

    private void UpdateDropIndicator(StepDropTarget target)
    {
        if (DropIndicator.RenderTransform is not TranslateTransform transform)
        {
            transform = new TranslateTransform();
            DropIndicator.RenderTransform = transform;
        }

        var indent = Math.Min(48, target.ParentPath.Count * 20 + 10);
        DropIndicator.Margin = new Thickness(indent, 0, 10, 0);
        transform.Y = GetDropIndicatorY(target);
        DropIndicator.Visibility = Visibility.Visible;
    }

    private void HideDropIndicator()
    {
        DropIndicator.Visibility = Visibility.Collapsed;
    }

    private double GetDropIndicatorY(StepDropTarget target)
    {
        var nextPath = target.ParentPath.Concat([target.InsertIndex]).ToArray();
        if (TryGetContainerByPath(nextPath, out var next))
        {
            return next.TranslatePoint(new Point(0, 0), StepList).Y - 2;
        }

        if (target.InsertIndex > 0)
        {
            var previousPath = target.ParentPath.Concat([target.InsertIndex - 1]).ToArray();
            if (TryGetContainerByPath(previousPath, out var previous))
            {
                return previous.TranslatePoint(new Point(0, previous.ActualHeight), StepList).Y + 2;
            }
        }

        if (target.ParentPath.Count > 0 && TryGetContainerByPath(target.ParentPath, out var parent))
        {
            return parent.TranslatePoint(new Point(0, parent.ActualHeight), StepList).Y + 2;
        }

        return Math.Max(0, StepList.ActualHeight - 6);
    }

    private bool TryGetContainerByPath(IReadOnlyList<int> stepPath, out ListBoxItem container)
    {
        var pathText = ToPathText(stepPath);
        for (var i = 0; i < StepList.Items.Count; i++)
        {
            if (StepList.Items[i] is StepDisplayItem item
                && string.Equals(item.StepPathText, pathText, StringComparison.Ordinal)
                && StepList.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem match)
            {
                container = match;
                return true;
            }
        }

        container = null!;
        return false;
    }

    private void SelectStepIndex(int stepIndex)
    {
        if (stepIndex < 0)
        {
            StepList.SelectedIndex = -1;
            return;
        }

        SelectStepPath([stepIndex]);
    }

    private void SelectStepPath(IReadOnlyList<int> stepPath)
    {
        var pathText = ToPathText(stepPath);
        for (var i = 0; i < StepList.Items.Count; i++)
        {
            if (StepList.Items[i] is StepDisplayItem item
                && string.Equals(item.StepPathText, pathText, StringComparison.Ordinal))
            {
                StepList.SelectedIndex = i;
                return;
            }
        }

        StepList.SelectedIndex = -1;
    }

    private void SelectStepPaths(IReadOnlyList<IReadOnlyList<int>> stepPaths)
    {
        var pathTexts = stepPaths
            .Where(path => path.Count > 0)
            .Select(ToPathText)
            .ToHashSet(StringComparer.Ordinal);

        StepList.SelectedItems.Clear();
        if (pathTexts.Count == 0)
        {
            return;
        }

        foreach (var item in StepList.Items.OfType<StepDisplayItem>())
        {
            if (item.StepPath.Count > 0 && pathTexts.Contains(item.StepPathText))
            {
                StepList.SelectedItems.Add(item);
            }
        }
    }

    private StepDropTarget GetDefaultInsertTarget()
    {
        if (StepList.SelectedItem is StepDisplayItem { StepPath.Count: > 0 } selected)
        {
            if (selected.IsStructural)
            {
                return CreateTargetInsideStep(selected, append: true);
            }

            return CreateTargetAfterStep(selected);
        }

        return new StepDropTarget([], steps.Count);
    }

    private void SelectAfterDelete(IReadOnlyList<MacroStep> currentSteps, IReadOnlyList<int> deletedPath)
    {
        var parentPath = deletedPath.Take(deletedPath.Count - 1).ToArray();
        var childCount = GetChildCount(currentSteps, parentPath);
        if (childCount > 0)
        {
            SelectStepPath(parentPath.Concat([Math.Min(deletedPath[^1], childCount - 1)]).ToArray());
            return;
        }

        if (parentPath.Length > 0)
        {
            SelectStepPath(parentPath);
            return;
        }

        StepList.SelectedIndex = -1;
    }

    private IReadOnlyList<IReadOnlyList<int>> GetMovedSelectionPaths(
        IReadOnlyList<IReadOnlyList<int>> sourcePaths,
        StepDropTarget target,
        IReadOnlyList<MacroStep> stepsAfterMove)
    {
        var parentPath = target.ParentPath.ToArray();
        var insertIndex = target.InsertIndex;
        foreach (var sourcePath in sourcePaths.OrderByDescending(path => path, StepPathComparer.Instance))
        {
            var sourceParentPath = sourcePath.Take(sourcePath.Count - 1).ToArray();
            if (PathsEqual(sourceParentPath, parentPath) && sourcePath[^1] < insertIndex)
            {
                insertIndex--;
            }

            parentPath = AdjustPathAfterDelete(parentPath, sourcePath).ToArray();
        }

        var childCount = GetChildCount(stepsAfterMove, parentPath);
        if (childCount == 0)
        {
            return [];
        }

        var start = Math.Clamp(insertIndex, 0, Math.Max(0, childCount - sourcePaths.Count));
        return Enumerable.Range(start, sourcePaths.Count)
            .Select(index => (IReadOnlyList<int>)parentPath.Concat([index]).ToArray())
            .ToList();
    }

    public void SetConditionHighlights(IReadOnlyList<ConditionalDirective>? conditions, Func<int, Brush>? colorSelector = null)
    {
        conditionRanges.Clear();
        if (conditions != null)
        {
            for (int i = 0; i < conditions.Count; i++)
            {
                var c = conditions[i];
                var color = colorSelector?.Invoke(i) ?? GetDefaultConditionColor(i);
                conditionRanges.Add(CreateConditionRangeHighlight(c, color));
            }
        }
        ApplyConditionBarsToItems();
    }

    public void HighlightSingleCondition(int conditionIndex, ConditionalDirective? directive, Brush? color = null)
    {
        conditionRanges.Clear();
        if (directive != null)
        {
            var brush = color ?? GetDefaultConditionColor(conditionIndex);
            conditionRanges.Add(CreateConditionRangeHighlight(directive, brush));
        }
        ApplyConditionBarsToItems();
    }

    private void ApplyConditionBarsToItems()
    {
        var displayItems = StepList.Items.OfType<StepDisplayItem>().ToList();
        var pathOrdinals = displayItems
            .Where(item => item.StepPath.Count > 0 && !IsContainerEnd(item))
            .Select((item, ordinal) => (item.StepPathText, ordinal))
            .GroupBy(item => item.StepPathText, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().ordinal, StringComparer.Ordinal);

        foreach (var item in StepList.Items.OfType<StepDisplayItem>())
        {
            item.ConditionBars.Clear();
            item.IsConditionEndpoint = false;
            var topLevelIndex = item.StepPath.Count > 0 ? item.StepPath[0] : item.Index;
            var itemOrdinal = item.StepPath.Count > 0 && pathOrdinals.TryGetValue(item.StepPathText, out var ordinal)
                ? ordinal
                : -1;

            foreach (var range in conditionRanges)
            {
                if (range.Contains(item.StepPathText, itemOrdinal, topLevelIndex, pathOrdinals))
                {
                    item.ConditionBars.Add(range.Color);
                    if (range.IsEndpoint(item.StepPathText, topLevelIndex))
                    {
                        item.IsConditionEndpoint = true;
                    }
                }
            }
        }
        StepList.Items.Refresh();
    }

    private static string GetStepPathFromSource(DependencyObject? source)
    {
        var item = FindVisualParent<ListBoxItem>(source);
        return item?.DataContext is StepDisplayItem { StepPath.Count: > 0 } step
            ? step.StepPathText
            : string.Empty;
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

    private static bool ShouldStartStepBoxSelection(DependencyObject? source)
    {
        return FindVisualParent<ListBoxItem>(source) is null
            && FindVisualParent<System.Windows.Controls.Primitives.ButtonBase>(source) is null
            && FindVisualParent<System.Windows.Controls.Primitives.ScrollBar>(source) is null;
    }

    private static bool IsContainerStart(StepDisplayItem item)
    {
        return item.Kind is StepDisplayKind.LoopStart or StepDisplayKind.ConditionStart;
    }

    private static bool IsContainerEnd(StepDisplayItem item)
    {
        return item.Kind is StepDisplayKind.LoopEnd or StepDisplayKind.ConditionEnd;
    }

    private static StepDropTarget CreateTargetBeforeStep(StepDisplayItem item)
    {
        return new StepDropTarget(item.StepPath.Take(item.StepPath.Count - 1).ToArray(), item.StepPath[^1]);
    }

    private static StepDropTarget CreateTargetAfterStep(StepDisplayItem item)
    {
        return new StepDropTarget(item.StepPath.Take(item.StepPath.Count - 1).ToArray(), item.StepPath[^1] + 1);
    }

    private StepDropTarget CreateTargetInsideStep(StepDisplayItem item, bool append)
    {
        var childCount = GetChildCount(steps, item.StepPath);
        return new StepDropTarget(item.StepPath.ToArray(), append ? childCount : 0);
    }

    private static int GetChildCount(IReadOnlyList<MacroStep> rootSteps, IReadOnlyList<int> parentPath)
    {
        if (parentPath.Count == 0)
        {
            return rootSteps.Count;
        }

        return MacroStepTreeEditor.GetAtPath(rootSteps, parentPath) switch
        {
            RepeatStep repeat => repeat.Steps.Count,
            PixelWhenStep pixel => pixel.ThenSteps.Count,
            _ => 0
        };
    }

    private static string ToPathText(IReadOnlyList<int> path)
    {
        return string.Join(".", path);
    }

    private static IReadOnlyList<int> ParseStepPath(string? pathText)
    {
        if (string.IsNullOrWhiteSpace(pathText))
        {
            return [];
        }

        var parts = pathText.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var result = new List<int>(parts.Length);
        foreach (var part in parts)
        {
            if (!int.TryParse(part, out var value) || value < 0)
            {
                return [];
            }

            result.Add(value);
        }

        return result;
    }

    private static IReadOnlyList<IReadOnlyList<int>> ParseStepPaths(string? pathTexts)
    {
        if (string.IsNullOrWhiteSpace(pathTexts))
        {
            return [];
        }

        return NormalizeSourcePaths(pathTexts
            .Split(StepDragPathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(ParseStepPath)
            .ToList());
    }

    private static IReadOnlyList<IReadOnlyList<int>> NormalizeSourcePaths(IReadOnlyList<IReadOnlyList<int>> sourcePaths)
    {
        var unique = new List<IReadOnlyList<int>>();
        foreach (var path in sourcePaths.Where(path => path.Count > 0).Select(path => path.ToArray()))
        {
            if (!unique.Any(existing => PathsEqual(existing, path)))
            {
                unique.Add(path);
            }
        }

        unique.Sort(StepPathComparer.Instance);
        return unique
            .Where(path => !unique.Any(other => !PathsEqual(path, other) && PathStartsWith(path, other)))
            .ToList();
    }

    private static bool IsMultiSelectionGesture()
    {
        return (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;
    }

    private static IReadOnlyList<int> AdjustPathAfterDelete(IReadOnlyList<int> path, IReadOnlyList<int> deletedPath)
    {
        if (path.Count == 0 || deletedPath.Count == 0)
        {
            return path.ToArray();
        }

        var deletedParent = deletedPath.Take(deletedPath.Count - 1).ToArray();
        var pathParent = path.Take(Math.Max(0, deletedParent.Length)).ToArray();
        if (PathsEqual(pathParent, deletedParent) && path.Count > deletedParent.Length)
        {
            var adjusted = path.ToArray();
            var level = deletedParent.Length;
            if (adjusted[level] > deletedPath[^1])
            {
                adjusted[level]--;
            }

            return adjusted;
        }

        return path.ToArray();
    }

    private static bool PathStartsWith(IReadOnlyList<int> path, IReadOnlyList<int> prefix)
    {
        return path.Count >= prefix.Count
            && !prefix.Where((value, index) => path[index] != value).Any();
    }

    private static bool PathsEqual(IReadOnlyList<int> left, IReadOnlyList<int> right)
    {
        return left.Count == right.Count && !left.Where((value, index) => right[index] != value).Any();
    }

    private static bool AreSameParent(IReadOnlyList<IReadOnlyList<int>> paths)
    {
        if (paths.Count == 0)
        {
            return false;
        }

        var parent = paths[0].Take(paths[0].Count - 1).ToArray();
        return paths.All(path => PathsEqual(parent, path.Take(path.Count - 1).ToArray()));
    }

    private static Brush GetDefaultConditionColor(int index)
    {
        Brush[] palette =
        [
            new SolidColorBrush(Color.FromRgb(99, 102, 241)),
            new SolidColorBrush(Color.FromRgb(16, 185, 129)),
            new SolidColorBrush(Color.FromRgb(245, 158, 11)),
            new SolidColorBrush(Color.FromRgb(239, 68, 68)),
            new SolidColorBrush(Color.FromRgb(139, 92, 246)),
            new SolidColorBrush(Color.FromRgb(6, 182, 212)),
        ];
        return palette[index % palette.Length];
    }

    private static ConditionRangeHighlight CreateConditionRangeHighlight(ConditionalDirective directive, Brush color)
    {
        return new ConditionRangeHighlight(
            directive.StartStepIndex,
            directive.EndStepIndex,
            directive.StartStepPathText,
            directive.EndStepPathText,
            color);
    }

    private static bool IsModifierKey(Key key)
    {
        return key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;
    }

    private static HidModifier ReadCurrentModifiers()
    {
        var modifiers = HidModifier.None;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) modifiers |= HidModifier.LeftCtrl;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) modifiers |= HidModifier.LeftShift;
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) modifiers |= HidModifier.LeftAlt;
        if ((Keyboard.Modifiers & ModifierKeys.Windows) != 0) modifiers |= HidModifier.LeftGui;
        return modifiers;
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

    private static T? FindVisualChild<T>(DependencyObject? source)
        where T : DependencyObject
    {
        if (source is null)
        {
            return null;
        }

        var count = VisualTreeHelper.GetChildrenCount(source);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(source, i);
            if (child is T match)
            {
                return match;
            }

            var descendant = FindVisualChild<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private void StepUp_Click(object sender, RoutedEventArgs e) => MoveSelectedStep(-1);
    private void StepDown_Click(object sender, RoutedEventArgs e) => MoveSelectedStep(1);
    private void StepDelete_Click(object sender, RoutedEventArgs e) => DeleteSelectedStep();
    private void StepUndo_Click(object sender, RoutedEventArgs e) => UndoRequested?.Invoke();
    private void StepClear_Click(object sender, RoutedEventArgs e)
    {
        if (ClearRequested is not null)
        {
            ClearRequested.Invoke();
        }
        else
        {
            ClearSteps();
        }
    }

    private void StepCopy_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: StepDisplayItem { StepPath.Count: > 0 } item })
        {
            DuplicateStepAtPath(item.StepPath);
            e.Handled = true;
        }
    }

    private void StepRowDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: StepDisplayItem { StepPath.Count: > 0 } item })
        {
            DeleteStepAtPath(item.StepPath);
            e.Handled = true;
        }
    }

    private sealed class StepPathComparer : IComparer<IReadOnlyList<int>>
    {
        public static StepPathComparer Instance { get; } = new();

        public int Compare(IReadOnlyList<int>? x, IReadOnlyList<int>? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            var count = Math.Min(x.Count, y.Count);
            for (var i = 0; i < count; i++)
            {
                var compared = x[i].CompareTo(y[i]);
                if (compared != 0)
                {
                    return compared;
                }
            }

            return x.Count.CompareTo(y.Count);
        }
    }

    private sealed record StepDropTarget(IReadOnlyList<int> ParentPath, int InsertIndex)
    {
        public string ParentPathText => ToPathText(ParentPath);
    }
}

public sealed record StepChoice(int Index, string Label, IReadOnlyList<int> Path)
{
    public string PathText => string.Join(".", Path);

    public override string ToString() => Label;
}

public sealed record ConditionRangeHighlight(
    int StartIndex,
    int EndIndex,
    string StartPathText,
    string EndPathText,
    Brush Color)
{
    public bool Contains(
        string pathText,
        int itemOrdinal,
        int topLevelIndex,
        IReadOnlyDictionary<string, int> pathOrdinals)
    {
        if (!string.IsNullOrWhiteSpace(StartPathText)
            && !string.IsNullOrWhiteSpace(EndPathText)
            && pathOrdinals.TryGetValue(StartPathText, out var startOrdinal)
            && pathOrdinals.TryGetValue(EndPathText, out var endOrdinal)
            && itemOrdinal >= 0)
        {
            var min = Math.Min(startOrdinal, endOrdinal);
            var max = Math.Max(startOrdinal, endOrdinal);
            return itemOrdinal >= min && itemOrdinal <= max;
        }

        var legacyStart = Math.Min(StartIndex, EndIndex);
        var legacyEnd = Math.Max(StartIndex, EndIndex);
        return topLevelIndex >= legacyStart && topLevelIndex <= legacyEnd;
    }

    public bool IsEndpoint(string pathText, int topLevelIndex)
    {
        if (!string.IsNullOrWhiteSpace(StartPathText) || !string.IsNullOrWhiteSpace(EndPathText))
        {
            return string.Equals(pathText, StartPathText, StringComparison.Ordinal)
                || string.Equals(pathText, EndPathText, StringComparison.Ordinal);
        }

        return topLevelIndex == StartIndex || topLevelIndex == EndIndex;
    }
}
