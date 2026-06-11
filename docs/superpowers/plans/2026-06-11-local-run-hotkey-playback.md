# Local Run Hotkey Playback Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** Build a framework-dependent local run output and add global hotkey playback controls with toggle loop, hold loop, and fixed-count modes.

**Architecture:** Extend `MacroHid.Core` with playback settings and trigger parsing, then add `MacroHid.Runtime` for shared playback execution, cancellation, and hotkey event handling. `MacroRunner` and `MacroStudio` become hosts over the shared runtime, while `scripts\Build-LocalRun.ps1` produces a local executable verification folder.

**Tech Stack:** C#/.NET 8, WPF, Windows low-level keyboard hook (`WH_KEYBOARD_LL`), QPC timing, PowerShell, existing custom console test harness.

---

### Task 1: Add Playback Settings to MCRX

**Files:**
- Modify: `src/shared/MacroHid.Core/MacroModel.cs`
- Modify: `src/shared/MacroHid.Core/McrxParser.cs`
- Modify: `tests/MacroHid.Core.Tests/Program.cs`
- Modify: `docs/mcrx-format.md`

- [x] **Step 1: Write failing parser tests**

Add tests named:

```csharp
("MCRX parser covers playback hotkey settings", McrxParserCoversPlaybackHotkeySettings),
("MCRX parser defaults missing playback settings", McrxParserDefaultsMissingPlaybackSettings),
("MCRX parser rejects invalid playback settings", McrxParserRejectsInvalidPlaybackSettings),
```

The tests should assert:

```csharp
Assert.Equal(PlaybackMode.ToggleLoop, document.Playback.Mode);
Assert.Equal(1, document.Playback.Count);
Assert.Equal(HidKey.F8, document.Playback.Trigger!.Key);
Assert.Equal(HidModifier.LeftCtrl | HidModifier.LeftAlt, document.Playback.Trigger.Modifiers);
Assert.Equal("Ctrl+Alt+F8", document.Playback.Trigger.ToString());
Assert.Equal(PlaybackMode.FixedCount, missing.Playback.Mode);
Assert.Equal(1, missing.Playback.Count);
Assert.Equal(null, missing.Playback.Trigger);
Assert.Throws<JsonException>(() => McrxParser.Parse(invalidCountJson));
Assert.Throws<JsonException>(() => McrxParser.Parse(invalidTriggerJson));
```

- [x] **Step 2: Verify tests fail**

Run:

```powershell
dotnet run --project tests\MacroHid.Core.Tests\MacroHid.Core.Tests.csproj --configuration Release
```

Expected: fails because `MacroDocument.Playback`, `PlaybackMode`, and `HotkeyGesture` do not exist.

- [x] **Step 3: Implement playback model and parser**

Add:

```csharp
public sealed record MacroDocument(int Version, string Name, PlaybackSettings Playback, IReadOnlyList<MacroStep> Steps);
public sealed record PlaybackSettings(HotkeyGesture? Trigger, PlaybackMode Mode, int Count);
public sealed record HotkeyGesture(HidModifier Modifiers, HidKey Key);
public enum PlaybackMode { ToggleLoop, HoldLoop, FixedCount }
```

Update existing `new MacroDocument(...)` calls with `PlaybackSettings.Default`.

Parse optional root `playback` object:

- `trigger`: `null` or string
- `mode`: `toggleLoop`, `holdLoop`, `fixedCount`
- `count`: default `1`, must be `>= 1`

Parse trigger parts split by `+`, accepting `Ctrl`, `Shift`, `Alt`, `Win`, left modifier flags by default, and one final non-modifier HID key.

- [x] **Step 4: Verify parser tests pass**

Run:

```powershell
dotnet run --project tests\MacroHid.Core.Tests\MacroHid.Core.Tests.csproj --configuration Release
```

Expected: all tests pass.

### Task 2: Add Shared Runtime Playback Engine

**Files:**
- Create: `src/shared/MacroHid.Runtime/MacroHid.Runtime.csproj`
- Create: `src/shared/MacroHid.Runtime/MacroPlaybackController.cs`
- Create: `src/shared/MacroHid.Runtime/MacroPlaybackExecutor.cs`
- Create: `src/shared/MacroHid.Runtime/DriverMacroReportSink.cs`
- Create: `src/shared/MacroHid.Runtime/ScreenPixelSampler.cs`
- Create: `src/shared/MacroHid.Runtime/RuntimeNativeMethods.cs`
- Modify: `MacroHID.sln`
- Modify: `tests/MacroHid.Core.Tests/MacroHid.Core.Tests.csproj`
- Modify: `tests/MacroHid.Core.Tests/Program.cs`

- [x] **Step 1: Write failing runtime tests**

Add tests for:

```csharp
Playback controller starts and stops toggle loop on trigger press.
Playback controller runs fixed count once by default.
Playback controller cancels hold loop when trigger is released.
Playback executor checks cancellation before submitting delayed reports.
```

Use a fake report sink that records submitted reports and a test delay strategy that can cancel before delayed submission.

- [x] **Step 2: Verify tests fail**

Run:

```powershell
dotnet run --project tests\MacroHid.Core.Tests\MacroHid.Core.Tests.csproj --configuration Release
```

Expected: fails because `MacroHid.Runtime` does not exist.

- [x] **Step 3: Implement minimal runtime**

Implement:

```csharp
public enum PlaybackStatus { Idle, Listening, Running, Stopping, DriverMissing, Error }
public enum PixelEvaluationMode { Skip, MatchAll, Live }
public interface IMacroReportSink { bool IsAvailable { get; } void Submit(uint sequence, byte[] report); MacroDriverStats? GetStats(); }
public sealed class MacroPlaybackExecutor { Task<PlaybackRunResult> RunAsync(...); }
public sealed class MacroPlaybackController { Task TriggerPressedAsync(); Task TriggerReleasedAsync(); Task RunNowAsync(); void Stop(); }
```

The executor compiles reports per iteration, waits with cancellation, submits reports through the sink, and sends safe release reports on cancellation.

- [x] **Step 4: Verify runtime tests pass**

Run:

```powershell
dotnet run --project tests\MacroHid.Core.Tests\MacroHid.Core.Tests.csproj --configuration Release
```

Expected: all tests pass.

### Task 3: Refactor MacroRunner onto Runtime

**Files:**
- Modify: `src/tools/MacroRunner/MacroRunner.csproj`
- Modify: `src/tools/MacroRunner/Program.cs`
- Modify: `tests/MacroHid.Core.Tests/Program.cs`

- [x] **Step 1: Write a regression smoke expectation**

Keep existing dry-run output shape:

```text
MacroHID MacroRunner
reports=7 pixelMode=MatchAll send=False
#000001 due=0us id=0x01 ...
```

- [x] **Step 2: Refactor Program.cs**

Move driver client and pixel sampler use to `MacroHid.Runtime`. Keep option names:

```text
--macro
--dry-run
--send
--pixels skip|match|live
--no-wait
```

- [x] **Step 3: Verify MacroRunner**

Run:

```powershell
dotnet run --project src\tools\MacroRunner\MacroRunner.csproj --configuration Release -- --macro samples\baseline.mcrx --pixels match
```

Expected: dry-run report output still appears and exits 0.

### Task 4: Add MacroStudio Playback UI and Global Hook

**Files:**
- Modify: `src/ui/MacroStudio/MacroStudio.csproj`
- Create: `src/ui/MacroStudio/GlobalKeyboardHook.cs`
- Modify: `src/ui/MacroStudio/MainWindow.xaml`
- Modify: `src/ui/MacroStudio/MainWindow.xaml.cs`

- [x] **Step 1: Add UI controls**

Add a playback panel with:

```text
Trigger
Capture
Mode
Count
Start Listening
Stop Listening
Run Now
Stop
Playback status
Last result
```

- [x] **Step 2: Implement hook wrapper**

Use `SetWindowsHookEx(WH_KEYBOARD_LL)` and emit trigger pressed/released events. Ignore injected keyboard events by checking `LLKHF_INJECTED`.

- [x] **Step 3: Connect UI to runtime**

Validate editor JSON, update the `playback` JSON object when controls change, and use `MacroPlaybackController` for run/listen/stop behavior.

- [x] **Step 4: Verify UI builds**

Run:

```powershell
dotnet build src\ui\MacroStudio\MacroStudio.csproj --configuration Release
```

Expected: build succeeds.

### Task 5: Add Local Run Build Script

**Files:**
- Create: `scripts/Build-LocalRun.ps1`
- Modify: `README.md`
- Modify: `docs/installer.md`

- [x] **Step 1: Write script**

Create a framework-dependent publisher:

```powershell
.\scripts\Build-LocalRun.ps1 -Configuration Release
.\scripts\Build-LocalRun.ps1 -Configuration Release -Launch
```

Output:

```text
artifacts\local-run\MacroStudio\MacroStudio.exe
artifacts\local-run\MacroRunner\MacroRunner.exe
artifacts\local-run\LatencyProbe\LatencyProbe.exe
```

- [x] **Step 2: Verify local run output**

Run:

```powershell
.\scripts\Build-LocalRun.ps1 -Configuration Release
Test-Path artifacts\local-run\MacroStudio\MacroStudio.exe
.\artifacts\local-run\MacroRunner\MacroRunner.exe --macro .\artifacts\local-run\samples\baseline.mcrx --pixels match
```

Expected: executable exists and dry-run succeeds.

### Task 6: Full Verification and Commit

**Files:**
- All touched files.

- [x] **Step 1: Run all verification commands**

```powershell
dotnet build MacroHID.sln --configuration Release
dotnet run --project tests\MacroHid.Core.Tests\MacroHid.Core.Tests.csproj --configuration Release
dotnet run --project src\tools\MacroRunner\MacroRunner.csproj --configuration Release -- --macro samples\baseline.mcrx --pixels match
.\scripts\Build-LocalRun.ps1 -Configuration Release
.\scripts\Build-Installer.ps1 -Configuration Release -SkipNativeBuild
```

- [x] **Step 2: Commit and push**

```powershell
git status --short
git add .
git commit -m "feat: add hotkey playback runtime"
git push origin main
```

Expected: CI starts on GitHub Actions and completes successfully.
