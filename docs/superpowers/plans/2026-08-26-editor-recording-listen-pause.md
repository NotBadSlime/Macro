# Editor Recording Modes and Listen Pause Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Separate press/release from delay in the step editor, add three recording modes that never capture mouse movement, and add a session-wide pause-listening control so existing triggers cannot fire while the user is configuring keys.

**Architecture:** Keep Hold/Modifiers on the existing step records. Templates and delayed recording insert a shared 5ms `WaitStep`. `MacroRecordingSession` gains a delay mode and defaults `RecordMouseMove` to false. `MainWindow` owns pause state; capture surfaces only raise start/finish events.

**Tech Stack:** C# / .NET 8, WPF MacroStudio, existing `tests/MacroHid.Core.Tests` executable (no xUnit).

**Spec:** `docs/superpowers/specs/2026-08-26-editor-recording-listen-pause-design.md`

**Working tree:** Only stage files listed in each task. The repo already has unrelated dirty files; do not include them.

**Test command** (run from repo root):

```powershell
dotnet run --project tests\MacroHid.Core.Tests\MacroHid.Core.Tests.csproj
```

Expected success line: `N test(s) passed.` (N increases as tests are added).

---

## File map

| File | Responsibility |
|------|----------------|
| `src/shared/MacroHid.Core/PressReleaseGapTracker.cs` | Add `InputPressTiming.DefaultPressReleaseGap` (5ms) |
| `src/shared/MacroHid.Core/MacroActionTemplates.cs` | Keyboard/mouse/pixel templates insert 5ms wait |
| `src/shared/MacroHid.Core/MacroRecordingSession.cs` | `MacroRecordingMode`, delay strategy, default no mouse move |
| `src/ui/MacroStudio/RecordingModeMenu.cs` | Shared context menu for the three recording modes |
| `src/ui/MacroStudio/MacroInputRecorder.cs` | Unchanged hook; session options already filter moves |
| `src/ui/MacroStudio/Controls/StepEditorPanel.xaml(.cs)` | Hide timing except mouse move; capture modifiers; localize Down/Up |
| `src/ui/MacroStudio/Controls/SequencePanel.xaml(.cs)` | Show mode menu before recording |
| `src/ui/MacroStudio/Controls/ConditionDirectivePanel.xaml.cs` | Same mode menu for condition recording |
| `src/ui/MacroStudio/Controls/PlaybackPanel.xaml.cs` | Raise trigger-capture start/finish |
| `src/ui/MacroStudio/MainWindow.xaml(.cs)` | Pause button; pause/resume listening; pass recording options |
| `src/ui/MacroStudio/Resources/Strings*.resx` | New UI strings |
| `docs/usage.md` | Recording modes, no mouse move, pause listening |
| `tests/MacroHid.Core.Tests/Program.cs` | Core + UI source tests |

---

### Task 1: Default 5ms gap and press/release templates

**Files:**
- Modify: `src/shared/MacroHid.Core/PressReleaseGapTracker.cs`
- Modify: `src/shared/MacroHid.Core/MacroActionTemplates.cs`
- Modify: `tests/MacroHid.Core.Tests/Program.cs` (`MacroActionTemplatesCreatePlayablePressReleaseSteps`)

- [ ] **Step 1: Write the failing template assertions**

In `MacroActionTemplatesCreatePlayablePressReleaseSteps`, replace the keyboard/mouse counts and add pixel-gap checks. Also update the round-trip step count from 11 to 13:

```csharp
var keySteps = MacroActionTemplateFactory.CreateSteps(MacroActionTemplateKind.Keyboard);
Assert.Equal(3, keySteps.Count);
Assert.Equal(KeyActionKind.Down, Assert.IsType<KeyStep>(keySteps[0]).Kind);
Assert.Equal(TimeSpan.Zero, Assert.IsType<KeyStep>(keySteps[0]).Hold);
Assert.Equal(TimeSpan.FromMilliseconds(5), Assert.IsType<WaitStep>(keySteps[1]).Duration);
Assert.Equal(KeyActionKind.Up, Assert.IsType<KeyStep>(keySteps[2]).Kind);
Assert.Equal(TimeSpan.Zero, Assert.IsType<KeyStep>(keySteps[2]).Hold);

var mouseSteps = MacroActionTemplateFactory.CreateSteps(MacroActionTemplateKind.MouseButton);
Assert.Equal(3, mouseSteps.Count);
Assert.Equal(ButtonActionKind.Down, Assert.IsType<MouseButtonStep>(mouseSteps[0]).Kind);
Assert.Equal(TimeSpan.Zero, Assert.IsType<MouseButtonStep>(mouseSteps[0]).Hold);
Assert.Equal(TimeSpan.FromMilliseconds(5), Assert.IsType<WaitStep>(mouseSteps[1]).Duration);
Assert.Equal(ButtonActionKind.Up, Assert.IsType<MouseButtonStep>(mouseSteps[2]).Kind);

var pixel = Assert.IsType<PixelWhenStep>(
    MacroActionTemplateFactory.CreateSteps(MacroActionTemplateKind.Pixel)[0]);
Assert.Equal(3, pixel.ThenSteps.Count);
Assert.Equal(TimeSpan.FromMilliseconds(5), Assert.IsType<WaitStep>(pixel.ThenSteps[1]).Duration);
```

Change `Assert.Equal(11, roundTrip.Steps.Count);` to `Assert.Equal(13, roundTrip.Steps.Count);`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet run --project tests\MacroHid.Core.Tests\MacroHid.Core.Tests.csproj`

Expected: `FAIL Macro action templates create playable press release steps` because keyboard/mouse templates still return 2 steps.

- [ ] **Step 3: Add the shared constant and update templates**

In `InputPressTiming` (`PressReleaseGapTracker.cs`), add:

```csharp
public static readonly TimeSpan DefaultPressReleaseGap = TimeSpan.FromMilliseconds(5);
```

In `MacroActionTemplates.cs`, use `TimeSpan.Zero` holds and insert `new WaitStep(InputPressTiming.DefaultPressReleaseGap)` between down/up for Keyboard, MouseButton, and Pixel inner keys:

```csharp
MacroActionTemplateKind.Keyboard =>
[
    new KeyStep(KeyActionKind.Down, HidKey.A, HidModifier.None, TimeSpan.Zero),
    new WaitStep(InputPressTiming.DefaultPressReleaseGap),
    new KeyStep(KeyActionKind.Up, HidKey.A, HidModifier.None, TimeSpan.Zero)
],
MacroActionTemplateKind.MouseButton =>
[
    new MouseButtonStep(MouseButton.Left, ButtonActionKind.Down, TimeSpan.Zero),
    new WaitStep(InputPressTiming.DefaultPressReleaseGap),
    new MouseButtonStep(MouseButton.Left, ButtonActionKind.Up, TimeSpan.Zero)
],
```

Pixel `ThenSteps` becomes the same three-step keyboard pair.

- [ ] **Step 4: Run tests to verify they pass**

Run the same `dotnet run` command.

Expected: `PASS Macro action templates create playable press release steps` and no other new failures from this change.

- [ ] **Step 5: Commit**

```powershell
git add src/shared/MacroHid.Core/PressReleaseGapTracker.cs src/shared/MacroHid.Core/MacroActionTemplates.cs tests/MacroHid.Core.Tests/Program.cs
git commit -m "feat: insert a 5ms wait between template press and release."
```

---

### Task 2: Recording delay modes (core)

**Files:**
- Modify: `src/shared/MacroHid.Core/MacroRecordingSession.cs`
- Modify: `tests/MacroHid.Core.Tests/Program.cs`

- [ ] **Step 1: Register and write failing recording tests**

Add these entries next to the existing recording tests in the `tests` array:

```csharp
("Macro recording session ignores mouse move by default", MacroRecordingSessionIgnoresMouseMoveByDefault),
("Macro recording session uses a fixed delay between inputs", MacroRecordingSessionUsesFixedDelayBetweenInputs),
("Macro recording session can omit delays", MacroRecordingSessionCanOmitDelays),
```

Update `MacroRecordingSessionSamplesMovementAndReleasesHeldInputs` so the options explicitly enable movement:

```csharp
var recording = new MacroRecordingSession(new MacroRecordingOptions(
    RecordMouseMove: true,
    MinimumDelay: TimeSpan.FromMilliseconds(1),
    MouseMoveSampleInterval: TimeSpan.FromMilliseconds(16),
    MouseMoveMinimumDistance: 2));
```

Add:

```csharp
static void MacroRecordingSessionIgnoresMouseMoveByDefault()
{
    var recording = new MacroRecordingSession();
    Assert.False(recording.RecordMouseMove(TimeSpan.Zero, 100, 100));
    Assert.True(recording.RecordKey(TimeSpan.FromMilliseconds(10), HidKey.A, true));
    var steps = recording.Complete();
    Assert.DoesNotContain(steps, static step => step is MouseMoveStep);
    Assert.Equal(KeyActionKind.Down, Assert.IsType<KeyStep>(steps[0]).Kind);
}

static void MacroRecordingSessionUsesFixedDelayBetweenInputs()
{
    var recording = new MacroRecordingSession(new MacroRecordingOptions(
        Mode: MacroRecordingMode.FixedDelay,
        RecordMouseMove: false));
    Assert.True(recording.RecordKey(TimeSpan.Zero, HidKey.A, true));
    Assert.True(recording.RecordKey(TimeSpan.FromMilliseconds(250), HidKey.A, false));
    var steps = recording.Complete();
    Assert.Equal(3, steps.Count);
    Assert.Equal(InputPressTiming.DefaultPressReleaseGap, Assert.IsType<WaitStep>(steps[1]).Duration);
}

static void MacroRecordingSessionCanOmitDelays()
{
    var recording = new MacroRecordingSession(new MacroRecordingOptions(
        Mode: MacroRecordingMode.NoDelay,
        RecordMouseMove: false));
    Assert.True(recording.RecordKey(TimeSpan.Zero, HidKey.A, true));
    Assert.True(recording.RecordKey(TimeSpan.FromMilliseconds(80), HidKey.A, false));
    var steps = recording.Complete();
    Assert.Equal(2, steps.Count);
    Assert.DoesNotContain(steps, static step => step is WaitStep);
}
```

If `Assert.DoesNotContain` with a predicate is unavailable, use `Assert.False(steps.OfType<MouseMoveStep>().Any());` / `Assert.False(steps.OfType<WaitStep>().Any());`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet run --project tests\MacroHid.Core.Tests\MacroHid.Core.Tests.csproj`

Expected: compile error `MacroRecordingMode does not exist`, or FAIL on ignore-move because `RecordMouseMove` still defaults to true.

- [ ] **Step 3: Implement recording options and AddInput**

Replace `MacroRecordingOptions` with:

```csharp
public enum MacroRecordingMode
{
    Replica,
    FixedDelay,
    NoDelay
}

public sealed record MacroRecordingOptions(
    bool RecordKeyboard = true,
    bool RecordMouseButtons = true,
    bool RecordMouseWheel = true,
    bool RecordMouseMove = false,
    MacroRecordingMode Mode = MacroRecordingMode.Replica,
    TimeSpan MinimumDelay = default,
    TimeSpan FixedDelay = default,
    TimeSpan MouseMoveSampleInterval = default,
    int MouseMoveMinimumDistance = 2)
{
    public static MacroRecordingOptions Default { get; } = new(
        MinimumDelay: TimeSpan.FromMilliseconds(1),
        MouseMoveSampleInterval: TimeSpan.FromMilliseconds(16),
        MouseMoveMinimumDistance: 2);

    public static MacroRecordingOptions ForUserMode(MacroRecordingMode mode) => new(
        RecordMouseMove: false,
        Mode: mode,
        MinimumDelay: TimeSpan.FromMilliseconds(1),
        FixedDelay: InputPressTiming.DefaultPressReleaseGap,
        MouseMoveSampleInterval: TimeSpan.FromMilliseconds(16),
        MouseMoveMinimumDistance: 2);
}
```

In `NormalizeOptions`, also normalize `FixedDelay` to `InputPressTiming.DefaultPressReleaseGap` when `<= TimeSpan.Zero`.

Replace `AddInput` delay insertion:

```csharp
private void AddInput(TimeSpan elapsed, MacroStep step)
{
    elapsed = elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
    if (hasAcceptedInput)
    {
        switch (options.Mode)
        {
            case MacroRecordingMode.NoDelay:
                break;
            case MacroRecordingMode.FixedDelay:
                steps.Add(new WaitStep(options.FixedDelay));
                break;
            default:
                var delay = elapsed - lastAcceptedTime;
                if (delay >= options.MinimumDelay)
                {
                    steps.Add(new WaitStep(delay));
                }
                break;
        }
    }

    steps.Add(step);
    lastAcceptedTime = elapsed;
    hasAcceptedInput = true;
}
```

Do not change `RecordMouseMove` early-return logic; the new default already disables it.

- [ ] **Step 4: Run tests to verify they pass**

Run the same `dotnet run` command.

Expected: the three new tests PASS, and `Macro recording session samples movement...` still PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/shared/MacroHid.Core/MacroRecordingSession.cs tests/MacroHid.Core.Tests/Program.cs
git commit -m "feat: add replica, fixed-delay, and no-delay recording modes."
```

---

### Task 3: Step editor property card

**Files:**
- Modify: `src/ui/MacroStudio/Controls/StepEditorPanel.xaml`
- Modify: `src/ui/MacroStudio/Controls/StepEditorPanel.xaml.cs`
- Modify: `src/ui/MacroStudio/Resources/Strings.resx`
- Modify: `src/ui/MacroStudio/Resources/Strings.zh-CN.resx`
- Modify: `src/ui/MacroStudio/Resources/Strings.zh-TW.resx`
- Modify: `tests/MacroHid.Core.Tests/Program.cs`

- [ ] **Step 1: Write the failing UI source test**

Add to the `tests` array:

```csharp
("MacroStudio step editor hides hold timing and captures modifier keys", MacroStudioStepEditorHidesHoldTimingAndCapturesModifierKeys),
```

```csharp
static void MacroStudioStepEditorHidesHoldTimingAndCapturesModifierKeys()
{
    var xaml = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "StepEditorPanel.xaml"));
    var code = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "StepEditorPanel.xaml.cs"));
    var english = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Resources", "Strings.resx"));
    var simplified = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Resources", "Strings.zh-CN.resx"));
    var traditional = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Resources", "Strings.zh-TW.resx"));

    Assert.DoesNotContain("StepCtrlBox", xaml);
    Assert.DoesNotContain("StepShiftBox", xaml);
    Assert.DoesNotContain("StepAltBox", xaml);
    Assert.DoesNotContain("StepWinBox", xaml);
    Assert.Contains("timing: step is MouseMoveStep", code);
    Assert.Contains("Hold = TimeSpan.Zero", code);
    Assert.Contains("ActionKindDown", code);
    Assert.DoesNotContain("if (IsModifierKey(key)) return;", code);
    Assert.Contains("name=\"ActionKindDown\"", english);
    Assert.Contains("按下", simplified);
    Assert.Contains("抬起", simplified);
    Assert.Contains("點按", traditional);
}
```

If `Assert.DoesNotContain(string, string)` is missing, use `Assert.False(xaml.Contains("StepCtrlBox"));`.

- [ ] **Step 2: Run tests to verify they fail**

Expected: FAIL because modifier checkboxes and `if (IsModifierKey(key)) return;` still exist.

- [ ] **Step 3: Add localization strings**

Append to `Strings.resx`:

```xml
<data name="ActionKindDown" xml:space="preserve"><value>Down</value></data>
<data name="ActionKindUp" xml:space="preserve"><value>Up</value></data>
<data name="ActionKindTap" xml:space="preserve"><value>Tap</value></data>
<data name="ActionKindClick" xml:space="preserve"><value>Click</value></data>
```

`Strings.zh-CN.resx`: Down=按下, Up=抬起, Tap=点按, Click=单击.

`Strings.zh-TW.resx`: Down=按下, Up=抬起, Tap=點按, Click=單擊.

- [ ] **Step 4: Update StepEditorPanel XAML and code**

Delete the `WrapPanel` with the four modifier checkboxes from `StepEditorPanel.xaml`.

In `SetEditorPanels` call sites, change timing to only `MouseMoveStep`:

```csharp
timing: step is MouseMoveStep,
```

`TimingEditPanel.Visibility = ToVis(timing && !delay);` stays.

In `PopulateStepEditor`:
- Remove `SetModifierBoxes(...)` and key/button `TimingMsBox` writes.
- Before `SetComboBox(ActionKindBox, ...)`, refill the box with the matching enum:

```csharp
case KeyStep key:
    FillEnumBox(ActionKindBox, key.Kind);
    StepKeyBox.Text = key.Key.ToString();
    SetComboBox(ActionKindBox, key.Kind.ToString());
    break;
case MouseButtonStep button:
    FillEnumBox(ActionKindBox, button.Kind);
    // existing mouse fields, no TimingMsBox
```

For `ConsumerStep` if populated, `FillEnumBox(ActionKindBox, consumer.Kind)` and no timing.

`BuildEditedStep` for `KeyStep`:

```csharp
KeyStep key => key with
{
    Key = ParseHidKeyFromText(StepKeyBox.Text),
    Kind = GetComboBoxEnum<KeyActionKind>(ActionKindBox),
    Modifiers = IsModifierHidKey(ParseHidKeyFromText(StepKeyBox.Text))
        ? HidModifier.None
        : key.Modifiers,
    Hold = TimeSpan.Zero
},
```

`MouseButtonStep` and any `ConsumerStep` branch: `Hold = TimeSpan.Zero` (do not read `TimingMsBox`). `MouseMoveStep` still reads `TimingMsBox`.

`CaptureStepKey_KeyDown`: remove the `if (IsModifierKey(key)) return;` line so Ctrl/Shift/Alt/Win are captured. Keep mapping via `TryMapVirtualKeyToHidKey`.

Replace `GetEnumLabel`:

```csharp
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
```

Delete `SetModifierBoxes`, `ReadStepModifiers`, and `IsModifierKey` if unused.

Add:

```csharp
private static bool IsModifierHidKey(HidKey key) =>
    key is HidKey.LeftControl or HidKey.RightControl
        or HidKey.LeftShift or HidKey.RightShift
        or HidKey.LeftAlt or HidKey.RightAlt
        or HidKey.LeftGui or HidKey.RightGui;
```

Fix `GetComboBoxEnum` so a `ButtonActionKind` tag can still parse as `KeyActionKind` by name (both have Down/Up). After refill-on-populate this is a safety net:

```csharp
if (comboBox.SelectedItem is ComboBoxItem { Tag: Enum tag }
    && Enum.TryParse<TEnum>(tag.ToString(), ignoreCase: true, out var fromTag))
{
    return fromTag;
}
```

- [ ] **Step 5: Run tests to verify they pass**

Expected: `PASS MacroStudio step editor hides hold timing and captures modifier keys`.

- [ ] **Step 6: Commit**

```powershell
git add src/ui/MacroStudio/Controls/StepEditorPanel.xaml src/ui/MacroStudio/Controls/StepEditorPanel.xaml.cs src/ui/MacroStudio/Resources/Strings.resx src/ui/MacroStudio/Resources/Strings.zh-CN.resx src/ui/MacroStudio/Resources/Strings.zh-TW.resx tests/MacroHid.Core.Tests/Program.cs
git commit -m "feat: simplify key/mouse property cards and localize press kinds."
```

---

### Task 4: Recording mode menu and recorder wiring

**Files:**
- Create: `src/ui/MacroStudio/RecordingModeMenu.cs`
- Modify: `src/ui/MacroStudio/Controls/SequencePanel.xaml.cs`
- Modify: `src/ui/MacroStudio/Controls/ConditionDirectivePanel.xaml.cs`
- Modify: `src/ui/MacroStudio/MainWindow.xaml.cs`
- Modify: `src/ui/MacroStudio/Resources/Strings.resx`
- Modify: `src/ui/MacroStudio/Resources/Strings.zh-CN.resx`
- Modify: `src/ui/MacroStudio/Resources/Strings.zh-TW.resx`
- Modify: `docs/usage.md`
- Modify: `tests/MacroHid.Core.Tests/Program.cs`

- [ ] **Step 1: Write the failing UI source test and extend existing recording tests**

Add:

```csharp
("MacroStudio recording buttons choose a delay mode first", MacroStudioRecordingButtonsChooseADelayModeFirst),
```

```csharp
static void MacroStudioRecordingButtonsChooseADelayModeFirst()
{
    var menu = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "RecordingModeMenu.cs"));
    var sequence = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "SequencePanel.xaml.cs"));
    var condition = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "ConditionDirectivePanel.xaml.cs"));
    var mainWindow = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "MainWindow.xaml.cs"));

    Assert.Contains("MacroRecordingMode.Replica", menu);
    Assert.Contains("MacroRecordingMode.FixedDelay", menu);
    Assert.Contains("MacroRecordingMode.NoDelay", menu);
    Assert.Contains("RecordingModeMenu.Show", sequence);
    Assert.Contains("RecordingModeMenu.Show", condition);
    Assert.Contains("Action<MacroRecordingMode>", sequence);
    Assert.Contains("MacroRecordingOptions.ForUserMode(mode)", mainWindow);
    Assert.Contains("new MacroInputRecorder(options)", mainWindow);
}
```

In `MacroStudioExposesKeyboardAndMouseInputRecording`, keep `session.RecordMouseMove` (the hook still forwards moves). In `MacroStudioRecordsInputIntoSelectedConditionActions`, keep `StartMacroRecording(targetsCondition: true)` — the new call must still contain that named argument.

- [ ] **Step 2: Run tests to verify they fail**

Expected: FAIL because `RecordingModeMenu.cs` does not exist.

- [ ] **Step 3: Add strings**

English:

- `RecordModeReplica` = `Replay timing`
- `RecordModeFixedDelay` = `Fixed 5 ms delay`
- `RecordModeNoDelay` = `No delay`
- Update `RecordingHelp` = `Choose a recording mode, then capture keyboard, mouse buttons, and wheel. Mouse movement is not recorded. Press Ctrl+Shift+F12 to stop.`

zh-CN:

- 复刻录入 / 延迟录入 / 无延迟录入
- `RecordingHelp` = `先选择录制模式，再记录键盘、鼠标按键和滚轮；不录入鼠标移动。按 Ctrl+Shift+F12 停止`

zh-TW: 復刻錄入 / 延遲錄入 / 無延遲錄入, and the matching help sentence.

- [ ] **Step 4: Create RecordingModeMenu and wire UI**

`src/ui/MacroStudio/RecordingModeMenu.cs`:

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using MacroHid.Core;

namespace MacroStudio;

internal static class RecordingModeMenu
{
    public static void Show(FrameworkElement placementTarget, Action<MacroRecordingMode> onSelected)
    {
        var menu = new ContextMenu();
        Add(menu, "RecordModeReplica", MacroRecordingMode.Replica, onSelected);
        Add(menu, "RecordModeFixedDelay", MacroRecordingMode.FixedDelay, onSelected);
        Add(menu, "RecordModeNoDelay", MacroRecordingMode.NoDelay, onSelected);
        menu.PlacementTarget = placementTarget;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private static void Add(
        ContextMenu menu,
        string resourceKey,
        MacroRecordingMode mode,
        Action<MacroRecordingMode> onSelected)
    {
        var item = new MenuItem { Header = LocalizationService.Get(resourceKey), Tag = mode };
        item.Click += (_, _) => onSelected(mode);
        menu.Items.Add(item);
    }
}
```

`SequencePanel`:
- Change `public event Action? RecordingStartRequested;` to `public event Action<MacroRecordingMode>? RecordingStartRequested;`
- In `RecordInputButton_Click` else branch: `RecordingModeMenu.Show(RecordInputButton, mode => RecordingStartRequested?.Invoke(mode));`

`ConditionDirectivePanel`: same event type change; in `RecordThenActions_Click` else branch show the menu on `RecordThenActionsButton`.

`MainWindow`:
- `private void OnStartRecording(MacroRecordingMode mode) => StartMacroRecording(targetsCondition: false, mode);`
- `private void OnStartConditionRecording(MacroRecordingMode mode) => StartMacroRecording(targetsCondition: true, mode);`
- Change `StartMacroRecording` to take `MacroRecordingMode mode` and:

```csharp
var options = MacroRecordingOptions.ForUserMode(mode);
var recorder = new MacroInputRecorder(options);
```

Keep minimizing, disposing the trigger hook, and Ctrl+Shift+F12 stop behavior.

- [ ] **Step 5: Update docs/usage.md**

Replace the Chinese recording paragraph (~line 40) with: clicking 录制输入 first chooses 复刻录入 (real gaps), 延迟录入 (5ms between inputs), or 无延迟录入; keyboard/buttons/wheel only; no mouse movement; Ctrl+Shift+F12 still stops; listening still pauses during recording.

Replace the English paragraph (~line 176) the same way.

Add under the trigger-key Chinese section (~line 110) and English playback section: title-bar **暂停监听** / **Pause listening** stops all trigger listening without clearing saved hotkeys; capturing a trigger or step key auto-pauses; Resume restores the previous listening set.

- [ ] **Step 6: Run tests to verify they pass**

Expected: new recording-menu test PASS; existing recording UI tests PASS.

- [ ] **Step 7: Commit**

```powershell
git add src/ui/MacroStudio/RecordingModeMenu.cs src/ui/MacroStudio/Controls/SequencePanel.xaml.cs src/ui/MacroStudio/Controls/ConditionDirectivePanel.xaml.cs src/ui/MacroStudio/MainWindow.xaml.cs src/ui/MacroStudio/Resources/Strings.resx src/ui/MacroStudio/Resources/Strings.zh-CN.resx src/ui/MacroStudio/Resources/Strings.zh-TW.resx docs/usage.md tests/MacroHid.Core.Tests/Program.cs
git commit -m "feat: choose recording timing mode before capturing input."
```

---

### Task 5: Pause listening (title bar + capture auto-pause)

**Files:**
- Modify: `src/ui/MacroStudio/MainWindow.xaml`
- Modify: `src/ui/MacroStudio/MainWindow.xaml.cs`
- Modify: `src/ui/MacroStudio/Controls/PlaybackPanel.xaml.cs`
- Modify: `src/ui/MacroStudio/Controls/StepEditorPanel.xaml.cs`
- Modify: `src/ui/MacroStudio/Resources/Strings.resx`
- Modify: `src/ui/MacroStudio/Resources/Strings.zh-CN.resx`
- Modify: `src/ui/MacroStudio/Resources/Strings.zh-TW.resx`
- Modify: `tests/MacroHid.Core.Tests/Program.cs`

- [ ] **Step 1: Write the failing UI source test**

```csharp
("MacroStudio can pause listening without clearing triggers", MacroStudioCanPauseListeningWithoutClearingTriggers),
```

```csharp
static void MacroStudioCanPauseListeningWithoutClearingTriggers()
{
    var xaml = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "MainWindow.xaml"));
    var main = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "MainWindow.xaml.cs"));
    var playback = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "PlaybackPanel.xaml.cs"));
    var editor = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Controls", "StepEditorPanel.xaml.cs"));
    var simplified = File.ReadAllText(Path.Combine("src", "ui", "MacroStudio", "Resources", "Strings.zh-CN.resx"));

    Assert.Contains("PauseListeningButton", xaml);
    Assert.Contains("listeningPaused", main);
    Assert.Contains("pausedListeningIds", main);
    Assert.Contains("PauseListeningForCapture", main);
    Assert.Contains("ResumeListeningAfterCapture", main);
    Assert.Contains("ListeningPausedHint", main);
    Assert.Contains("TriggerCaptureStarted", playback);
    Assert.Contains("TriggerCaptureFinished", playback);
    Assert.Contains("AnyKeyCaptureStarted", editor);
    Assert.Contains("AnyKeyCaptureFinished", editor);
    Assert.Contains("暂停监听", simplified);
    Assert.Contains("恢复监听", simplified);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Expected: FAIL; `PauseListeningButton` is missing.

- [ ] **Step 3: Add strings**

| Key | en | zh-CN | zh-TW |
|-----|----|-------|-------|
| PauseListening | Pause listening | 暂停监听 | 暫停監聽 |
| ResumeListening | Resume listening | 恢复监听 | 恢復監聽 |
| ListeningPausedHint | Resume listening before starting listeners. | 请先恢复监听，再开始监听。 | 請先恢復監聽，再開始監聽。 |
| ListeningPausedStatus | Listening paused. Saved triggers are unchanged. | 已暂停监听。已保存的触发键不会被删除。 | 已暫停監聽。已儲存的觸發鍵不會被刪除。 |
| ListeningResumePartial | Restored {0} listener(s). | 已恢复 {0} 个监听。 | 已恢復 {0} 個監聽。 |

- [ ] **Step 4: Add the title-bar button**

In `MainWindow.xaml`, inside the right-hand `StackPanel` of `CommandBar`, **before** `LanguageLabelText`:

```xml
<Button x:Name="PauseListeningButton"
        Style="{StaticResource CommandBarButton}"
        MinWidth="88"
        Margin="0,0,12,0"
        Content="暂停监听"
        Click="PauseListeningButton_Click" />
```

In `ApplyLocalization`, call `UpdatePauseListeningButton()`.

- [ ] **Step 5: Implement pause state in MainWindow**

Add fields:

```csharp
private bool listeningPaused;
private bool pauseOwnedByCapture;
private readonly List<string> pausedListeningIds = [];
```

```csharp
private void PauseListeningButton_Click(object sender, RoutedEventArgs e)
{
    if (listeningPaused && !pauseOwnedByCapture)
    {
        ResumePausedListening();
        return;
    }

    PauseListening(userRequested: true);
}

private void PauseListening(bool userRequested)
{
    if (!listeningPaused)
    {
        pausedListeningIds.Clear();
        pausedListeningIds.AddRange(listeningControllers.Keys);
        OnStopPlayback();
        listening = false;
        keyboardHook?.Dispose();
        keyboardHook = null;
        StopListeningControllers();
        RefreshLibraryListeningState();
        PlaybackPanelControl.SetPlaybackStatus(L("PlaybackStatusIdle"));
        PlaybackPanelControl.SetPlaybackResult(L("HotkeyListenerStopped"));
    }

    listeningPaused = true;
    if (userRequested)
    {
        pauseOwnedByCapture = false;
    }

    UpdatePauseListeningButton();
    SetStatus(L("ListeningPausedStatus"));
}

private void ResumePausedListening()
{
    listeningPaused = false;
    pauseOwnedByCapture = false;
    UpdatePauseListeningButton();
    var ids = pausedListeningIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
    pausedListeningIds.Clear();
    if (ids.Count == 0)
    {
        SetStatus(L("Idle"));
        return;
    }

    try
    {
        var candidates = BuildListeningCandidates()
            .Where(candidate => ids.Contains(candidate.Item.Id))
            .ToList();
        if (candidates.Count == 0)
        {
            SetStatus(L("Idle"));
            return;
        }

        var count = StartListeningCandidates(candidates, ids);
        PlaybackPanelControl.SetPlaybackStatus($"{L("PlaybackStatusListening")} ({count})");
        SetStatus(LocalizationService.Format("ListeningResumePartial", count));
    }
    catch (Exception ex)
    {
        SetStatus(ex.Message);
    }
}

private void PauseListeningForCapture()
{
    if (listeningPaused)
    {
        return;
    }

    PauseListening(userRequested: false);
    pauseOwnedByCapture = true;
}

private void ResumeListeningAfterCapture()
{
    if (!listeningPaused || !pauseOwnedByCapture)
    {
        return;
    }

    ResumePausedListening();
}

private bool BlockListeningWhilePaused()
{
    if (!listeningPaused)
    {
        return false;
    }

    SetStatus(L("ListeningPausedHint"));
    return true;
}

private void UpdatePauseListeningButton()
{
    PauseListeningButton.Content = L(listeningPaused ? "ResumeListening" : "PauseListening");
    PauseListeningButton.Style = (Style)FindResource(
        listeningPaused ? "DangerButton" : "CommandBarButton");
}
```

At the start of `OnStartCurrentListening` and `OnStartListeningGroups`:

```csharp
if (BlockListeningWhilePaused())
{
    return;
}
```

In `OnStopListeningAll`, after stopping controllers, if `listeningPaused` then `pausedListeningIds.Clear();` (stay paused; Resume will not restart those macros).

Subscribe in `InitializePanels`:

```csharp
PlaybackPanelControl.TriggerCaptureStarted += PauseListeningForCapture;
PlaybackPanelControl.TriggerCaptureFinished += ResumeListeningAfterCapture;
StepEditorPanel.AnyKeyCaptureStarted += PauseListeningForCapture;
StepEditorPanel.AnyKeyCaptureFinished += ResumeListeningAfterCapture;
```

Do **not** clear `PlaybackSettings.Trigger` anywhere in this task.

- [ ] **Step 6: Raise capture events from PlaybackPanel and StepEditorPanel**

`PlaybackPanel`:

```csharp
public event Action? TriggerCaptureStarted;
public event Action? TriggerCaptureFinished;
```

In `CaptureTrigger_Click` when starting capture, invoke `TriggerCaptureStarted` after setting `capturingTrigger = true`.

In `StopCapture`, after the early-return guard, invoke `TriggerCaptureFinished` (covers cancel, commit, and read-only interrupt because they all call `StopCapture`).

`StepEditorPanel`:

```csharp
public static event Action? AnyKeyCaptureStarted;
public static event Action? AnyKeyCaptureFinished;
```

In `CaptureStepKey_Click` when starting: `AnyKeyCaptureStarted?.Invoke();`

In `StopStepKeyCapture` after the early-return guard: `AnyKeyCaptureFinished?.Invoke();`

Static events cover the inline editor and `ThenActionsEditorWindow`.

- [ ] **Step 7: Run tests to verify they pass**

Expected: `PASS MacroStudio can pause listening without clearing triggers`.

- [ ] **Step 8: Commit**

```powershell
git add src/ui/MacroStudio/MainWindow.xaml src/ui/MacroStudio/MainWindow.xaml.cs src/ui/MacroStudio/Controls/PlaybackPanel.xaml.cs src/ui/MacroStudio/Controls/StepEditorPanel.xaml.cs src/ui/MacroStudio/Resources/Strings.resx src/ui/MacroStudio/Resources/Strings.zh-CN.resx src/ui/MacroStudio/Resources/Strings.zh-TW.resx tests/MacroHid.Core.Tests/Program.cs
git commit -m "feat: pause macro listening from the title bar and during key capture."
```

---

## Self-review

**Spec coverage**
- Hide timing except mouse move / wait editor: Task 3
- 5ms wait on press/release templates including pixel: Task 1
- Capture modifiers, drop checkboxes, localize Down/Up: Task 3
- Three recording modes, no mouse move, shared menu: Tasks 2 and 4
- Title-bar pause, auto-pause on capture, do not delete triggers, stop-all clears restore list: Task 5
- usage.md: Task 4

**Type names (locked)**
- `InputPressTiming.DefaultPressReleaseGap`
- `MacroRecordingMode` = Replica | FixedDelay | NoDelay
- `MacroRecordingOptions.ForUserMode`
- `RecordingModeMenu.Show`
- `listeningPaused`, `pausedListeningIds`, `pauseOwnedByCapture`
- `PauseListeningForCapture` / `ResumeListeningAfterCapture`
- `StepEditorPanel.AnyKeyCaptureStarted` / `AnyKeyCaptureFinished`
- `PlaybackPanel.TriggerCaptureStarted` / `TriggerCaptureFinished`
