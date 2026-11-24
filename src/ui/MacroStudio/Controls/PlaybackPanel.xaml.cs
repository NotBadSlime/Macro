using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MacroHid.Core;
using MacroHid.Runtime;
using MacroStudio.Services;

namespace MacroStudio.Controls;

public partial class PlaybackPanel : UserControl
{
    private const double WideFieldsWidth = 470;

    private bool wideFields;
    private bool capturingTrigger;
    private bool updatingPlaybackControls;
    private bool isReadOnly;
    private readonly DispatcherTimer triggerCaptureCommitTimer;
    private readonly List<HidKey> capturedTriggerKeys = [];
    private readonly List<MacroHid.Core.MouseButton> capturedTriggerMouseButtons = [];

    public event Action? StartListeningRequested;
    public event Action? StopListeningRequested;
    public event Action? RunNowRequested;
    public event Action? StopPlaybackRequested;
    public event Action? PlaybackSettingsEdited;
    public event Action? TriggerCaptureStarted;
    public event Action? TriggerCaptureFinished;

    public PlaybackPanel()
    {
        InitializeComponent();
        SizeChanged += PlaybackFields_SizeChanged;
        triggerCaptureCommitTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(450)
        };
        triggerCaptureCommitTimer.Tick += (_, _) => CommitTriggerCapture();
    }

    private void PlaybackFields_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var wide = e.NewSize.Width >= WideFieldsWidth;
        if (wide == wideFields)
        {
            return;
        }

        wideFields = wide;
        FieldGapColumn.Width = new GridLength(wide ? 16 : 8);
        FieldEndColumn.Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(72);

        Grid.SetColumnSpan(TriggerField, wide ? 1 : 3);
        Grid.SetRow(ModeField, wide ? 0 : 1);
        Grid.SetColumn(ModeField, wide ? 2 : 0);
        Grid.SetColumn(CountField, wide ? 0 : 2);
        Grid.SetRow(DaemonField, wide ? 1 : 2);
        Grid.SetColumn(DaemonField, wide ? 2 : 0);
        Grid.SetColumnSpan(DaemonField, wide ? 1 : 3);
        DaemonField.Margin = wide ? new Thickness(0, 0, 0, 8) : new Thickness(0, 9, 0, 0);
        DaemonField.Padding = wide ? new Thickness(0, 20, 0, 0) : new Thickness(0, 9, 0, 10);
        DaemonField.BorderThickness = new Thickness(0, wide ? 0 : 1, 0, 0);
        DaemonField.VerticalAlignment = wide ? VerticalAlignment.Center : VerticalAlignment.Stretch;
        CountField.Margin = wide ? new Thickness(0, 0, 0, 8) : new Thickness(0);
    }

    public string TriggerText => TriggerTextBox.Text.Trim();
    public string CountText => PlaybackCountTextBox.Text;
    public bool RunAsDaemon => RunAsDaemonCheckBox.IsChecked == true;

    public void SetRunNowHint(bool continuous) =>
        RunNowButton.ToolTip = L(continuous ? "RunNowConfiguredModeHint" : "RunNowOnceHint");

    public PlaybackMode GetSelectedPlaybackMode()
    {
        var selected = PlaybackModeBox.SelectedItem as ComboBoxItem;
        var value = selected?.Tag?.ToString() ?? "fixedCount";
        return value switch
        {
            "toggleLoop" => PlaybackMode.ToggleLoop,
            "holdLoop" => PlaybackMode.HoldLoop,
            "fixedCount" => PlaybackMode.FixedCount,
            _ => PlaybackMode.FixedCount
        };
    }

    public void SetPlaybackControls(PlaybackSettings settings)
    {
        updatingPlaybackControls = true;
        try
        {
            TriggerTextBox.Text = settings.Trigger?.ToString() ?? string.Empty;
            PlaybackCountTextBox.Text = settings.Count.ToString();
            RunAsDaemonCheckBox.IsChecked = settings.RunAsDaemon;
            foreach (var item in PlaybackModeBox.Items.OfType<ComboBoxItem>())
            {
                if (string.Equals(item.Tag?.ToString(), ToPlaybackModeText(settings.Mode), StringComparison.Ordinal))
                {
                    PlaybackModeBox.SelectedItem = item;
                    break;
                }
            }

            PlaybackCountTextBox.IsEnabled = !isReadOnly && settings.Mode == PlaybackMode.FixedCount;
        }
        finally
        {
            updatingPlaybackControls = false;
        }
    }

    public void SetReadOnly(bool value)
    {
        isReadOnly = value;
        if (value && capturingTrigger)
        {
            StopCapture();
        }
        CaptureTriggerButton.IsEnabled = !value;
        PlaybackModeBox.IsEnabled = !value;
        PlaybackCountTextBox.IsEnabled = !value && GetSelectedPlaybackMode() == PlaybackMode.FixedCount;
        RunAsDaemonCheckBox.IsEnabled = !value;
    }

    public void SetPlaybackStatus(string text)
    {
        PlaybackStatusText.Text = text;
    }

    public void SetPlaybackResult(string text)
    {
        PlaybackResultText.Text = text;
    }

    public void ApplyLocalization()
    {
        PlaybackTitleText.Text = L("Playback");
        TriggerLabelText.Text = L("Trigger");
        CaptureTriggerButton.Content = capturingTrigger ? L("Cancel") : L("Capture");
        TriggerCaptureHintText.Text = L("CaptureTriggerHint");
        TriggerSupportHintText.Text = L("TriggerSupportHint");
        ModeLabelText.Text = L("Mode");
        PlaybackCountLabelText.Text = L("Count");
        RunAsDaemonCheckBox.Content = L("RunAsDaemon");
        RunAsDaemonCheckBox.ToolTip = L("RunAsDaemonHint");
        StartListeningButton.Content = L("StartListening");
        StopListeningButton.Content = L("StopListening");
        RunNowButton.Content = L("RunNow");
        RunNowButton.ToolTip = L("RunNowOnceHint");
        StopPlaybackButton.Content = L("Stop");

        foreach (var item in PlaybackModeBox.Items.OfType<ComboBoxItem>())
        {
            item.Content = item.Tag?.ToString() switch
            {
                "toggleLoop" => L("ModeToggleLoop"),
                "holdLoop" => L("ModeHoldLoop"),
                "fixedCount" => L("ModeFixedCount"),
                _ => item.Content
            };
        }

    }

    private void CaptureTrigger_Click(object sender, RoutedEventArgs e)
    {
        if (capturingTrigger)
        {
            StopCapture();
            return;
        }

        capturingTrigger = true;
        TriggerCaptureStarted?.Invoke();
        capturedTriggerKeys.Clear();
        capturedTriggerMouseButtons.Clear();
        updatingPlaybackControls = true;
        TriggerTextBox.Text = string.Empty;
        TriggerTextBox.Tag = "Capturing";
        CaptureTriggerButton.Content = L("Cancel");
        updatingPlaybackControls = false;
        AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(CaptureTrigger_KeyDown), true);
        AddHandler(Keyboard.PreviewKeyUpEvent, new KeyEventHandler(CaptureTrigger_KeyUp), true);
        AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(CaptureTrigger_MouseDown), true);
    }

    private void CaptureTrigger_KeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (IsModifierKey(key))
        {
            UpdateTriggerCapturePreview(ReadCurrentModifiers());
            return;
        }

        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (!GlobalKeyboardHook.TryMapVirtualKeyToHidKey(virtualKey, out var hidKey))
        {
            StopCapture();
            e.Handled = true;
            return;
        }

        if (!capturedTriggerKeys.Contains(hidKey))
        {
            capturedTriggerKeys.Add(hidKey);
        }

        UpdateTriggerCapturePreview(ReadCurrentModifiers());
        ScheduleTriggerCaptureCommit();
        e.Handled = true;
    }

    private void CaptureTrigger_KeyUp(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (!IsModifierKey(key)) return;

        var modifier = ModifierFromKey(key) | ReadCurrentModifiers();
        if (modifier == HidModifier.None) return;

        if (capturedTriggerKeys.Count == 0 && capturedTriggerMouseButtons.Count == 0)
        {
            TriggerTextBox.Text = new HotkeyGesture(modifier, HidKey.None).ToString();
            CommitTriggerCapture();
        }
        e.Handled = true;
    }

    private void CaptureTrigger_MouseDown(object sender, MouseButtonEventArgs e)
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

        if (button == MacroHid.Core.MouseButton.None) return;

        if (!capturedTriggerMouseButtons.Contains(button))
        {
            capturedTriggerMouseButtons.Add(button);
        }

        UpdateTriggerCapturePreview(ReadCurrentModifiers());
        ScheduleTriggerCaptureCommit();
        e.Handled = true;
    }

    private void StopCapture()
    {
        if (!capturingTrigger) return;
        capturingTrigger = false;
        triggerCaptureCommitTimer.Stop();
        TriggerTextBox.Tag = null;
        CaptureTriggerButton.Content = L("Capture");
        RemoveHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(CaptureTrigger_KeyDown));
        RemoveHandler(Keyboard.PreviewKeyUpEvent, new KeyEventHandler(CaptureTrigger_KeyUp));
        RemoveHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(CaptureTrigger_MouseDown));
        TriggerCaptureFinished?.Invoke();
    }

    private void UpdateTriggerCapturePreview(HidModifier modifiers)
    {
        updatingPlaybackControls = true;
        try
        {
            TriggerTextBox.Text = new HotkeyGesture(ReadCurrentModifiers(), capturedTriggerKeys, capturedTriggerMouseButtons).ToString();
        }
        finally
        {
            updatingPlaybackControls = false;
        }
    }

    private void ScheduleTriggerCaptureCommit()
    {
        triggerCaptureCommitTimer.Stop();
        triggerCaptureCommitTimer.Start();
    }

    private void CommitTriggerCapture()
    {
        if (!capturingTrigger)
        {
            triggerCaptureCommitTimer.Stop();
            return;
        }

        triggerCaptureCommitTimer.Stop();
        var currentText = TriggerTextBox.Text.Trim();
        StopCapture();
        if (!string.IsNullOrWhiteSpace(currentText))
        {
            NotifyPlaybackSettingsEdited();
        }
    }

    private void PlaybackModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PlaybackCountTextBox is not null)
            PlaybackCountTextBox.IsEnabled = !isReadOnly && GetSelectedPlaybackMode() == PlaybackMode.FixedCount;
        NotifyPlaybackSettingsEdited();
    }

    private void PlaybackSettings_TextChanged(object sender, TextChangedEventArgs e)
    {
        NotifyPlaybackSettingsEdited();
    }

    private void RunAsDaemon_Changed(object sender, RoutedEventArgs e) => NotifyPlaybackSettingsEdited();

    private void NotifyPlaybackSettingsEdited()
    {
        if (updatingPlaybackControls)
        {
            return;
        }

        PlaybackSettingsEdited?.Invoke();
    }

    private void StartListening_Click(object sender, RoutedEventArgs e) => StartListeningRequested?.Invoke();
    private void StopListening_Click(object sender, RoutedEventArgs e) => StopListeningRequested?.Invoke();
    private void RunNow_Click(object sender, RoutedEventArgs e) => RunNowRequested?.Invoke();
    private void StopPlayback_Click(object sender, RoutedEventArgs e) => StopPlaybackRequested?.Invoke();

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

    private static string ToPlaybackModeText(PlaybackMode mode) => mode switch
    {
        PlaybackMode.ToggleLoop => "toggleLoop",
        PlaybackMode.HoldLoop => "holdLoop",
        PlaybackMode.FixedCount => "fixedCount",
        _ => "fixedCount"
    };

    private static string L(string key) => LocalizationService.Get(key);
}
