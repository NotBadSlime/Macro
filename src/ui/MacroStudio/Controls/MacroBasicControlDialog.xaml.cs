using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MacroHid.Core;
using MacroStudio.Services;

namespace MacroStudio.Controls;

public partial class MacroBasicControlDialog : Window
{
    private readonly RuntimePrecisionSettings source;
    private readonly Func<string, bool>? backgroundProcessRequired;
    private readonly DispatcherTimer captureCommitTimer;
    private readonly List<HidKey> capturedKeys = [];
    private readonly List<MacroHid.Core.MouseButton> capturedMouseButtons = [];
    private bool capturingColorSample;
    private bool capturingStopTrigger;

    public MacroBasicControlDialog(RuntimePrecisionSettings settings, Func<string, bool>? backgroundProcessRequired = null)
    {
        InitializeComponent();
        ThemedDialogChrome.Apply(this);
        source = settings;
        this.backgroundProcessRequired = backgroundProcessRequired;
        captureCommitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        captureCommitTimer.Tick += (_, _) => CommitCapture();
        ApplyLocalization();
        BackgroundWindowCheck.IsChecked = settings.BackgroundWindow;
        BackgroundProcessBox.Text = settings.BackgroundProcess;
        ColorSampleHotkeyBox.Text = string.IsNullOrWhiteSpace(settings.ColorSampleHotkey)
            ? CoreHotkeys.DefaultColorSampleText
            : settings.ColorSampleHotkey;
        StopTriggerTextBox.Text = settings.ForceStopHotkey;
        ZeroGapHoldBox.Text = settings.ZeroGapHoldMicroseconds.ToString();
        ModifierChordHoldBox.Text = settings.ModifierChordHoldMicroseconds.ToString();
        foreach (var item in CloseButtonBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), ToCloseButtonTag(settings.CloseButton), StringComparison.Ordinal))
            {
                CloseButtonBox.SelectedItem = item;
                break;
            }
        }
        BackgroundWindowCheck.Checked += BackgroundWindowCheck_Changed;
        BackgroundWindowCheck.Unchecked += BackgroundWindowCheck_Changed;
    }

    public event Action? CaptureStarted;

    public event Action? CaptureFinished;

    public RuntimePrecisionSettings Settings { get; private set; } = RuntimePrecisionSettings.Default;

    private void ApplyLocalization()
    {
        Title = L("MacroBasicControl");
        BackgroundWindowCheck.Content = L("BackgroundWindow");
        BackgroundProcessLabelText.Text = L("BackgroundProcess");
        SelectBackgroundProcessButton.Content = L("BackgroundSelectProcess");
        BackgroundProcessPlaceholderText.Text = L("BackgroundProcessPlaceholder");
        BackgroundWindowHelpText.Text = L("BackgroundWindowHelp");
        BackgroundProcessBox.ToolTip = L("BackgroundWindowHelp");
        ColorSampleHotkeyLabelText.Text = L("ColorSampleHotkey");
        ColorSampleHotkeyHelpText.Text = L("ColorSampleHotkeyHelp");
        ColorSampleHotkeyBox.ToolTip = L("ColorSampleHotkeyHelp");
        CaptureColorSampleButton.Content = L("Capture");
        StopTriggerLabelText.Text = L("StopTrigger");
        StopTriggerHintText.Text = L("StopTriggerHint");
        CaptureStopTriggerButton.Content = L("Capture");
        ClearStopTriggerButton.Content = L("ClearHotkey");
        ZeroGapHoldLabelText.Text = L("ZeroGapHold");
        ZeroGapHoldHelpText.Text = L("ZeroGapHoldHelp");
        ZeroGapHoldBox.ToolTip = L("ZeroGapHoldHelp");
        ModifierChordHoldLabelText.Text = L("ModifierChordHold");
        ModifierChordHoldHelpText.Text = L("ModifierChordHoldHelp");
        ModifierChordHoldBox.ToolTip = L("ModifierChordHoldHelp");
        ZeroGapUnitText.Text = L("Microseconds");
        ModifierChordUnitText.Text = L("Microseconds");
        CloseButtonLabelText.Text = L("CloseButtonBehavior");
        CloseButtonHelpText.Text = L("CloseButtonBehaviorHelp");
        foreach (var item in CloseButtonBox.Items.OfType<ComboBoxItem>())
        {
            item.Content = item.Tag?.ToString() switch
            {
                "minimizeToTray" => L("CloseToTray"),
                "exit" => L("CloseAndExit"),
                _ => L("CloseButtonAsk")
            };
        }
        CancelButton.Content = L("Cancel");
        ConfirmButton.Content = L("Confirm");
    }

    private void BackgroundWindowCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (BackgroundWindowCheck.IsChecked == true
            && backgroundProcessRequired?.Invoke(BackgroundProcessBox.Text.Trim()) == true)
        {
            var message = L("BackgroundProcessRequired");
            DialogOwnerService.MessageBoxSafe(this, message, L("BackgroundWindow"), MessageBoxButton.OK, MessageBoxImage.Information);
            BackgroundProcessBox.Focus();
        }
    }

    private void SelectBackgroundProcess_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        foreach (var process in EnumerateRunningProcesses())
        {
            var item = new MenuItem
            {
                Header = string.IsNullOrWhiteSpace(process.Title)
                    ? process.Name
                    : $"{process.Name} - {process.Title}",
                Tag = process.Name
            };
            item.Click += (_, _) => BackgroundProcessBox.Text = process.Name;
            menu.Items.Add(item);
        }

        if (menu.Items.Count == 0)
        {
            DialogOwnerService.MessageBoxSafe(this, L("NoProcessesFound"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        menu.PlacementTarget = SelectBackgroundProcessButton;
        menu.IsOpen = true;
    }

    private void CaptureColorSample_Click(object sender, RoutedEventArgs e)
    {
        if (capturingColorSample)
        {
            StopCapture(commit: false);
            return;
        }

        BeginCapture(colorSample: true);
    }

    private void CaptureStopTrigger_Click(object sender, RoutedEventArgs e)
    {
        if (capturingStopTrigger)
        {
            StopCapture(commit: false);
            return;
        }

        BeginCapture(colorSample: false);
    }

    private void ClearStopTrigger_Click(object sender, RoutedEventArgs e)
    {
        StopCapture(commit: false);
        StopTriggerTextBox.Text = string.Empty;
    }

    private void BeginCapture(bool colorSample)
    {
        StopCapture(commit: false);
        capturingColorSample = colorSample;
        capturingStopTrigger = !colorSample;
        capturedKeys.Clear();
        capturedMouseButtons.Clear();
        var box = colorSample ? ColorSampleHotkeyBox : StopTriggerTextBox;
        var button = colorSample ? CaptureColorSampleButton : CaptureStopTriggerButton;
        box.Text = string.Empty;
        button.Content = L("Cancel");
        CaptureStarted?.Invoke();
        AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(Capture_KeyDown), true);
        AddHandler(Keyboard.PreviewKeyUpEvent, new KeyEventHandler(Capture_KeyUp), true);
        if (!colorSample)
        {
            AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(Capture_MouseDown), true);
        }
    }

    private void Capture_KeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (IsModifierKey(key))
        {
            UpdateCapturePreview();
            e.Handled = true;
            return;
        }

        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (!GlobalKeyboardHook.TryMapVirtualKeyToHidKey(virtualKey, out var hidKey))
        {
            StopCapture(commit: false);
            e.Handled = true;
            return;
        }

        if (!capturedKeys.Contains(hidKey))
        {
            capturedKeys.Add(hidKey);
        }

        UpdateCapturePreview();
        captureCommitTimer.Stop();
        captureCommitTimer.Start();
        e.Handled = true;
    }

    private void Capture_KeyUp(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (!IsModifierKey(key))
        {
            return;
        }

        if (capturedKeys.Count == 0 && capturedMouseButtons.Count == 0)
        {
            var modifier = ModifierFromKey(key) | ReadCurrentModifiers();
            if (modifier != HidModifier.None)
            {
                ActiveBox().Text = new HotkeyGesture(modifier, HidKey.None).ToString();
                CommitCapture();
            }
        }

        e.Handled = true;
    }

    private void Capture_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var button = e.ChangedButton switch
        {
            System.Windows.Input.MouseButton.Left => MacroHid.Core.MouseButton.Left,
            System.Windows.Input.MouseButton.Right => MacroHid.Core.MouseButton.Right,
            System.Windows.Input.MouseButton.Middle => MacroHid.Core.MouseButton.Middle,
            System.Windows.Input.MouseButton.XButton1 => MacroHid.Core.MouseButton.X1,
            System.Windows.Input.MouseButton.XButton2 => MacroHid.Core.MouseButton.X2,
            _ => MacroHid.Core.MouseButton.None
        };
        if (button == MacroHid.Core.MouseButton.None)
        {
            return;
        }

        if (!capturedMouseButtons.Contains(button))
        {
            capturedMouseButtons.Add(button);
        }

        UpdateCapturePreview();
        captureCommitTimer.Stop();
        captureCommitTimer.Start();
        e.Handled = true;
    }

    private void UpdateCapturePreview()
    {
        ActiveBox().Text = new HotkeyGesture(ReadCurrentModifiers(), capturedKeys, capturedMouseButtons).ToString();
    }

    private void CommitCapture()
    {
        if (!capturingColorSample && !capturingStopTrigger)
        {
            captureCommitTimer.Stop();
            return;
        }

        var text = ActiveBox().Text.Trim();
        var colorSample = capturingColorSample;
        StopCapture(commit: false);
        if (colorSample && string.IsNullOrWhiteSpace(text))
        {
            ColorSampleHotkeyBox.Text = CoreHotkeys.DefaultColorSampleText;
            return;
        }

        if (!string.IsNullOrWhiteSpace(text))
        {
            (colorSample ? ColorSampleHotkeyBox : StopTriggerTextBox).Text = text;
        }
    }

    private void StopCapture(bool commit)
    {
        if (!capturingColorSample && !capturingStopTrigger)
        {
            return;
        }

        if (commit)
        {
            CommitCapture();
            return;
        }

        capturingColorSample = false;
        capturingStopTrigger = false;
        captureCommitTimer.Stop();
        CaptureColorSampleButton.Content = L("Capture");
        CaptureStopTriggerButton.Content = L("Capture");
        RemoveHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(Capture_KeyDown));
        RemoveHandler(Keyboard.PreviewKeyUpEvent, new KeyEventHandler(Capture_KeyUp));
        RemoveHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(Capture_MouseDown));
        CaptureFinished?.Invoke();
    }

    private TextBox ActiveBox() => capturingColorSample ? ColorSampleHotkeyBox : StopTriggerTextBox;

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        StopCapture(commit: true);
        if (!long.TryParse(ZeroGapHoldBox.Text.Trim(), out var zeroGap)
            || !long.TryParse(ModifierChordHoldBox.Text.Trim(), out var chord)
            || zeroGap < 0
            || chord < 0
            || zeroGap > InputPressTiming.MaxHoldMicroseconds
            || chord > InputPressTiming.MaxHoldMicroseconds)
        {
            DialogOwnerService.MessageBoxSafe(this, L("HoldMicrosecondsInvalid"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var colorSample = CoreHotkeys.NormalizeText(ColorSampleHotkeyBox.Text);
        var forceStop = StopTriggerTextBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(forceStop))
        {
            try
            {
                forceStop = McrxParser.ParseHotkeyGesture(forceStop).ToString();
            }
            catch (Exception ex)
            {
                DialogOwnerService.MessageBoxSafe(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (CoreHotkeys.GesturesEqual(CoreHotkeys.ParseOrDefault(colorSample), McrxParser.ParseHotkeyGesture(forceStop)))
            {
                DialogOwnerService.MessageBoxSafe(this, L("ForceStopConflictsWithColorSample"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }

        Settings = source with
        {
            BackgroundWindow = BackgroundWindowCheck.IsChecked == true,
            BackgroundProcess = BackgroundProcessBox.Text.Trim(),
            ColorSampleHotkey = colorSample,
            ForceStopHotkey = forceStop,
            ZeroGapHoldMicroseconds = zeroGap,
            ModifierChordHoldMicroseconds = chord,
            CloseButton = SelectedCloseButton()
        };
        DialogResult = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        StopCapture(commit: false);
        base.OnClosed(e);
    }

    private CloseButtonBehavior SelectedCloseButton()
    {
        return (CloseButtonBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
        {
            "minimizeToTray" => CloseButtonBehavior.MinimizeToTray,
            "exit" => CloseButtonBehavior.Exit,
            _ => CloseButtonBehavior.Ask
        };
    }

    private static string ToCloseButtonTag(CloseButtonBehavior behavior) => behavior switch
    {
        CloseButtonBehavior.MinimizeToTray => "minimizeToTray",
        CloseButtonBehavior.Exit => "exit",
        _ => "ask"
    };

    private static IEnumerable<(string Name, string Title)> EnumerateRunningProcesses()
    {
        return Process.GetProcesses()
            .Select(process =>
            {
                try
                {
                    var name = process.ProcessName + ".exe";
                    var title = process.MainWindowTitle;
                    return string.IsNullOrWhiteSpace(name) ? default : (Name: name, Title: title);
                }
                catch
                {
                    return default;
                }
                finally
                {
                    process.Dispose();
                }
            })
            .Where(choice => !string.IsNullOrWhiteSpace(choice.Name))
            .GroupBy(choice => choice.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(choice => !string.IsNullOrWhiteSpace(choice.Title)).First())
            .OrderBy(choice => choice.Name, StringComparer.CurrentCultureIgnoreCase);
    }

    private static bool IsModifierKey(Key key) =>
        key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;

    private static HidModifier ModifierFromKey(Key key) => key switch
    {
        Key.LeftCtrl => HidModifier.LeftCtrl,
        Key.RightCtrl => HidModifier.RightCtrl,
        Key.LeftShift => HidModifier.LeftShift,
        Key.RightShift => HidModifier.RightShift,
        Key.LeftAlt => HidModifier.LeftAlt,
        Key.RightAlt => HidModifier.RightAlt,
        Key.LWin => HidModifier.LeftGui,
        Key.RWin => HidModifier.RightGui,
        _ => HidModifier.None
    };

    private static HidModifier ReadCurrentModifiers()
    {
        var modifiers = HidModifier.None;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) modifiers |= HidModifier.LeftCtrl;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) modifiers |= HidModifier.LeftShift;
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) modifiers |= HidModifier.LeftAlt;
        if ((Keyboard.Modifiers & ModifierKeys.Windows) != 0) modifiers |= HidModifier.LeftGui;
        return modifiers;
    }

    private static string L(string key) => LocalizationService.Get(key);
}
