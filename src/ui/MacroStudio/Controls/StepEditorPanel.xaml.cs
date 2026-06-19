using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
        StepEditorFieldsPanel.Visibility = Visibility.Collapsed;
        ApplyStepEditButton.IsEnabled = false;
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
        TimingLabelText.Text = L("TimingMs");
        DelayModeLabelText.Text = L("DelayMode");
        DelayFixedLabelText.Text = L("TimingMs");
        DelayMinLabelText.Text = L("DelayMinMs");
        DelayMaxLabelText.Text = L("DelayMaxMs");
        TextActionLabelText.Text = L("AddText");
        LoopCountLabelText.Text = L("LoopCount");
        PixelLabelText.Text = L("PixelIf");
        PickPixelColorButton.Content = L("PickColor");
        RefreshEnumBoxLocalization(ActionKindBox);
        RefreshEnumBoxLocalization(MouseButtonBox);
        RefreshEnumBoxLocalization(MouseButtonCoordinateModeBox);
        RefreshEnumBoxLocalization(MoveModeBox);
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
        if (step is null)
        {
            StepEditorFieldsPanel.Visibility = Visibility.Collapsed;
            ApplyStepEditButton.IsEnabled = false;
            StepEditorHintText.Text = L("StepPropertiesHint");
            return;
        }

        updatingEditor = true;
        PopulateStepEditor(step);
        updatingEditor = false;
        StepEditorFieldsPanel.Visibility = Visibility.Visible;
        ApplyStepEditButton.IsEnabled = true;
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
            timing: step is KeyStep or MouseButtonStep or ConsumerStep or MouseMoveStep or WaitStep,
            delay: step is WaitStep,
            text: step is TextStep,
            loop: step is RepeatStep,
            macro: step is MacroCallStep,
            pixel: step is PixelWhenStep);

        switch (step)
        {
            case KeyStep key:
                StepKeyBox.Text = key.Key.ToString();
                SetModifierBoxes(key.Modifiers);
                SetComboBox(ActionKindBox, key.Kind.ToString());
                TimingMsBox.Text = FormatNumber(key.Hold.TotalMilliseconds);
                break;
            case MouseButtonStep button:
                SetComboBox(MouseButtonBox, button.Button.ToString());
                SetComboBox(ActionKindBox, button.Kind.ToString());
                TimingMsBox.Text = FormatNumber(button.Hold.TotalMilliseconds);
                MouseButtonCoordinateEnabledBox.IsChecked = button.HasCoordinate;
                SetComboBox(MouseButtonCoordinateModeBox, (button.CoordinateMode ?? MouseMoveMode.Absolute).ToString());
                MouseButtonXBox.Text = button.X?.ToString() ?? string.Empty;
                MouseButtonYBox.Text = button.Y?.ToString() ?? string.Empty;
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
            case WaitStep wait:
                PopulateDelayEditor(wait);
                break;
            case TextStep text:
                StepTextBox.Text = text.Text;
                break;
            case RepeatStep repeat:
                LoopCountBox.Text = repeat.Count.ToString();
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
        if (updatingEditor) return;
        StepEdited?.Invoke(null!); // signal to MainWindow to apply
    }

    public MacroStep BuildEditedStep(MacroStep current)
    {
        return current switch
        {
            KeyStep key => key with
            {
                Key = ParseHidKeyFromText(StepKeyBox.Text),
                Kind = GetComboBoxEnum<KeyActionKind>(ActionKindBox),
                Modifiers = ReadStepModifiers(),
                Hold = TimeSpan.FromMilliseconds(ReadDouble(TimingMsBox.Text, 0))
            },
            MouseButtonStep button => button with
            {
                Button = GetComboBoxEnum<CoreMouseButton>(MouseButtonBox),
                Kind = GetComboBoxEnum<ButtonActionKind>(ActionKindBox),
                Hold = TimeSpan.FromMilliseconds(ReadDouble(TimingMsBox.Text, 0)),
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
            WaitStep wait => BuildEditedWaitStep(wait),
            TextStep => new TextStep(StepTextBox.Text),
            RepeatStep repeat => repeat with { Count = Math.Max(1, ReadInt(LoopCountBox.Text, repeat.Count)) },
            MacroCallStep => new MacroCallStep(ReadSelectedMacroName()),
            PixelWhenStep pixel => BuildEditedPixelStep(pixel),
            _ => current
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
        AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(CaptureStepKey_KeyDown), true);
    }

    private void CaptureStepKey_KeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (IsModifierKey(key)) return;

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
    }

    private void PickPixelColor_Click(object sender, RoutedEventArgs e)
    {
        if (!TryPickScreenPixel(out var x, out var y, out var color)) return;
        PixelXBox.Text = x.ToString();
        PixelYBox.Text = y.ToString();
        PixelRBox.Text = color.R.ToString();
        PixelGBox.Text = color.G.ToString();
        PixelBBox.Text = color.B.ToString();
        UpdatePixelPreview(color);
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

    private void SetEditorPanels(bool keyboard, bool action, bool mouseButton, bool mouseMove, bool wheel, bool timing, bool delay, bool text, bool loop, bool macro, bool pixel)
    {
        KeyboardEditPanel.Visibility = ToVis(keyboard);
        ButtonEditPanel.Visibility = ToVis(action);
        MouseButtonEditPanel.Visibility = ToVis(mouseButton);
        MouseMoveEditPanel.Visibility = ToVis(mouseMove);
        WheelEditPanel.Visibility = ToVis(wheel);
        TimingEditPanel.Visibility = ToVis(timing && !delay);
        DelayEditPanel.Visibility = ToVis(delay);
        TextEditPanel.Visibility = ToVis(text);
        LoopEditPanel.Visibility = ToVis(loop);
        MacroEditPanel.Visibility = ToVis(macro);
        PixelEditPanel.Visibility = ToVis(pixel);
    }

    private void SetModifierBoxes(HidModifier modifiers)
    {
        StepCtrlBox.IsChecked = (modifiers & (HidModifier.LeftCtrl | HidModifier.RightCtrl)) != 0;
        StepShiftBox.IsChecked = (modifiers & (HidModifier.LeftShift | HidModifier.RightShift)) != 0;
        StepAltBox.IsChecked = (modifiers & (HidModifier.LeftAlt | HidModifier.RightAlt)) != 0;
        StepWinBox.IsChecked = (modifiers & (HidModifier.LeftGui | HidModifier.RightGui)) != 0;
    }

    private HidModifier ReadStepModifiers()
    {
        var m = HidModifier.None;
        if (StepCtrlBox.IsChecked == true) m |= HidModifier.LeftCtrl;
        if (StepShiftBox.IsChecked == true) m |= HidModifier.LeftShift;
        if (StepAltBox.IsChecked == true) m |= HidModifier.LeftAlt;
        if (StepWinBox.IsChecked == true) m |= HidModifier.LeftGui;
        return m;
    }

    private string ReadSelectedMacroName()
    {
        return (MacroTargetBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? MacroTargetBox.Text.Trim();
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
    private static bool IsModifierKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;

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
        if (comboBox.SelectedItem is ComboBoxItem { Tag: TEnum value }) return value;
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
