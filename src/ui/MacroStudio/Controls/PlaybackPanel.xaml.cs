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
    private bool capturingTrigger;
    private bool updatingPlaybackControls;
    private readonly DispatcherTimer triggerCaptureCommitTimer;
    private readonly List<HidKey> capturedTriggerKeys = [];
    private readonly List<MacroHid.Core.MouseButton> capturedTriggerMouseButtons = [];

    public event Action? StartListeningRequested;
    public event Action? StopListeningRequested;
    public event Action? RunNowRequested;
    public event Action? StopPlaybackRequested;
    public event Action? PlaybackSettingsEdited;

    public PlaybackPanel()
    {
        InitializeComponent();
        triggerCaptureCommitTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(450)
        };
        triggerCaptureCommitTimer.Tick += (_, _) => CommitTriggerCapture();
    }

    public string TriggerText => TriggerTextBox.Text.Trim();
    public string CountText => PlaybackCountTextBox.Text;

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
            foreach (var item in PlaybackModeBox.Items.OfType<ComboBoxItem>())
            {
                if (string.Equals(item.Tag?.ToString(), ToPlaybackModeText(settings.Mode), StringComparison.Ordinal))
                {
                    PlaybackModeBox.SelectedItem = item;
                    break;
                }
            }

            PlaybackCountTextBox.IsEnabled = settings.Mode == PlaybackMode.FixedCount;
        }
        finally
        {
            updatingPlaybackControls = false;
        }
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
        CaptureTriggerButton.Content = L("Capture");
        ModeLabelText.Text = L("Mode");
        PlaybackCountLabelText.Text = L("Count");
        StartListeningButton.Content = L("StartListening");
        StopListeningButton.Content = L("StopListening");
        RunNowButton.Content = L("RunNow");
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
        capturedTriggerKeys.Clear();
        capturedTriggerMouseButtons.Clear();
        updatingPlaybackControls = true;
        TriggerTextBox.Text = string.Empty;
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
        RemoveHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(CaptureTrigger_KeyDown));
        RemoveHandler(Keyboard.PreviewKeyUpEvent, new KeyEventHandler(CaptureTrigger_KeyUp));
        RemoveHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(CaptureTrigger_MouseDown));
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
            PlaybackCountTextBox.IsEnabled = GetSelectedPlaybackMode() == PlaybackMode.FixedCount;
        NotifyPlaybackSettingsEdited();
    }

    private void PlaybackSettings_TextChanged(object sender, TextChangedEventArgs e)
    {
        NotifyPlaybackSettingsEdited();
    }

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
