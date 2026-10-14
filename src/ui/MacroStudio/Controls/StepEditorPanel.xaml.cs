using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MacroHid.Core;
using MacroHid.Runtime;
using MacroStudio.Services;
using CoreMouseButton = MacroHid.Core.MouseButton;

namespace MacroStudio.Controls;

public partial class StepEditorPanel : UserControl
{
    private MacroEditorState? state;
    private bool capturingStepKey;
    private bool updatingEditor;
    private bool applyingOcrExtractPreset;
    private ScreenRegion ocrExtractRegion = ScreenRegion.FromRect(0, 0, 640, 360);
    private ScreenRegion ocrClickRegion = ScreenRegion.FromRect(0, 0, 640, 360);

    public static event Action? AnyKeyCaptureStarted;
    public static event Action? AnyKeyCaptureFinished;

    public event Action<MacroStep>? StepEdited;
    public event EventHandler? CoordinatePickerStarted;
    public event EventHandler? CoordinatePickerFinished;

    public StepEditorPanel()
    {
        InitializeComponent();
        FillEnumBox(ActionKindBox, ButtonActionKind.Down);
        FillEnumBox(MouseButtonBox, CoreMouseButton.Left);
        FillEnumBox(MouseButtonCoordinateModeBox, MouseMoveMode.Absolute);
        FillEnumBox(MoveModeBox, MouseMoveMode.Relative);
        FillEnumBox(OcrClickMouseButtonBox, CoreMouseButton.Left);
        OcrExtractRegexBox.IsChecked = true;
        StepEditorFieldsPanel.Visibility = Visibility.Collapsed;
        ApplyStepEditButton.IsEnabled = false;
        PreviewKeyDown += Editor_PreviewKeyDown;
        IsVisibleChanged += (_, _) => { if (IsVisible) FocusPrimaryEditor(); };
    }

    public void Initialize(MacroEditorState editorState)
    {
        state = editorState;
        RefreshMacroTargetBox();
    }

    public void ApplyLocalization()
    {
        StepEditorTitleText.Text = L("StepProperties");
        StepEditorHintText.Text = L("StepPropertiesHint");
        ApplyStepEditButton.Content = L("Apply");
        MacroTargetLabelText.Text = L("Macro");
        KeyLabelText.Text = L("Key");
        CaptureStepKeyButton.Content = L("Capture");
        ActionKindLabelText.Text = L("Action");
        MouseButtonLabelText.Text = L("AddMouseButton");
        MoveModeLabelText.Text = L("MoveMode");
        WheelLabelText.Text = L("AddWheel");
        WindowProcessLabelText.Text = L("WindowProcessName");
        PickWindowTargetButton.Content = L("SelectWindow");
        WindowTitleLabelText.Text = L("WindowTitleOptional");
        WindowTitleRegexBox.Content = L("WindowTitleUseRegex");
        WindowTitleRegexErrorText.Text = L("InvalidWindowTitleRegex");
        WindowMatchIndexLabelText.Text = L("WindowMatchIndex");
        WindowTimeoutLabelText.Text = L("WindowActivationTimeoutMs");
        WindowRestoreBox.Content = L("WindowRestore");
        WindowFailIfNotFoundBox.Content = L("WindowFailStopsMacro");
        WindowActivateStatusText.Text = L("WindowActivationConfirmationHint");
        OcrExtractPresetLabelText.Text = L("OcrExtractPreset");
        OcrExtractPresetCustomItem.Content = L("OcrExtractPresetCustom");
        OcrExtractPresetAllTextItem.Content = L("OcrExtractPresetAllText");
        OcrExtractPresetDigitsItem.Content = L("OcrExtractPresetDigits");
        OcrExtractPresetFilterDigitsItem.Content = L("OcrExtractPresetFilterDigits");
        OcrExtractPresetAlphaNumericItem.Content = L("OcrExtractPresetAlphaNumeric");
        OcrExtractPresetUidItem.Content = L("OcrExtractPresetUid");
        OcrExtractPresetAfterSeparatorItem.Content = L("OcrExtractPresetAfterSeparator");
        OcrExtractFilterTermsLabelText.Text = L("OcrExtractFilterTerms");
        OcrExtractKeepDigitsOnlyBox.Content = L("OcrExtractKeepDigitsOnly");
        OcrExtractPatternLabelText.Text = L("OcrExtractPattern");
        OcrExtractRegexBox.Content = L("OcrExtractUseRegex");
        OcrExtractRegexErrorText.Text = L("InvalidRegex");
        OcrExtractRegionLabelText.Text = L("OcrExtractRegion");
        PickOcrExtractRegionButton.Content = L("SelectRegion");
        OcrExtractLanguageLabelText.Text = L("OcrExtractLanguage");
        OcrExtractMatchIndexLabelText.Text = L("OcrExtractMatchIndex");
        OcrExtractCaptureGroupLabelText.Text = L("OcrExtractCaptureGroup");
        OcrExtractNormalizeWhitespaceBox.Content = L("OcrExtractNormalizeWhitespace");
        OcrExtractFailIfNotFoundBox.Content = L("OcrExtractFailStopsMacro");
        TestOcrExtractButton.Content = L("OcrExtractTest");
        OcrClickExpectedLabelText.Text = L("OcrClickExpectedText");
        OcrClickRegexBox.Content = L("OcrClickUseRegex");
        OcrClickContainsBox.Content = L("OcrClickContains");
        OcrClickRegexErrorText.Text = L("InvalidRegex");
        OcrClickRegionLabelText.Text = L("OcrClickRegion");
        PickOcrClickRegionButton.Content = L("SelectRegion");
        OcrClickLanguageLabelText.Text = L("OcrClickLanguage");
        OcrClickButtonLabelText.Text = L("OcrClickMouseButton");
        OcrClickCountLabelText.Text = L("OcrClickCount");
        OcrClickMatchIndexLabelText.Text = L("OcrClickMatchIndex");
        OcrClickHoldLabelText.Text = L("OcrClickHoldMs");
        OcrClickIntervalLabelText.Text = L("OcrClickIntervalMs");
        OcrClickOffsetXLabelText.Text = L("OcrClickOffsetX");
        OcrClickOffsetYLabelText.Text = L("OcrClickOffsetY");
        TestOcrClickButton.Content = L("OcrClickTest");
        TimingLabelText.Text = L("MouseMoveDuration");
        MouseMoveDurationHintText.Text = L("MouseMoveDurationHint");
        DelayModeLabelText.Text = L("DelayMode");
        DelayFixedLabelText.Text = L("TimingMs");
        DelayMinLabelText.Text = L("DelayMinMs");
        DelayMaxLabelText.Text = L("DelayMaxMs");
        TextActionLabelText.Text = L("AddText");
        LoopCountLabelText.Text = L("LoopCount");
        NetLimitProcessLabelText.Text = L("NetLimitProcess");
        NetLimitBytesLabelText.Text = L("NetLimitBytes");
        NetLimitHintText.Text = L("NetLimitHint");
        NetLimitStopHintText.Text = L("NetLimitStopHint");
        ClockShiftHoursLabelText.Text = L("WeakNetShiftHours");
        ClockShiftMinutesLabelText.Text = L("WeakNetShiftMinutes");
        ClockShiftHintText.Text = L("ClockShiftHint");
        ClockRestoreHintText.Text = L("ClockRestoreHint");
        WeakNetProcessLabelText.Text = L("WeakNetProcess");
        WeakNetDurationLabelText.Text = L("WeakNetDurationMs");
        WeakNetShiftHoursLabelText.Text = L("WeakNetShiftHours");
        WeakNetShiftMinutesLabelText.Text = L("WeakNetShiftMinutes");
        WeakNetShiftHintText.Text = L("WeakNetShiftHint");
        WeakNetStopHintText.Text = L("WeakNetStopHint");
        FrameLockProcessLabelText.Text = L("FrameLockProcess");
        FrameLockFpsLabelText.Text = L("FrameLockFps");
        FrameLockHintText.Text = L("FrameLockHint");
        ParallelTimeoutLabelText.Text = L("ParallelTimeoutMs");
        TextWhenExpectedLabelText.Text = L("TextWhenExpected");
        TextWhenContainsBox.Content = L("TextWhenContains");
        TextWhenRegexBox.Content = L("TextWhenUseRegex");
        TextWhenRegionLabelText.Text = L("TextWhenRegion");
        PixelLabelText.Text = L("PixelIf");
        PickPixelColorButton.Content = L("PickColor");
        RefreshEnumBoxLocalization(ActionKindBox);
        RefreshEnumBoxLocalization(MouseButtonBox);
        RefreshEnumBoxLocalization(MouseButtonCoordinateModeBox);
        RefreshEnumBoxLocalization(MoveModeBox);
        RefreshEnumBoxLocalization(OcrClickMouseButtonBox);
    }

    public void RefreshMacroTargetBox(string? selected = null)
    {
        if (state is null || MacroTargetBox is null) return;

        var previous = selected ?? (MacroTargetBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        MacroTargetBox.Items.Clear();
        foreach (var item in state.LibraryStore.Load().Items.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var comboItem = new ComboBoxItem { Tag = item.Name, Content = item.Name };
            MacroTargetBox.Items.Add(comboItem);
            if (item.MatchesReference(previous))
            {
                MacroTargetBox.SelectedItem = comboItem;
            }
        }
    }

    public void ShowStep(MacroStep? step)
    {
        StopStepKeyCapture();
        if (step is null)
        {
            StepEditorFieldsPanel.Visibility = Visibility.Collapsed;
            ApplyStepEditButton.IsEnabled = false;
            StepEditorHintText.Text = L("StepPropertiesHint");
            return;
        }

        if (step is StopCurrentSequenceStep or StopCurrentIterationStep or StopAllSequencesStep)
        {
            StepEditorFieldsPanel.Visibility = Visibility.Collapsed;
            ApplyStepEditButton.IsEnabled = false;
            StepEditorHintText.Text = step switch
            {
                StopAllSequencesStep => L("AddStopAllHint"),
                StopCurrentIterationStep => L("AddStopIterationHint"),
                _ => L("AddStopCurrentHint")
            };
            return;
        }

        updatingEditor = true;
        PopulateStepEditor(step);
        updatingEditor = false;
        StepEditorFieldsPanel.Visibility = Visibility.Visible;
        ApplyStepEditButton.IsEnabled = true;
        FocusPrimaryEditor();
    }

    public void SetHintText(string text)
    {
        StepEditorHintText.Text = text;
    }

    private void PopulateStepEditor(MacroStep step)
    {
        SetEditorPanels(
            keyboard: step is KeyStep,
            action: step is KeyStep or MouseButtonStep or ConsumerStep,
            mouseButton: step is MouseButtonStep,
            mouseMove: step is MouseMoveStep,
            wheel: step is MouseWheelStep,
            windowActivate: step is WindowActivateStep,
            ocrExtractText: step is OcrExtractTextStep,
            ocrClick: step is OcrClickStep,
            timing: step is MouseMoveStep,
            delay: step is WaitStep,
            text: step is TextStep or CommentStep,
            loop: step is RepeatStep,
            macro: step is MacroCallStep,
            pixel: step is PixelWhenStep,
            netLimit: step is NetLimitStep,
            clockShift: step is ClockShiftStep,
            weakNet: step is WeakNetStep,
            frameLock: step is FrameLockStep or YsrFpsToggleStep,
            parallel: step is ParallelStep,
            textWhen: step is TextWhenStep);

        switch (step)
        {
            case KeyStep key:
                FillEnumBox(ActionKindBox, key.Kind);
                StepKeyBox.Text = key.Key.ToString();
                SetComboBox(ActionKindBox, key.Kind.ToString());
                break;
            case MouseButtonStep button:
                FillEnumBox(ActionKindBox, button.Kind);
                SetComboBox(MouseButtonBox, button.Button.ToString());
                SetComboBox(ActionKindBox, button.Kind.ToString());
                MouseButtonCoordinateEnabledBox.IsChecked = button.HasCoordinate;
                SetComboBox(MouseButtonCoordinateModeBox, (button.CoordinateMode ?? MouseMoveMode.Absolute).ToString());
                MouseButtonXBox.Text = button.X?.ToString() ?? string.Empty;
                MouseButtonYBox.Text = button.Y?.ToString() ?? string.Empty;
                break;
            case ConsumerStep consumer:
                FillEnumBox(ActionKindBox, consumer.Kind);
                SetComboBox(ActionKindBox, consumer.Kind.ToString());
                break;
            case MouseMoveStep move:
                SetComboBox(MoveModeBox, move.Mode.ToString());
                MoveXBox.Text = move.X.ToString();
                MoveYBox.Text = move.Y.ToString();
                TimingMsBox.Text = FormatNumber(move.Duration.TotalMilliseconds);
                break;
            case MouseWheelStep wheel:
                WheelVerticalBox.Text = wheel.Vertical.ToString();
                WheelHorizontalBox.Text = wheel.Horizontal.ToString();
                break;
            case WindowActivateStep windowActivate:
                WindowProcessBox.Text = windowActivate.ProcessName;
                WindowTitleBox.Text = windowActivate.WindowTitle;
                WindowTitleRegexBox.IsChecked = windowActivate.UseTitleRegex;
                WindowMatchIndexBox.Text = windowActivate.MatchIndex.ToString();
                WindowTimeoutMsBox.Text = FormatNumber(
                    (windowActivate.Timeout > TimeSpan.Zero
                        ? windowActivate.Timeout
                        : TimeSpan.FromSeconds(3)).TotalMilliseconds);
                WindowRestoreBox.IsChecked = windowActivate.Restore;
                WindowFailIfNotFoundBox.IsChecked = windowActivate.FailIfNotFound;
                ValidateWindowTitleRegex();
                break;
            case OcrExtractTextStep ocrExtractText:
                ocrExtractRegion = ocrExtractText.Region;
                OcrExtractPatternBox.Text = ocrExtractText.Pattern;
                OcrExtractRegexBox.IsChecked = ocrExtractText.UseRegex;
                SetComboBox(OcrExtractLanguageBox, ocrExtractText.Language);
                OcrExtractMatchIndexBox.Text = ocrExtractText.MatchIndex.ToString();
                OcrExtractCaptureGroupBox.Text = ocrExtractText.CaptureGroup.ToString();
                OcrExtractFilterTermsBox.Text = ocrExtractText.FilterTerms;
                OcrExtractKeepDigitsOnlyBox.IsChecked = ocrExtractText.KeepDigitsOnly;
                OcrExtractNormalizeWhitespaceBox.IsChecked = ocrExtractText.NormalizeWhitespace;
                OcrExtractFailIfNotFoundBox.IsChecked = ocrExtractText.FailIfNotFound;
                SelectOcrExtractPreset(
                    ocrExtractText.Pattern,
                    ocrExtractText.CaptureGroup,
                    ocrExtractText.FilterTerms,
                    ocrExtractText.KeepDigitsOnly);
                OcrExtractStatusText.Text = PaddleOcrBridge.DefaultStatusText;
                UpdateOcrExtractRegionInfo();
                ValidateOcrExtractRegex();
                break;
            case OcrClickStep ocrClick:
                ocrClickRegion = ocrClick.Region;
                OcrClickExpectedTextBox.Text = ocrClick.ExpectedText;
                OcrClickRegexBox.IsChecked = ocrClick.UseRegex;
                OcrClickContainsBox.IsChecked = ocrClick.Contains;
                SetComboBox(OcrClickLanguageBox, ocrClick.Language);
                SetComboBox(OcrClickMouseButtonBox, ocrClick.Button.ToString());
                OcrClickCountBox.Text = ocrClick.ClickCount.ToString();
                OcrClickMatchIndexBox.Text = ocrClick.MatchIndex.ToString();
                OcrClickHoldMsBox.Text = FormatNumber(ocrClick.Hold.TotalMilliseconds);
                OcrClickIntervalMsBox.Text = FormatNumber(ocrClick.Interval.TotalMilliseconds);
                OcrClickOffsetXBox.Text = ocrClick.OffsetX.ToString();
                OcrClickOffsetYBox.Text = ocrClick.OffsetY.ToString();
                OcrClickStatusText.Text = PaddleOcrBridge.DefaultStatusText;
                UpdateOcrClickRegionInfo();
                ValidateOcrClickRegex();
                break;
            case WaitStep wait:
                PopulateDelayEditor(wait);
                break;
            case TextStep text:
                TextActionLabelText.Text = L("AddText");
                StepTextBox.Text = text.Text;
                break;
            case CommentStep comment:
                TextActionLabelText.Text = L("AddComment");
                StepTextBox.Text = comment.Text;
                break;
            case RepeatStep repeat:
                LoopCountBox.Text = repeat.Count.ToString();
                break;
            case NetLimitStep limit:
                NetLimitProcessBox.Text = limit.ProcessName;
                NetLimitBytesBox.Text = limit.EffectiveBytesPerSecond.ToString();
                NetLimitHintText.Text = limit.Engine == NetLimitEngine.NetLimiter ? L("NetLimiterHint") : L("NetLimitHint");
                NetLimitStopHintText.Text = limit.Engine == NetLimitEngine.NetLimiter ? L("NetLimiterStopHint") : L("NetLimitStopHint");
                ShowNetLimitFields(limit.Action);
                break;
            case ClockShiftStep clock:
                ClockShiftHoursBox.Text = ((int)clock.EffectiveTimeShift.TotalHours).ToString();
                ClockShiftMinutesBox.Text = clock.EffectiveTimeShift.Minutes.ToString();
                ShowClockShiftFields(clock.Action);
                break;
            case WeakNetStep weak:
                WeakNetProcessBox.Text = weak.ProcessName;
                WeakNetDurationBox.Text = FormatNumber(weak.EffectiveDuration.TotalMilliseconds);
                WeakNetShiftHoursBox.Text = ((int)weak.EffectiveTimeShift.TotalHours).ToString();
                WeakNetShiftMinutesBox.Text = weak.EffectiveTimeShift.Minutes.ToString();
                ShowWeakNetFields(weak.Action);
                break;
            case FrameLockStep frame:
                FrameLockProcessBox.Text = frame.ProcessName;
                FrameLockFpsBox.Text = frame.EffectiveFps.ToString();
                ShowFrameLockFields(frame.Action);
                break;
            case YsrFpsToggleStep ysr:
                FrameLockFpsBox.Text = ysr.ToggleFps.ToString();
                ShowFrameLockFields(ysr.Action == YsrFpsAction.Stop ? FrameLockAction.Stop : FrameLockAction.Start, true, ysr.Backend);
                break;
            case ParallelStep parallel:
                ParallelTimeoutBox.Text = parallel.Timeout is { } timeout && timeout > TimeSpan.Zero
                    ? FormatNumber(timeout.TotalMilliseconds)
                    : string.Empty;
                break;
            case TextWhenStep textWhen:
                TextWhenExpectedBox.Text = textWhen.ExpectedText;
                TextWhenContainsBox.IsChecked = textWhen.Contains;
                TextWhenRegexBox.IsChecked = textWhen.UseRegex;
                TextWhenLeftBox.Text = textWhen.Region.TopLeft.X.ToString();
                TextWhenTopBox.Text = textWhen.Region.TopLeft.Y.ToString();
                TextWhenRightBox.Text = textWhen.Region.BottomRight.X.ToString();
                TextWhenBottomBox.Text = textWhen.Region.BottomRight.Y.ToString();
                break;
            case MacroCallStep macro:
                RefreshMacroTargetBox(macro.Macro);
                break;
            case PixelWhenStep pixel:
                PixelXBox.Text = pixel.Condition.Coordinate.X.ToString();
                PixelYBox.Text = pixel.Condition.Coordinate.Y.ToString();
                PixelToleranceBox.Text = pixel.Condition.Tolerance.ToString();
                PixelRBox.Text = pixel.Condition.Expected.R.ToString();
                PixelGBox.Text = pixel.Condition.Expected.G.ToString();
                PixelBBox.Text = pixel.Condition.Expected.B.ToString();
                PixelWindowStartBox.Text = pixel.WindowStart is { } start ? FormatNumber(start.TotalMilliseconds) : string.Empty;
                PixelWindowEndBox.Text = pixel.WindowEnd is { } end ? FormatNumber(end.TotalMilliseconds) : string.Empty;
                PixelPollBox.Text = pixel.PollInterval is { } poll ? FormatNumber(poll.TotalMilliseconds) : string.Empty;
                UpdatePixelPreview(pixel.Condition.Expected);
                break;
        }
    }

    private void ApplyStepEdit_Click(object sender, RoutedEventArgs e)
    {
        ApplyCurrentStepEdit();
    }

    public void FocusPrimaryEditor()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!IsVisible || !ApplyStepEditButton.IsEnabled) return;
            if (TextEditPanel.Visibility == Visibility.Visible)
            {
                StepTextBox.Focus();
                StepTextBox.CaretIndex = StepTextBox.Text.Length;
                return;
            }

            var input = FindPrimaryInput(StepEditorFieldsPanel);
            if (input is TextBox textBox) { textBox.Focus(); textBox.SelectAll(); }
            else input?.Focus();
        }, DispatcherPriority.ContextIdle);
    }

    private static Control? FindPrimaryInput(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is UIElement element && (!element.IsVisible || !element.IsEnabled)) continue;
            if (child is ComboBox) continue;
            if (child is TextBox { IsReadOnly: false }) return (Control)child;
            var input = FindPrimaryInput(child);
            if (input is not null) return input;
        }
        return null;
    }

    private void Editor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key != Key.Enter || capturingStepKey || !ApplyStepEditButton.IsEnabled) return;
        // Let an open dropdown commit its highlighted item before confirming the card.
        if (Keyboard.FocusedElement is DependencyObject focused)
        {
            for (var current = focused; current is not null; current = VisualTreeHelper.GetParent(current))
                if (current is ComboBox { IsDropDownOpen: true }) return;
        }
        if (Keyboard.Modifiers == ModifierKeys.Alt)
        {
            if (Keyboard.FocusedElement is TextBox { AcceptsReturn: true } box)
            {
                var caret = box.SelectionStart;
                box.SelectedText = Environment.NewLine;
                box.CaretIndex = caret + Environment.NewLine.Length;
            }
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers == ModifierKeys.None)
        {
            ApplyCurrentStepEdit();
            e.Handled = true;
        }
        else if (Keyboard.FocusedElement is TextBox { AcceptsReturn: true }) e.Handled = true;
    }

    private void ApplyCurrentStepEdit()
    {
        if (updatingEditor) return;
        StepEdited?.Invoke(null!); // signal to MainWindow to apply
    }

    public MacroStep BuildEditedStep(MacroStep current)
    {
        ValidateEffectFields(current);
        return current switch
        {
            KeyStep key => key with
            {
                Key = ParseHidKeyFromText(StepKeyBox.Text),
                Kind = GetComboBoxEnum<KeyActionKind>(ActionKindBox),
                Modifiers = IsModifierHidKey(ParseHidKeyFromText(StepKeyBox.Text))
                    ? HidModifier.None
                    : key.Modifiers,
                Hold = TimeSpan.Zero
            },
            MouseButtonStep button => button with
            {
                Button = GetComboBoxEnum<CoreMouseButton>(MouseButtonBox),
                Kind = GetComboBoxEnum<ButtonActionKind>(ActionKindBox),
                Hold = TimeSpan.Zero,
                CoordinateMode = MouseButtonCoordinateEnabledBox.IsChecked == true
                    ? GetComboBoxEnum<MouseMoveMode>(MouseButtonCoordinateModeBox)
                    : null,
                X = MouseButtonCoordinateEnabledBox.IsChecked == true ? ReadInt(MouseButtonXBox.Text, 0) : null,
                Y = MouseButtonCoordinateEnabledBox.IsChecked == true ? ReadInt(MouseButtonYBox.Text, 0) : null
            },
            MouseMoveStep move => move with
            {
                Mode = GetComboBoxEnum<MouseMoveMode>(MoveModeBox),
                X = ReadInt(MoveXBox.Text, 0),
                Y = ReadInt(MoveYBox.Text, 0),
                Duration = TimeSpan.FromMilliseconds(ReadDouble(TimingMsBox.Text, 0))
            },
            MouseWheelStep wheel => wheel with
            {
                Vertical = ReadInt(WheelVerticalBox.Text, 0),
                Horizontal = ReadInt(WheelHorizontalBox.Text, 0)
            },
            ConsumerStep consumer => consumer with
            {
                Kind = GetComboBoxEnum<ButtonActionKind>(ActionKindBox),
                Hold = TimeSpan.Zero
            },
            WindowActivateStep windowActivate => BuildEditedWindowActivateStep(windowActivate),
            OcrExtractTextStep ocrExtractText => BuildEditedOcrExtractTextStep(ocrExtractText),
            OcrClickStep ocrClick => BuildEditedOcrClickStep(ocrClick),
            WaitStep wait => BuildEditedWaitStep(wait),
            TextStep => new TextStep(StepTextBox.Text),
            CommentStep => new CommentStep(StepTextBox.Text),
            RepeatStep repeat => repeat with { Count = Math.Max(1, ReadInt(LoopCountBox.Text, repeat.Count)) },
            NetLimitStep limit => limit.Action == NetLimitAction.Stop
                ? limit
                : limit with
                {
                    ProcessName = string.IsNullOrWhiteSpace(NetLimitProcessBox.Text) ? limit.ProcessName : NetLimitProcessBox.Text.Trim(),
                    BytesPerSecond = ReadNetLimitBytes(limit.EffectiveBytesPerSecond)
                },
            ClockShiftStep clock => clock.Action == ClockShiftAction.Restore
                ? clock
                : clock with { TimeShift = ReadClockShift(clock.EffectiveTimeShift) },
            WeakNetStep weak => weak.Action == WeakNetAction.Stop
                ? weak
                : weak with
                {
                    ProcessName = string.IsNullOrWhiteSpace(WeakNetProcessBox.Text) ? weak.ProcessName : WeakNetProcessBox.Text.Trim(),
                    Duration = weak.Action == WeakNetAction.Hold
                        ? TimeSpan.FromMilliseconds(Math.Max(0, ReadDouble(WeakNetDurationBox.Text, weak.EffectiveDuration.TotalMilliseconds)))
                        : weak.Duration,
                    TimeShift = ReadWeakNetTimeShift(weak.EffectiveTimeShift)
                },
            YsrFpsToggleStep ysr => ysr with { ToggleFps = ysr.Action == YsrFpsAction.Stop ? ysr.ToggleFps : (int)ReadDouble(FrameLockFpsBox.Text, ysr.ToggleFps) },
            FrameLockStep frame => frame.Action == FrameLockAction.Stop
                ? frame with
                {
                    ProcessName = string.IsNullOrWhiteSpace(FrameLockProcessBox.Text) ? frame.ProcessName : FrameLockProcessBox.Text.Trim()
                }
                : frame with
                {
                    ProcessName = string.IsNullOrWhiteSpace(FrameLockProcessBox.Text) ? frame.ProcessName : FrameLockProcessBox.Text.Trim(),
                    Fps = (int)Math.Clamp(Math.Round(ReadDouble(FrameLockFpsBox.Text, frame.EffectiveFps)), FrameLockStep.MinFps, FrameLockStep.MaxFps)
                },
            ParallelStep parallel => parallel with
            {
                Timeout = string.IsNullOrWhiteSpace(ParallelTimeoutBox.Text)
                    ? null
                    : TimeSpan.FromMilliseconds(Math.Max(0, ReadDouble(ParallelTimeoutBox.Text, 0)))
            },
            TextWhenStep textWhen => textWhen with
            {
                ExpectedText = TextWhenExpectedBox.Text,
                Contains = TextWhenContainsBox.IsChecked != false,
                UseRegex = TextWhenRegexBox.IsChecked == true,
                Region = ScreenRegion.FromRect(
                    ReadInt(TextWhenLeftBox.Text, textWhen.Region.TopLeft.X),
                    ReadInt(TextWhenTopBox.Text, textWhen.Region.TopLeft.Y),
                    ReadInt(TextWhenRightBox.Text, textWhen.Region.BottomRight.X),
                    ReadInt(TextWhenBottomBox.Text, textWhen.Region.BottomRight.Y))
            },
            MacroCallStep => new MacroCallStep(ReadSelectedMacroName()),
            PixelWhenStep pixel => BuildEditedPixelStep(pixel),
            _ => current
        };
    }

    private void ValidateEffectFields(MacroStep step)
    {
        static double Number(string text, string label, double min, double max, bool integer = false)
        {
            if (!double.TryParse(text.Trim(), out var value) || !double.IsFinite(value)
                || value < min || value > max || (integer && value != Math.Truncate(value)))
                throw new InvalidOperationException($"{label}请输入 {min} 到 {max} 之间的{(integer ? "整数" : "数值")}。");
            return value;
        }
        static void Process(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("请填写目标程序名称，例如 Game.exe。");
            if (WeakNetSession.IsDeniedProcess(text)) throw new InvalidOperationException("不能对系统关键进程应用此效果。");
        }
        void Shift(string hours, string minutes)
        {
            var h = Number(hours, "小时", 0, 24, true);
            var m = Number(minutes, "分钟", 0, 59, true);
            if (h * 60 + m > WeakNetStep.MaxTimeShiftMinutes)
                throw new InvalidOperationException("时间偏移不能超过 24 小时。");
        }
        switch (step)
        {
            case NetLimitStep limit when limit.Action != NetLimitAction.Stop:
                Process(NetLimitProcessBox.Text);
                Number(NetLimitBytesBox.Text, "上传限速（字节/秒）", NetLimitStep.MinBytesPerSecond, NetLimitStep.MaxBytesPerSecond, true);
                break;
            case WeakNetStep weak when weak.Action != WeakNetAction.Stop:
                Process(WeakNetProcessBox.Text);
                Shift(WeakNetShiftHoursBox.Text, WeakNetShiftMinutesBox.Text);
                if (weak.Action == WeakNetAction.Hold) Number(WeakNetDurationBox.Text, "持续时间（毫秒）", 0, TimeSpan.MaxValue.TotalMilliseconds);
                break;
            case ClockShiftStep clock when clock.Action != ClockShiftAction.Restore:
                Shift(ClockShiftHoursBox.Text, ClockShiftMinutesBox.Text);
                break;
            case YsrFpsToggleStep ysr:
                if (ysr.Action != YsrFpsAction.Stop) Number(FrameLockFpsBox.Text, "目标帧率", ysr.Backend != FpsBackend.Ysr ? 0 : 1, 1000, true);
                break;
            case FrameLockStep frame:
                Process(FrameLockProcessBox.Text);
                if (frame.Action != FrameLockAction.Stop) Number(FrameLockFpsBox.Text, "目标帧率", FrameLockStep.MinFps, FrameLockStep.MaxFps, true);
                break;
        }
    }

    private WindowActivateStep BuildEditedWindowActivateStep(WindowActivateStep current)
    {
        if (WindowTitleRegexBox.IsChecked == true
            && !WindowActivationService.IsValidTitleRegex(WindowTitleBox.Text, out var regexError))
        {
            throw new InvalidOperationException($"{L("InvalidWindowTitleRegex")}: {regexError}");
        }

        var processName = WindowProcessBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(processName))
        {
            throw new InvalidOperationException(L("WindowProcessRequired"));
        }

        return current with
        {
            ProcessName = processName,
            WindowTitle = WindowTitleBox.Text.Trim(),
            UseTitleRegex = WindowTitleRegexBox.IsChecked == true,
            MatchIndex = Math.Max(1, ReadInt(WindowMatchIndexBox.Text, current.MatchIndex)),
            Timeout = TimeSpan.FromMilliseconds(Math.Max(
                0,
                ReadDouble(
                    WindowTimeoutMsBox.Text,
                    current.Timeout > TimeSpan.Zero ? current.Timeout.TotalMilliseconds : 3000))),
            Restore = WindowRestoreBox.IsChecked != false,
            FailIfNotFound = WindowFailIfNotFoundBox.IsChecked != false
        };
    }

    private OcrClickStep BuildEditedOcrClickStep(OcrClickStep current)
    {
        if (OcrClickRegexBox.IsChecked == true
            && !PaddleOcrBridge.IsValidRegex(OcrClickExpectedTextBox.Text, out var regexError))
        {
            throw new InvalidOperationException($"{L("InvalidRegex")}: {regexError}");
        }

        return current with
        {
            Region = ocrClickRegion,
            ExpectedText = OcrClickExpectedTextBox.Text.Trim(),
            Contains = OcrClickContainsBox.IsChecked != false,
            Language = ReadComboValue(OcrClickLanguageBox, "ch"),
            UseRegex = OcrClickRegexBox.IsChecked == true,
            Button = GetComboBoxEnum<CoreMouseButton>(OcrClickMouseButtonBox),
            ClickCount = Math.Clamp(ReadInt(OcrClickCountBox.Text, current.ClickCount), 1, 3),
            MatchIndex = Math.Max(1, ReadInt(OcrClickMatchIndexBox.Text, current.MatchIndex)),
            Hold = TimeSpan.FromMilliseconds(Math.Max(0, ReadDouble(OcrClickHoldMsBox.Text, current.Hold.TotalMilliseconds))),
            Interval = TimeSpan.FromMilliseconds(Math.Max(0, ReadDouble(OcrClickIntervalMsBox.Text, current.Interval.TotalMilliseconds))),
            OffsetX = ReadInt(OcrClickOffsetXBox.Text, current.OffsetX),
            OffsetY = ReadInt(OcrClickOffsetYBox.Text, current.OffsetY)
        };
    }

    private OcrExtractTextStep BuildEditedOcrExtractTextStep(OcrExtractTextStep current)
    {
        if (!ValidateOcrExtractRegex())
        {
            throw new InvalidOperationException(L("InvalidRegex"));
        }

        return current with
        {
            Region = ocrExtractRegion,
            Pattern = OcrExtractPatternBox.Text.Trim(),
            Language = ReadComboValue(OcrExtractLanguageBox, "ch"),
            UseRegex = OcrExtractRegexBox.IsChecked == true,
            MatchIndex = Math.Max(1, ReadInt(OcrExtractMatchIndexBox.Text, current.MatchIndex)),
            CaptureGroup = Math.Max(0, ReadInt(OcrExtractCaptureGroupBox.Text, current.CaptureGroup)),
            FilterTerms = OcrExtractFilterTermsBox.Text.Trim(),
            KeepDigitsOnly = OcrExtractKeepDigitsOnlyBox.IsChecked == true,
            NormalizeWhitespace = OcrExtractNormalizeWhitespaceBox.IsChecked != false,
            FailIfNotFound = OcrExtractFailIfNotFoundBox.IsChecked != false
        };
    }

    private PixelWhenStep BuildEditedPixelStep(PixelWhenStep current)
    {
        var color = new RgbColor(
            checked((byte)Math.Clamp(ReadInt(PixelRBox.Text, current.Condition.Expected.R), 0, 255)),
            checked((byte)Math.Clamp(ReadInt(PixelGBox.Text, current.Condition.Expected.G), 0, 255)),
            checked((byte)Math.Clamp(ReadInt(PixelBBox.Text, current.Condition.Expected.B), 0, 255)));
        var condition = new PixelCondition(
            new PixelCoordinate(
                current.Condition.Coordinate.Scope,
                ReadInt(PixelXBox.Text, current.Condition.Coordinate.X),
                ReadInt(PixelYBox.Text, current.Condition.Coordinate.Y),
                current.Condition.Coordinate.WindowTitle),
            color,
            checked((byte)Math.Clamp(ReadInt(PixelToleranceBox.Text, current.Condition.Tolerance), 0, 255)));
        UpdatePixelPreview(color);
        return current with
        {
            Condition = condition,
            WindowStart = ReadOptionalTimeSpan(PixelWindowStartBox.Text),
            WindowEnd = ReadOptionalTimeSpan(PixelWindowEndBox.Text),
            PollInterval = ReadOptionalTimeSpan(PixelPollBox.Text)
        };
    }

    private void CaptureStepKey_Click(object sender, RoutedEventArgs e)
    {
        if (capturingStepKey) { StopStepKeyCapture(); return; }
        capturingStepKey = true;
        AnyKeyCaptureStarted?.Invoke();
        AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(CaptureStepKey_KeyDown), true);
    }

    private void CaptureStepKey_KeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (GlobalKeyboardHook.TryMapVirtualKeyToHidKey(virtualKey, out var hidKey))
            StepKeyBox.Text = hidKey.ToString();

        StopStepKeyCapture();
        e.Handled = true;
    }

    private void StopStepKeyCapture()
    {
        if (!capturingStepKey) return;
        capturingStepKey = false;
        RemoveHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(CaptureStepKey_KeyDown));
        AnyKeyCaptureFinished?.Invoke();
    }

    private async void PickPixelColor_Click(object sender, RoutedEventArgs e)
    {
        CoordinatePickerStarted?.Invoke(this, EventArgs.Empty);
        try
        {
            var result = await ScreenPixelSampler.PickScreenPixelAsync(Window.GetWindow(this));
            if (!result.Ok) return;
            PixelXBox.Text = result.X.ToString();
            PixelYBox.Text = result.Y.ToString();
            PixelRBox.Text = result.Color.R.ToString();
            PixelGBox.Text = result.Color.G.ToString();
            PixelBBox.Text = result.Color.B.ToString();
            UpdatePixelPreview(result.Color);
        }
        finally
        {
            CoordinatePickerFinished?.Invoke(this, EventArgs.Empty);
        }
    }

    private void PickMouseButtonCoordinate_Click(object sender, RoutedEventArgs e)
    {
        CoordinatePickerStarted?.Invoke(this, EventArgs.Empty);
        try
        {
            var picker = new ScreenCoordinatePickerWindow();
            if (DialogOwnerService.ShowDialogSafe(picker, this) == true)
            {
                MouseButtonCoordinateEnabledBox.IsChecked = true;
                SetComboBox(MouseButtonCoordinateModeBox, MouseMoveMode.Absolute.ToString());
                MouseButtonXBox.Text = picker.SelectedX.ToString();
                MouseButtonYBox.Text = picker.SelectedY.ToString();
            }
        }
        finally
        {
            CoordinatePickerFinished?.Invoke(this, EventArgs.Empty);
        }
    }

    private void PickOcrClickRegion_Click(object sender, RoutedEventArgs e)
    {
        CoordinatePickerStarted?.Invoke(this, EventArgs.Empty);
        try
        {
            var selected = ScreenRegionPicker.PickRegion(Window.GetWindow(this));
            if (selected is null) return;
            ocrClickRegion = selected;
            UpdateOcrClickRegionInfo();
        }
        finally
        {
            CoordinatePickerFinished?.Invoke(this, EventArgs.Empty);
        }
    }

    private void PickOcrExtractRegion_Click(object sender, RoutedEventArgs e)
    {
        CoordinatePickerStarted?.Invoke(this, EventArgs.Empty);
        try
        {
            var selected = ScreenRegionPicker.PickRegion(Window.GetWindow(this));
            if (selected is null) return;
            ocrExtractRegion = selected;
            UpdateOcrExtractRegionInfo();
        }
        finally
        {
            CoordinatePickerFinished?.Invoke(this, EventArgs.Empty);
        }
    }

    private void PickWindowTarget_Click(object sender, RoutedEventArgs e)
    {
        CoordinatePickerStarted?.Invoke(this, EventArgs.Empty);
        try
        {
            var picker = new WindowTargetPickerDialog();
            if (DialogOwnerService.ShowDialogSafe(picker, this) != true
                || picker.SelectedTarget is not { } target)
            {
                return;
            }

            WindowProcessBox.Text = target.ProcessName;
            WindowTitleBox.Text = target.WindowTitle;
            WindowTitleRegexBox.IsChecked = false;
            WindowMatchIndexBox.Text = "1";
            WindowActivateStatusText.Text = string.Format(
                L("WindowSelected"),
                target.ProcessName,
                target.WindowTitle);
        }
        finally
        {
            CoordinatePickerFinished?.Invoke(this, EventArgs.Empty);
        }
    }

    private void WindowTitleRegexBox_Changed(object sender, RoutedEventArgs e)
    {
        ValidateWindowTitleRegex();
    }

    private void WindowTitleBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ValidateWindowTitleRegex();
    }

    private bool ValidateWindowTitleRegex()
    {
        var valid = WindowTitleRegexBox.IsChecked != true
            || WindowActivationService.IsValidTitleRegex(WindowTitleBox.Text, out _);
        WindowTitleRegexErrorText.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
        WindowTitleBox.BorderBrush = valid ? null : Brushes.Red;
        return valid;
    }

    private async void TestOcrClick_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateOcrClickRegex()) return;

        TestOcrClickButton.IsEnabled = false;
        OcrClickStatusText.Text = L("OcrClickTesting");
        try
        {
            using var bridge = new PaddleOcrBridge();
            var location = await bridge.FindTextAsync(
                ocrClickRegion,
                OcrClickExpectedTextBox.Text.Trim(),
                OcrClickContainsBox.IsChecked != false,
                ReadComboValue(OcrClickLanguageBox, "ch"),
                OcrClickRegexBox.IsChecked == true,
                Math.Max(1, ReadInt(OcrClickMatchIndexBox.Text, 1)),
                ReadInt(OcrClickOffsetXBox.Text, 0),
                ReadInt(OcrClickOffsetYBox.Text, 0));
            OcrClickStatusText.Text = location is null
                ? L("OcrClickNotFound")
                : string.Format(L("OcrClickFound"), location.Text, location.X, location.Y);
        }
        catch (Exception ex)
        {
            OcrClickStatusText.Text = $"{L("OcrClickNotFound")}: {ex.Message}";
        }
        finally
        {
            TestOcrClickButton.IsEnabled = true;
        }
    }

    private async void TestOcrExtract_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateOcrExtractRegex()) return;

        TestOcrExtractButton.IsEnabled = false;
        OcrExtractStatusText.Text = L("OcrExtractTesting");
        try
        {
            using var bridge = new PaddleOcrBridge();
            var recognition = await bridge.RecognizeWithDiagnosticsAsync(
                ocrExtractRegion,
                ReadComboValue(OcrExtractLanguageBox, "ch"));
            if (!recognition.Success)
            {
                OcrExtractStatusText.Text = $"{L("OcrExtractNotFound")}: {recognition.Error}";
                return;
            }

            var extracted = PaddleOcrBridge.TryExtractText(
                recognition.Text,
                OcrExtractPatternBox.Text.Trim(),
                OcrExtractRegexBox.IsChecked == true,
                Math.Max(1, ReadInt(OcrExtractMatchIndexBox.Text, 1)),
                Math.Max(0, ReadInt(OcrExtractCaptureGroupBox.Text, 0)),
                OcrExtractFilterTermsBox.Text,
                OcrExtractKeepDigitsOnlyBox.IsChecked == true,
                OcrExtractNormalizeWhitespaceBox.IsChecked != false,
                out var value,
                out var error);
            OcrExtractStatusText.Text = extracted
                ? string.Format(L("OcrExtractFound"), value)
                : $"{L("OcrExtractNotFound")}: {error}";
        }
        catch (Exception ex)
        {
            OcrExtractStatusText.Text = $"{L("OcrExtractNotFound")}: {ex.Message}";
        }
        finally
        {
            TestOcrExtractButton.IsEnabled = true;
        }
    }

    private void OcrExtractRegexBox_Changed(object sender, RoutedEventArgs e)
    {
        if (OcrExtractRegexBox is null
            || OcrExtractCaptureGroupBox is null
            || OcrExtractPatternBox is null
            || OcrExtractRegexErrorText is null)
        {
            return;
        }

        OcrExtractCaptureGroupBox.IsEnabled = OcrExtractRegexBox.IsChecked == true;
        ValidateOcrExtractRegex();
    }

    private void OcrExtractPatternBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!updatingEditor && !applyingOcrExtractPreset)
        {
            SelectOcrExtractPresetItem("Custom");
        }
        ValidateOcrExtractRegex();
    }

    private void OcrExtractPresetBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingEditor
            || applyingOcrExtractPreset
            || OcrExtractPresetBox.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        var preset = item.Tag?.ToString();
        if (string.IsNullOrWhiteSpace(preset) || string.Equals(preset, "Custom", StringComparison.Ordinal))
        {
            return;
        }

        applyingOcrExtractPreset = true;
        try
        {
            OcrExtractRegexBox.IsChecked = true;
            OcrExtractFilterTermsBox.Text = string.Empty;
            OcrExtractKeepDigitsOnlyBox.IsChecked = false;
            (OcrExtractPatternBox.Text, OcrExtractCaptureGroupBox.Text) = preset switch
            {
                "AllText" => (string.Empty, "0"),
                "Digits" => (@"\d+", "0"),
                "FilterDigits" => (string.Empty, "0"),
                "AlphaNumeric" => (@"[A-Za-z0-9_-]+", "0"),
                "Uid" => (@"(?i)UID\s*[:：=]?\s*([^\s，。！？,;；]+)", "1"),
                "AfterSeparator" => (@"[:：=]\s*([^\s，。！？,;；]+)", "1"),
                _ => (OcrExtractPatternBox.Text, OcrExtractCaptureGroupBox.Text)
            };
            if (string.Equals(preset, "FilterDigits", StringComparison.Ordinal))
            {
                OcrExtractFilterTermsBox.Text = $"-6{Environment.NewLine}4=3";
                OcrExtractKeepDigitsOnlyBox.IsChecked = true;
            }
            ValidateOcrExtractRegex();
        }
        finally
        {
            applyingOcrExtractPreset = false;
        }
    }

    private void SelectOcrExtractPreset(
        string pattern,
        int captureGroup,
        string filterTerms,
        bool keepDigitsOnly)
    {
        var normalizedFilters = filterTerms.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        var preset = (pattern, captureGroup, normalizedFilters, keepDigitsOnly) switch
        {
            ("", 0, "-6\n4=3", true) => "FilterDigits",
            ("", 0, "", false) => "AllText",
            (@"\d+", 0, "", false) => "Digits",
            (@"[A-Za-z0-9_-]+", 0, "", false) => "AlphaNumeric",
            (@"(?i)UID\s*[:：=]?\s*([^\s，。！？,;；]+)", 1, "", false) => "Uid",
            (@"[:：=]\s*([^\s，。！？,;；]+)", 1, "", false) => "AfterSeparator",
            _ => "Custom"
        };
        SelectOcrExtractPresetItem(preset);
    }

    private void OcrExtractFilterTermsBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        MarkOcrExtractPresetCustom();
    }

    private void OcrExtractKeepDigitsOnlyBox_Changed(object sender, RoutedEventArgs e)
    {
        MarkOcrExtractPresetCustom();
    }

    private void MarkOcrExtractPresetCustom()
    {
        if (!updatingEditor && !applyingOcrExtractPreset)
        {
            SelectOcrExtractPresetItem("Custom");
        }
    }

    private void SelectOcrExtractPresetItem(string preset)
    {
        foreach (var item in OcrExtractPresetBox.Items.OfType<ComboBoxItem>())
        {
            if (!string.Equals(item.Tag?.ToString(), preset, StringComparison.Ordinal))
            {
                continue;
            }

            OcrExtractPresetBox.SelectedItem = item;
            return;
        }
    }

    private bool ValidateOcrExtractRegex()
    {
        var pattern = OcrExtractPatternBox.Text;
        var valid = OcrExtractRegexBox.IsChecked != true
            || string.IsNullOrWhiteSpace(pattern)
            || PaddleOcrBridge.IsValidRegex(pattern, out _);
        OcrExtractRegexErrorText.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
        OcrExtractPatternBox.BorderBrush = valid ? null : Brushes.Red;
        return valid;
    }

    private void UpdateOcrExtractRegionInfo()
    {
        OcrExtractRegionInfoText.Text =
            $"({ocrExtractRegion.TopLeft.X},{ocrExtractRegion.TopLeft.Y}) – ({ocrExtractRegion.BottomRight.X},{ocrExtractRegion.BottomRight.Y})";
    }

    private void OcrClickRegexBox_Changed(object sender, RoutedEventArgs e)
    {
        OcrClickContainsBox.IsEnabled = OcrClickRegexBox.IsChecked != true;
        ValidateOcrClickRegex();
    }

    private void OcrClickExpectedTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ValidateOcrClickRegex();
    }

    private bool ValidateOcrClickRegex()
    {
        var valid = OcrClickRegexBox.IsChecked != true
            || PaddleOcrBridge.IsValidRegex(OcrClickExpectedTextBox.Text, out _);
        OcrClickRegexErrorText.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
        OcrClickExpectedTextBox.BorderBrush = valid ? null : Brushes.Red;
        return valid;
    }

    private void UpdateOcrClickRegionInfo()
    {
        OcrClickRegionInfoText.Text =
            $"({ocrClickRegion.TopLeft.X},{ocrClickRegion.TopLeft.Y}) – ({ocrClickRegion.BottomRight.X},{ocrClickRegion.BottomRight.Y})";
    }

    private WaitStep BuildEditedWaitStep(WaitStep current)
    {
        var mode = (DelayModeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Fixed";
        if (string.Equals(mode, "Random", StringComparison.OrdinalIgnoreCase))
        {
            var min = Math.Max(0, ReadDouble(DelayMinMsBox.Text, current.MinDuration.TotalMilliseconds));
            var max = Math.Max(0, ReadDouble(DelayMaxMsBox.Text, current.MaxDuration?.TotalMilliseconds ?? min));
            if (max < min)
            {
                max = min;
            }

            return new WaitStep(TimeSpan.FromMilliseconds(min), TimeSpan.FromMilliseconds(max));
        }

        return new WaitStep(TimeSpan.FromMilliseconds(Math.Max(0, ReadDouble(DelayMsBox.Text, current.Duration.TotalMilliseconds))));
    }

    private void PopulateDelayEditor(WaitStep wait)
    {
        SetDelayMode(wait.IsRandom ? "Random" : "Fixed");
        DelayMsBox.Text = FormatNumber(wait.Duration.TotalMilliseconds);
        DelayMinMsBox.Text = FormatNumber(wait.MinDuration.TotalMilliseconds);
        DelayMaxMsBox.Text = FormatNumber((wait.MaxDuration ?? wait.Duration).TotalMilliseconds);
    }

    private void SetDelayMode(string mode)
    {
        foreach (var item in DelayModeBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), mode, StringComparison.OrdinalIgnoreCase))
            {
                DelayModeBox.SelectedItem = item;
                break;
            }
        }

        var random = string.Equals(mode, "Random", StringComparison.OrdinalIgnoreCase);
        DelayFixedPanel.Visibility = random ? Visibility.Collapsed : Visibility.Visible;
        DelayRangePanel.Visibility = random ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DelayModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DelayModeBox.SelectedItem is ComboBoxItem item)
        {
            SetDelayMode(item.Tag?.ToString() ?? "Fixed");
        }
    }

    private void SetEditorPanels(bool keyboard, bool action, bool mouseButton, bool mouseMove, bool wheel, bool windowActivate, bool ocrExtractText, bool ocrClick, bool timing, bool delay, bool text, bool loop, bool macro, bool pixel, bool weakNet = false, bool parallel = false, bool textWhen = false, bool frameLock = false, bool netLimit = false, bool clockShift = false)
    {
        KeyboardEditPanel.Visibility = ToVis(keyboard);
        ButtonEditPanel.Visibility = ToVis(action);
        MouseButtonEditPanel.Visibility = ToVis(mouseButton);
        MouseMoveEditPanel.Visibility = ToVis(mouseMove);
        WheelEditPanel.Visibility = ToVis(wheel);
        WindowActivateEditPanel.Visibility = ToVis(windowActivate);
        OcrExtractTextEditPanel.Visibility = ToVis(ocrExtractText);
        OcrClickEditPanel.Visibility = ToVis(ocrClick);
        TimingEditPanel.Visibility = ToVis(timing && !delay);
        DelayEditPanel.Visibility = ToVis(delay);
        TextEditPanel.Visibility = ToVis(text);
        LoopEditPanel.Visibility = ToVis(loop);
        MacroEditPanel.Visibility = ToVis(macro);
        PixelEditPanel.Visibility = ToVis(pixel);
        NetLimitEditPanel.Visibility = ToVis(netLimit);
        ClockShiftEditPanel.Visibility = ToVis(clockShift);
        WeakNetEditPanel.Visibility = ToVis(weakNet);
        FrameLockEditPanel.Visibility = ToVis(frameLock);
        ParallelEditPanel.Visibility = ToVis(parallel);
        TextWhenEditPanel.Visibility = ToVis(textWhen);
    }

    private void ShowClockShiftFields(ClockShiftAction action)
    {
        var restore = action == ClockShiftAction.Restore;
        ClockRestoreHintText.Visibility = restore ? Visibility.Visible : Visibility.Collapsed;
        ClockShiftRow.Visibility = restore ? Visibility.Collapsed : Visibility.Visible;
        ClockShiftHintText.Visibility = restore ? Visibility.Collapsed : Visibility.Visible;
    }

    private TimeSpan ReadClockShift(TimeSpan fallback)
    {
        var hours = (int)Math.Clamp(Math.Round(ReadDouble(ClockShiftHoursBox.Text, fallback.TotalHours)), 0, 24);
        var minutes = (int)Math.Clamp(Math.Round(ReadDouble(ClockShiftMinutesBox.Text, fallback.Minutes)), 0, 59);
        var total = hours * 60 + minutes;
        if (total > WeakNetStep.MaxTimeShiftMinutes)
        {
            total = WeakNetStep.MaxTimeShiftMinutes;
        }

        return TimeSpan.FromMinutes(total);
    }

    private void ShowNetLimitFields(NetLimitAction action)
    {
        var stop = action == NetLimitAction.Stop;
        NetLimitStopHintText.Visibility = stop ? Visibility.Visible : Visibility.Collapsed;
        NetLimitProcessLabelText.Visibility = stop ? Visibility.Collapsed : Visibility.Visible;
        NetLimitProcessBox.Visibility = stop ? Visibility.Collapsed : Visibility.Visible;
        NetLimitBytesLabelText.Visibility = stop ? Visibility.Collapsed : Visibility.Visible;
        NetLimitBytesBox.Visibility = stop ? Visibility.Collapsed : Visibility.Visible;
        NetLimitHintText.Visibility = stop ? Visibility.Collapsed : Visibility.Visible;
    }

    private long ReadNetLimitBytes(long fallback)
    {
        var value = (long)Math.Round(ReadDouble(NetLimitBytesBox.Text, fallback));
        return NetLimitStep.ClampBytesPerSecond(value);
    }

    private void ShowWeakNetFields(WeakNetAction action)
    {
        var stop = action == WeakNetAction.Stop;
        var hold = action == WeakNetAction.Hold;
        WeakNetStopHintText.Visibility = stop ? Visibility.Visible : Visibility.Collapsed;
        WeakNetProcessLabelText.Visibility = stop ? Visibility.Collapsed : Visibility.Visible;
        WeakNetProcessBox.Visibility = stop ? Visibility.Collapsed : Visibility.Visible;
        WeakNetDurationLabelText.Visibility = hold ? Visibility.Visible : Visibility.Collapsed;
        WeakNetDurationBox.Visibility = hold ? Visibility.Visible : Visibility.Collapsed;
        WeakNetShiftRow.Visibility = stop ? Visibility.Collapsed : Visibility.Visible;
        WeakNetShiftHintText.Visibility = stop ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowFrameLockFields(FrameLockAction action, bool ysr = false, FpsBackend backend = FpsBackend.Ysr)
    {
        FrameLockProcessLabelText.Visibility = FrameLockProcessBox.Visibility = ysr ? Visibility.Collapsed : Visibility.Visible;
        FrameLockHintText.Text = ysr
            ? (YsrFpsSession.TryReadCurrent(out var current) ? $"YSR 当前帧率：{current} FPS。" : "YSR 未连接。") + "目标帧率 1–1000；关闭或播放结束时恢复开启前的帧率。需先启动解帧器，不需要运行 fps_toggle.py。"
            : L("FrameLockHint");
        if (ysr && backend != FpsBackend.Ysr)
        {
            int unlockerCurrent;
            bool connected = backend == FpsBackend.Fufu
                ? FufuFpsSession.TryReadCurrent(out unlockerCurrent)
                : GameMemoryFpsSession.TryReadCurrent(out unlockerCurrent);
            FrameLockHintText.Text = (connected ? $"{backend} 配置目标：{unlockerCurrent} FPS。" : $"请在顶部「解帧器设置」检查 {backend} 路径、版本和解帧开关。")
                + "0 表示 999 FPS；结束时恢复原配置目标。写入后等待解帧器应用，不代表实测 FPS 已达到。"
                + (backend == FpsBackend.Fufu ? "Fufu 原版配置加载约 100–200 毫秒，需进入游戏世界验证。" : "KY3 补丁检查间隔为 20 毫秒；原版备用模式约 2 秒。请在设置中确认模式，实际响应延迟需实测。短时间切换可能合并。");
        }
        var stop = action == FrameLockAction.Stop;
        FrameLockFpsLabelText.Visibility = stop ? Visibility.Collapsed : Visibility.Visible;
        FrameLockFpsBox.Visibility = stop ? Visibility.Collapsed : Visibility.Visible;
    }

    private TimeSpan ReadWeakNetTimeShift(TimeSpan fallback)
    {
        var hours = (int)Math.Clamp(Math.Round(ReadDouble(WeakNetShiftHoursBox.Text, fallback.TotalHours)), 0, 24);
        var minutes = (int)Math.Clamp(Math.Round(ReadDouble(WeakNetShiftMinutesBox.Text, fallback.Minutes)), 0, 59);
        var total = hours * 60 + minutes;
        if (total > WeakNetStep.MaxTimeShiftMinutes)
        {
            total = WeakNetStep.MaxTimeShiftMinutes;
        }

        return TimeSpan.FromMinutes(total);
    }

    private string ReadSelectedMacroName()
    {
        return (MacroTargetBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? MacroTargetBox.Text.Trim();
    }

    private static string ReadComboValue(ComboBox comboBox, string fallback)
    {
        if (comboBox.SelectedItem is ComboBoxItem item && item.Tag is not null)
        {
            return item.Tag.ToString() ?? fallback;
        }

        return string.IsNullOrWhiteSpace(comboBox.Text) ? fallback : comboBox.Text.Trim();
    }

    private void UpdatePixelPreview(RgbColor color)
    {
        PixelColorPreview.Background = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
    }

    private static bool TryPickScreenPixel(out int x, out int y, out RgbColor color)
    {
        x = 0; y = 0; color = new RgbColor(0, 0, 0);
        if (!TryGetCursorPosition(out x, out y)) return false;
        var dc = GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) return false;
        try
        {
            var pixel = GetPixel(dc, x, y);
            if (pixel == 0xFFFF_FFFF) return false;
            color = new RgbColor((byte)(pixel & 0xFF), (byte)((pixel >> 8) & 0xFF), (byte)((pixel >> 16) & 0xFF));
            return true;
        }
        finally { _ = ReleaseDC(IntPtr.Zero, dc); }
    }

    private static bool TryGetCursorPosition(out int x, out int y)
    {
        x = 0;
        y = 0;
        if (!GetCursorPos(out var point)) return false;
        x = point.X;
        y = point.Y;
        return true;
    }

    private static Visibility ToVis(bool v) => v ? Visibility.Visible : Visibility.Collapsed;

    private static bool IsModifierHidKey(HidKey key) =>
        key is HidKey.LeftControl or HidKey.RightControl
            or HidKey.LeftShift or HidKey.RightShift
            or HidKey.LeftAlt or HidKey.RightAlt
            or HidKey.LeftGui or HidKey.RightGui;

    private static void FillEnumBox<TEnum>(ComboBox comboBox, TEnum selected) where TEnum : struct, Enum
    {
        comboBox.Items.Clear();
        foreach (var value in Enum.GetValues<TEnum>())
        {
            if (value.ToString() == "None") continue;
            if (typeof(TEnum) == typeof(ButtonActionKind) && value.ToString() == nameof(ButtonActionKind.Click)) continue;
            var item = new ComboBoxItem { Tag = value, Content = GetEnumLabel(value) };
            comboBox.Items.Add(item);
            if (EqualityComparer<TEnum>.Default.Equals(value, selected))
                comboBox.SelectedItem = item;
        }
    }

    private static void RefreshEnumBoxLocalization(ComboBox comboBox)
    {
        foreach (var item in comboBox.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is not null)
            {
                item.Content = GetEnumLabel(item.Tag);
                if (ReferenceEquals(comboBox.SelectedItem, item))
                {
                    comboBox.Text = item.Content?.ToString() ?? string.Empty;
                }
            }
        }
    }

    private static string GetEnumLabel(object value)
    {
        return value switch
        {
            MouseMoveMode.Relative => L("MoveModeRelative"),
            MouseMoveMode.Absolute => L("MoveModeAbsolute"),
            KeyActionKind.Down or ButtonActionKind.Down => L("ActionKindDown"),
            KeyActionKind.Up or ButtonActionKind.Up => L("ActionKindUp"),
            KeyActionKind.Tap => L("ActionKindTap"),
            ButtonActionKind.Click => L("ActionKindClick"),
            _ => value.ToString() ?? string.Empty
        };
    }

    private static void SetComboBox(ComboBox comboBox, string value)
    {
        foreach (var item in comboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), value, StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.Content?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = item;
                comboBox.Text = item.Content?.ToString() ?? value;
                return;
            }
        }

        comboBox.SelectedItem = null;
        comboBox.Text = value;
    }

    private static TEnum GetComboBoxEnum<TEnum>(ComboBox comboBox) where TEnum : struct, Enum
    {
        if (comboBox.SelectedItem is ComboBoxItem { Tag: Enum tag }
            && Enum.TryParse<TEnum>(tag.ToString(), ignoreCase: true, out var fromTag))
        {
            return fromTag;
        }

        return ResolveComboBoxEnumFromText<TEnum>(comboBox) ?? Enum.GetValues<TEnum>()[0];
    }

    private static TEnum? ResolveComboBoxEnumFromText<TEnum>(ComboBox comboBox) where TEnum : struct, Enum
    {
        var text = comboBox.Text.Trim();
        foreach (var item in comboBox.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is TEnum value
                && (string.Equals(item.Tag.ToString(), text, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item.Content?.ToString(), text, StringComparison.OrdinalIgnoreCase)))
            {
                return value;
            }
        }

        return Enum.TryParse<TEnum>(text, ignoreCase: true, out var parsed) ? parsed : null;
    }

    private static HidKey ParseHidKeyFromText(string value)
    {
        var text = value.Trim();
        if (text.Length == 1 && char.IsDigit(text[0])) text = $"D{text}";
        if (!Enum.TryParse<HidKey>(text, ignoreCase: true, out var key) || key == HidKey.None)
            throw new InvalidOperationException($"Unsupported key '{value}'.");
        return key;
    }

    private static int ReadInt(string value, int defaultValue) => int.TryParse(value.Trim(), out var p) ? p : defaultValue;
    private static double ReadDouble(string value, double defaultValue) => double.TryParse(value.Trim(), out var p) ? p : defaultValue;
    private static TimeSpan? ReadOptionalTimeSpan(string value) => string.IsNullOrWhiteSpace(value) ? null : TimeSpan.FromMilliseconds(ReadDouble(value, 0));
    private static string FormatNumber(double value) => Math.Abs(value - Math.Round(value)) < 0.0001 ? ((int)Math.Round(value)).ToString() : value.ToString("0.####");
    private static string L(string key) => LocalizationService.Get(key);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern uint GetPixel(IntPtr hdc, int nXPos, int nYPos);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
}
