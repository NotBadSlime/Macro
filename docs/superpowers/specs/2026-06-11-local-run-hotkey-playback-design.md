# Local Run and Global Hotkey Playback Design

## Summary

MacroHID needs a development-friendly local build path and first-class global hotkey playback controls. After development work, a developer should be able to build a runnable local folder, launch `MacroStudio.exe`, configure a single-key or combination global hotkey, choose a playback mode, and stop execution at any time.

The local run build will depend on the machine's installed .NET 8 runtime. It will not create a self-contained runtime bundle.

## Goals

- Produce a local runnable app folder at `artifacts\local-run`.
- Launch `artifacts\local-run\MacroStudio\MacroStudio.exe` directly for verification.
- Support global keyboard hotkeys even when MacroStudio is not focused.
- Allow single-key hotkeys and modifier combinations such as `F8`, `Ctrl+Alt+F8`, or `Shift+F9`.
- Support three playback modes:
  - `toggleLoop`: press the trigger once to start looping, press it again to stop.
  - `holdLoop`: keep looping while the trigger is physically held, stop when it is released.
  - `fixedCount`: play the macro N times, with N defaulting to 1.
- Allow every mode to stop during execution through the trigger key or an explicit Stop command.
- Keep normal desktop automation as the scope boundary. Do not bypass secure desktop, anti-cheat, protected processes, or other security boundaries.

## Non-Goals

- Production driver signing.
- Service-session hotkeys.
- Secure desktop or lock-screen input.
- Anti-cheat bypass behavior.
- A self-contained .NET package for the local run build.

## Architecture

Add a shared runtime project named `MacroHid.Runtime`.

`MacroHid.Runtime` will own the reusable execution pieces that currently live mostly inside `MacroRunner`:

- macro loading from `.mcrx`
- pixel evaluation modes
- QPC-based report scheduling and wait strategy
- driver-device discovery and report submission
- cancellation-aware playback loops
- playback statistics and status events

`MacroRunner` will become a thin command-line host over `MacroHid.Runtime`.

`MacroStudio` will reference `MacroHid.Runtime` and provide the desktop UI for configuring hotkeys, selecting playback mode, starting hotkey listening, running the current macro, and stopping execution.

## Playback Settings

The `.mcrx` format will gain an optional `playback` object:

```json
{
  "version": 1,
  "name": "example",
  "playback": {
    "trigger": "Ctrl+Alt+F8",
    "mode": "toggleLoop",
    "count": 1
  },
  "steps": []
}
```

Rules:

- `trigger` is optional. If it is absent, MacroStudio can still run the macro manually.
- `trigger` accepts a single key or a `+`-joined combination.
- Modifiers are `Ctrl`, `Shift`, `Alt`, and `Win`.
- The final non-modifier key must be a supported keyboard key.
- `mode` defaults to `fixedCount`.
- `count` defaults to 1 and is only used by `fixedCount`.
- `count` must be at least 1.

The parser will keep older `.mcrx` files valid by treating missing `playback` as default manual playback settings.

## Hotkey Capture

Use a low-level keyboard hook with `WH_KEYBOARD_LL` for MacroStudio hotkey listening.

This is preferred over `RegisterHotKey` because `holdLoop` needs reliable key-down and key-up events. The hook will:

- track currently pressed physical keys
- recognize configured single-key and combination triggers
- ignore injected events when Windows marks them as injected
- emit `Pressed` and `Released` events to the playback controller
- avoid swallowing keys by default, so the configured trigger still reaches the foreground app unless a later setting changes that behavior

Global hotkeys can only be captured inside the current interactive desktop. If the target app is elevated and MacroStudio is not, normal Windows integrity rules may prevent consistent interaction. In that case MacroStudio should be run as Administrator.

## Playback Modes

`toggleLoop`:

- Trigger press when idle starts playback.
- Playback repeats until cancellation is requested.
- Trigger press while running requests cancellation.
- Explicit Stop requests cancellation.

`holdLoop`:

- Trigger press starts playback if idle.
- Playback repeats while the trigger remains physically held.
- Trigger release requests cancellation.
- Explicit Stop requests cancellation.

`fixedCount`:

- Trigger press when idle starts playback for `count` iterations.
- Trigger press while running requests cancellation.
- Explicit Stop requests cancellation.

Cancellation must be cooperative and checked:

- before compiling or submitting each iteration
- during QPC wait loops
- before each report submission
- between iterations

On cancellation, runtime should submit safe release reports for keyboard, mouse buttons, and consumer control where practical, so interrupted macros do not leave virtual controls stuck down.

## MacroStudio UI

Add a playback panel to the existing main window:

- trigger capture field
- mode selector
- count input enabled only for `fixedCount`
- Start Listening / Stop Listening
- Run Now
- Stop
- current status text
- last result or error text

The UI should validate the current macro before allowing run/listen. It should persist playback settings into the editor JSON when the user changes them, so saving a macro keeps its trigger and mode.

Status values:

- `Idle`
- `Listening`
- `Running`
- `Stopping`
- `Driver missing`
- `Error`

## Local Run Build

Add `scripts\Build-LocalRun.ps1`.

Default behavior:

- use `Release`
- publish framework-dependent `win-x64` outputs
- write to `artifacts\local-run`
- publish `MacroStudio`, `MacroRunner`, and `LatencyProbe`
- copy `samples`, `docs`, and driver install scripts
- keep native driver/service build optional

Parameters:

- `-Configuration Debug|Release`
- `-Platform x64`
- `-BuildNative`
- `-Launch`

When `-Launch` is supplied, the script starts `artifacts\local-run\MacroStudio\MacroStudio.exe` after publishing.

## Error Handling

Runtime operations return structured status rather than forcing UI code to parse console output.

Expected user-facing errors:

- invalid macro JSON
- unsupported trigger key
- hotkey hook registration failure
- driver not installed
- driver ping failure
- report submission failure
- pixel sampling failure when live pixel mode is selected

Errors should leave the runtime in `Idle` or `Listening`, not half-running.

## Testing

Core tests:

- parse `playback` with single-key trigger
- parse `playback` with modifier combination
- default missing playback to `fixedCount` and count 1
- reject invalid trigger strings
- reject `fixedCount` values below 1
- verify playback-mode state transitions
- verify cancellation can stop before a scheduled report is submitted

Tool tests:

- `MacroRunner` keeps existing dry-run behavior.
- `MacroRunner --send` uses the shared runtime path.

UI build tests:

- `MacroStudio` builds after referencing `MacroHid.Runtime`.
- playback controls bind to the current macro document.

Local run verification:

- `.\scripts\Build-LocalRun.ps1 -Configuration Release` produces `artifacts\local-run\MacroStudio\MacroStudio.exe`.
- `.\scripts\Build-LocalRun.ps1 -Configuration Release -Launch` starts MacroStudio.
- `MacroRunner` dry-run works from the local run folder.

Installer verification:

- Inno Setup continues to build.
- The installer can keep using the normal installer input path independently from `artifacts\local-run`.

## Acceptance Criteria

- A developer can run one script and launch the framework-dependent local build.
- A macro can define `Ctrl+Alt+F8` as a global trigger.
- `toggleLoop`, `holdLoop`, and `fixedCount` behave as specified.
- All three modes can be stopped during waits and before report submission.
- Existing macros without `playback` continue to parse and run.
- Existing CI build jobs remain green.
