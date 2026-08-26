using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MacroHid.Core;
using MacroStudio.Services;

namespace MacroStudio.Controls;

public partial class SequencePanel : UserControl
{
    private const int MaxUndoDepth = 64;

    private MacroEditorState? state;
    private bool updatingMacroName;
    private bool isReadOnly;
    private bool isRecording;
    private string editorText = string.Empty;
    private readonly List<string> undoStack = [];
    private readonly List<string> redoStack = [];

    public event Action? SaveLibraryRequested;
    public event Action? RunNowRequested;
    public event Action? StopRequested;
    public event Action<MacroRecordingMode>? RecordingStartRequested;
    public event Action? RecordingStopRequested;
    public event Action<int>? StepSelectionChanged;
    public event Action<MacroActionTemplateKind, string, int>? ActionTemplateDropped;
    public event Action<string, string, int>? MacroLibraryDropped;
    public event Action? UndoApplied;
    public event Action? RedoApplied;
    public event Action? DocumentEdited;
    public event Action? SequenceActivated;
    public event Action<string>? EditorTextChanged;
    public event Action<bool>? EditLockChanged;

    public SequencePanel()
    {
        InitializeComponent();
    }

    public string EditorText
    {
        get => editorText;
        set
        {
            var normalized = value ?? string.Empty;
            if (string.Equals(editorText, normalized, StringComparison.Ordinal))
            {
                return;
            }

            editorText = normalized;
            EditorTextChanged?.Invoke(editorText);
        }
    }

    public int SelectedStepIndex
    {
        get => StepSequenceControl.SelectedStepIndex;
        set => StepSequenceControl.SelectedStepIndex = value;
    }

    public IReadOnlyList<int> SelectedStepPath => StepSequenceControl.SelectedStepPath;
    public string MacroName => MacroNameBox.Text.Trim();
    public bool CanUndo => undoStack.Count > 0;
    public bool CanRedo => redoStack.Count > 0;
    public bool IsReadOnly => isReadOnly;

    public void Initialize(MacroEditorState editorState)
    {
        state = editorState;
        StepSequenceControl.Initialize(editorState);
        StepSequenceControl.SetMacroNameResolver(ResolveMacroDisplayName);
        StepSequenceControl.BeforeStepsChanged += CaptureUndoSnapshot;
        StepSequenceControl.StepsChanged += OnStepSequenceStepsChanged;
        StepSequenceControl.StepSelectionChanged += index => StepSelectionChanged?.Invoke(index);
        StepSequenceControl.ActionTemplateDropped += (kind, parentPath, insertIndex) => ActionTemplateDropped?.Invoke(kind, parentPath, insertIndex);
        StepSequenceControl.MacroLibraryDropped += (macroId, parentPath, insertIndex) => MacroLibraryDropped?.Invoke(macroId, parentPath, insertIndex);
        StepSequenceControl.UndoRequested += UndoLastChange;
        StepSequenceControl.RedoRequested += RedoLastChange;
        StepSequenceControl.ClearRequested += ClearAllSteps;
        StepSequenceControl.Activated += () => SequenceActivated?.Invoke();
    }

    public void ApplyLocalization()
    {
        SaveLibraryButton.Content = L("SaveLibrary");
        RunNowHeaderButton.Content = L("RunNow");
        StopHeaderButton.Content = L("Stop");
        RecordInputButton.Content = L(isRecording ? "StopRecording" : "StartRecording");
        RecordInputButton.ToolTip = L("RecordingHelp");
        RecordingStatusText.Text = isRecording ? L("RecordingActive") : string.Empty;
        NameLabelText.Text = L("Name");
        ScheduledStepsLabelText.Text = L("ScheduledSteps");
        DurationLabelText.Text = L("Duration");
        MacroLockButton.Content = isReadOnly ? L("UnlockMacro") : L("LockMacro");
        MacroLockButton.ToolTip = isReadOnly ? L("UnlockMacroHelp") : L("LockMacroHelp");
        StepSequenceControl.ApplyLocalization();
    }

    public void SetReadOnly(bool value)
    {
        isReadOnly = value;
        MacroLockButton.IsChecked = value;
        MacroLockButton.Content = value ? L("UnlockMacro") : L("LockMacro");
        MacroLockButton.ToolTip = value ? L("UnlockMacroHelp") : L("LockMacroHelp");
        MacroNameBox.IsReadOnly = value;
        SaveLibraryButton.IsEnabled = !value;
        RecordInputButton.IsEnabled = isRecording || !value;
        StepSequenceControl.SetReadOnly(value);
    }

    public void SetRecordingState(bool recording, int inputCount = 0)
    {
        isRecording = recording;
        MacroLockButton.IsEnabled = !recording;
        RecordInputButton.Content = L(recording ? "StopRecording" : "StartRecording");
        RecordInputButton.ToolTip = L("RecordingHelp");
        RecordInputButton.IsEnabled = recording || !isReadOnly;
        RecordingStatusText.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        RecordingStatusText.Text = recording
            ? (inputCount > 0
                ? LocalizationService.Format("RecordingCaptured", inputCount)
                : L("RecordingActive"))
            : string.Empty;
    }

    public void SetEditorDocument(MacroDocument document)
    {
        EditorText = McrxSerializer.Serialize(document);
        ValidateCurrentMacro();
        UpdateUndoButtonState();
    }

    public void CaptureUndoSnapshot()
    {
        if (isReadOnly) return;
        var current = EditorText;
        if (string.IsNullOrWhiteSpace(current))
        {
            return;
        }

        if (undoStack.Count > 0 && string.Equals(undoStack[^1], current, StringComparison.Ordinal))
        {
            return;
        }

        PushSnapshot(undoStack, current);
        redoStack.Clear();

        UpdateUndoButtonState();
    }

    public void ClearUndoHistory()
    {
        undoStack.Clear();
        redoStack.Clear();
        UpdateUndoButtonState();
    }

    public void UndoLastChange()
    {
        if (isReadOnly) return;
        if (undoStack.Count == 0)
        {
            return;
        }

        var previous = undoStack[^1];
        undoStack.RemoveAt(undoStack.Count - 1);
        PushSnapshot(redoStack, EditorText);
        EditorText = previous;
        ValidateCurrentMacro();
        UpdateUndoButtonState();
        UndoApplied?.Invoke();
    }

    public void RedoLastChange()
    {
        if (isReadOnly) return;
        if (redoStack.Count == 0)
        {
            return;
        }

        var next = redoStack[^1];
        redoStack.RemoveAt(redoStack.Count - 1);
        PushSnapshot(undoStack, EditorText);
        EditorText = next;
        ValidateCurrentMacro();
        UpdateUndoButtonState();
        RedoApplied?.Invoke();
    }

    public void ValidateCurrentMacro()
    {
        try
        {
            var document = McrxParser.Parse(EditorText);
            var scheduled = MacroScheduler.Compile(document, Stopwatch.GetTimestamp(), Stopwatch.Frequency);

            MacroNameText.Text = document.Name;
            updatingMacroName = true;
            MacroNameBox.Text = document.Name;
            updatingMacroName = false;
            StepCountText.Text = scheduled.Count.ToString();
            DurationText.Text = FormatDurationRange(EstimateDurationRange(document.Steps));
            StepSequenceControl.SetSteps(document.Steps);
        }
        catch (Exception ex)
        {
            MacroNameText.Text = "-";
            updatingMacroName = true;
            MacroNameBox.Text = string.Empty;
            updatingMacroName = false;
            StepCountText.Text = "0";
            DurationText.Text = "0 ms";
            StepSequenceControl.SetSteps([]);
            // Keep the JSON text as the source of truth and surface parse errors in the status row.
            StepSequenceControl.SetTitle(ex.Message);
        }
    }

    public MacroDocument GetCurrentDocument() => McrxParser.Parse(EditorText);

    public IReadOnlyList<StepChoice> GetStepChoices() => StepSequenceControl.GetStepChoices();

    public void InsertSteps(IReadOnlyList<MacroStep> stepsToInsert) => StepSequenceControl.InsertSteps(stepsToInsert);
    public void InsertStepsAt(IReadOnlyList<MacroStep> stepsToInsert, int insertIndex) => StepSequenceControl.InsertStepsAt(stepsToInsert, insertIndex);
    public void InsertStepsAtPath(IReadOnlyList<MacroStep> stepsToInsert, string parentPathText, int insertIndex) => StepSequenceControl.InsertStepsAtPath(stepsToInsert, parentPathText, insertIndex);
    public bool HandleExplorerShortcut(Key key, ModifierKeys modifiers) => StepSequenceControl.HandleExplorerShortcut(key, modifiers);
    public bool SelectAllSteps() => StepSequenceControl.SelectAllSteps();
    public bool CopySelectedStepsToClipboard() => StepSequenceControl.CopySelectedStepsToClipboard();
    public bool CutSelectedStepsToClipboard() => StepSequenceControl.CutSelectedStepsToClipboard();
    public bool PasteStepsFromClipboard() => StepSequenceControl.PasteStepsFromClipboard();
    public void MoveSelectedStep(int offset) => StepSequenceControl.MoveSelectedStep(offset);
    public void MoveSelectedSteps(int offset) => StepSequenceControl.MoveSelectedSteps(offset);
    public void DeleteSelectedStep() => StepSequenceControl.DeleteSelectedStep();
    public void DeleteStepAtIndex(int stepIndex) => StepSequenceControl.DeleteStepAtIndex(stepIndex);
    public void DeleteStepAtPath(IReadOnlyList<int> stepPath) => StepSequenceControl.DeleteStepAtPath(stepPath);
    public void DuplicateStepAtIndex(int stepIndex) => StepSequenceControl.DuplicateStepAtIndex(stepIndex);
    public void DuplicateStepAtPath(IReadOnlyList<int> stepPath) => StepSequenceControl.DuplicateStepAtPath(stepPath);
    public void ApplyEditedStep(MacroStep newStep) => StepSequenceControl.ApplyEditedStep(newStep);
    public void CloseInlineStepEditorOnExternalPointerDown(DependencyObject? source) => StepSequenceControl.CloseInlineStepEditorOnExternalPointerDown(source);
    public void SetConditionHighlights(IReadOnlyList<ConditionalDirective>? conditions, Func<int, Brush>? colorSelector = null) => StepSequenceControl.SetConditionHighlights(conditions, colorSelector);
    public void HighlightSingleCondition(int conditionIndex, ConditionalDirective? directive, Brush? color = null) => StepSequenceControl.HighlightSingleCondition(conditionIndex, directive, color);

    public void ClearAllSteps()
    {
        if (isReadOnly) return;
        var document = McrxParser.Parse(EditorText);
        if (document.Steps.Count == 0 && document.EffectiveConditions.Count == 0)
        {
            return;
        }

        CaptureUndoSnapshot();
        SetEditorDocument(document with
        {
            Steps = [],
            Conditions = []
        });
        RaiseDocumentEdited();
    }

    public void ApplyMacroName(string name)
    {
        if (isReadOnly) return;
        var document = McrxParser.Parse(EditorText);
        if (string.Equals(document.Name, name, StringComparison.Ordinal)) return;
        CaptureUndoSnapshot();
        SetEditorDocument(document with { Name = name });
        RaiseDocumentEdited();
    }

    private void OnStepSequenceStepsChanged()
    {
        if (isReadOnly) return;
        var document = McrxParser.Parse(EditorText);
        EditorText = McrxSerializer.Serialize(document with { Steps = StepSequenceControl.Steps });
        ValidateCurrentMacro();
        RaiseDocumentEdited();
    }

    private string ResolveMacroDisplayName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var item = state?.LibrarySnapshot.Items.FirstOrDefault(item => item.MatchesReference(value));
        return item?.Name ?? value;
    }

    private void SaveLibrary_Click(object sender, RoutedEventArgs e) => SaveLibraryRequested?.Invoke();
    private void MacroLockButton_Click(object sender, RoutedEventArgs e) => EditLockChanged?.Invoke(MacroLockButton.IsChecked == true);
    private void RunNow_Click(object sender, RoutedEventArgs e) => RunNowRequested?.Invoke();
    private void Stop_Click(object sender, RoutedEventArgs e) => StopRequested?.Invoke();
    private void RecordInputButton_Click(object sender, RoutedEventArgs e)
    {
        if (isRecording)
        {
            RecordingStopRequested?.Invoke();
        }
        else if (!isReadOnly)
        {
            RecordingModeMenu.Show(RecordInputButton, mode => RecordingStartRequested?.Invoke(mode));
        }
    }

    private void MacroNameBox_LostFocus(object sender, RoutedEventArgs e) => ApplyNameFromBox();

    private void MacroNameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ApplyNameFromBox();
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    private void ApplyNameFromBox()
    {
        if (updatingMacroName) return;
        var name = MacroNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;
        try { ApplyMacroName(name); } catch { }
    }

    private static TimeSpan EstimateDuration(IReadOnlyList<MacroStep> steps)
    {
        return EstimateDurationRange(steps).Max;
    }

    private static DurationRange EstimateDurationRange(IReadOnlyList<MacroStep> steps)
    {
        var minTicks = 0L;
        var maxTicks = 0L;
        foreach (var step in steps)
        {
            var range = EstimateStepDurationRange(step);
            minTicks += range.Min.Ticks;
            maxTicks += range.Max.Ticks;
        }

        return new DurationRange(TimeSpan.FromTicks(minTicks), TimeSpan.FromTicks(maxTicks));
    }

    private static TimeSpan EstimateStepDuration(MacroStep step)
    {
        return EstimateStepDurationRange(step).Max;
    }

    private static DurationRange EstimateStepDurationRange(MacroStep step) => step switch
    {
        KeyStep key => FixedDuration(key.Hold),
        MouseMoveStep move => FixedDuration(move.Duration),
        MouseButtonStep button => FixedDuration(button.Hold),
        ConsumerStep consumer => FixedDuration(consumer.Hold),
        WaitStep wait => new DurationRange(wait.Duration, wait.MaxDuration ?? wait.Duration),
        RepeatStep repeat => MultiplyDuration(EstimateDurationRange(repeat.Steps), Math.Max(0, repeat.Count)),
        PixelWhenStep pixel => EstimateDurationRange(pixel.ThenSteps),
        _ => FixedDuration(TimeSpan.Zero)
    };

    private static DurationRange FixedDuration(TimeSpan duration) => new(duration, duration);

    private static DurationRange MultiplyDuration(DurationRange range, int count)
    {
        return new DurationRange(
            TimeSpan.FromTicks(range.Min.Ticks * count),
            TimeSpan.FromTicks(range.Max.Ticks * count));
    }

    private static string FormatDuration(TimeSpan duration)
    {
        return duration.TotalSeconds >= 1 ? $"{duration.TotalSeconds:0.###} s" : $"{duration.TotalMilliseconds:0.###} ms";
    }

    private static string FormatDurationRange(DurationRange range)
    {
        return range.Min == range.Max
            ? FormatDuration(range.Max)
            : string.Join(" ~ ", [FormatDuration(range.Min), FormatDuration(range.Max)]);
    }

    private readonly record struct DurationRange(TimeSpan Min, TimeSpan Max);

    private void UpdateUndoButtonState()
    {
        StepSequenceControl.SetCanUndo(CanUndo);
        StepSequenceControl.SetCanRedo(CanRedo);
    }

    private static void PushSnapshot(List<string> stack, string snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot)
            || (stack.Count > 0 && string.Equals(stack[^1], snapshot, StringComparison.Ordinal)))
        {
            return;
        }

        stack.Add(snapshot);
        if (stack.Count > MaxUndoDepth)
        {
            stack.RemoveAt(0);
        }
    }

    private void RaiseDocumentEdited()
    {
        DocumentEdited?.Invoke();
    }

    private static string L(string key) => LocalizationService.Get(key);
}
