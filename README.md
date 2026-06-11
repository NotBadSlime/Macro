# MacroHID

MacroHID is a Windows input macro project for legal local desktop automation. It combines a .NET macro runtime, Windows `SendInput` submission, visible-desktop pixel sampling, a WPF editor, built-in macro conversion, and latency diagnostics.

## Current State

- `.NET` solution: builds the core macro model, `.mcrx` parser/serializer, conversion library, input action compiler, SendInput runtime, latency probe, MacroRunner, and WPF studio.
- Macro execution expands steps such as `key.tap`, `key.text`, `mouse.click`, `consumer.tap`, waits, repeats, and pixel branches into scheduled input actions.
- `MacroRunner` can dry-run `.mcrx` files into an input action timeline, or submit those actions through `SendInput` with `--send`.
- `MacroStudio` supports English, Simplified Chinese, and Traditional Chinese UI text with a runtime language selector, and requests Administrator privileges on startup so it can automate elevated desktop apps.
- Diagnostics probe the live visible-desktop pixel sampler and the SendInput backend.
- MacroStudio has a built-in import/export panel for MacroHID `.mcrx`, MacroConverter XML, Razer Synapse XML, Lua/Logitech Lua, XMouse, and QMacro scripts. It does not bundle or launch the sibling Electron MacroConverter app.
- The project is pure user-mode. It does not install a driver, does not use test signing, and does not require Secure Boot changes.

## Build and Test

```powershell
dotnet build MacroHID.sln
dotnet run --project tests\MacroHid.Core.Tests\MacroHid.Core.Tests.csproj
dotnet run --project src\tools\LatencyProbe\LatencyProbe.csproj -- --iterations 2000 --interval-us 1000
dotnet run --project src\tools\MacroRunner\MacroRunner.csproj -- --macro samples\baseline.mcrx --pixels match
dotnet run --project src\ui\MacroStudio\MacroStudio.csproj
```

Submit a sample macro through SendInput:

```powershell
dotnet run --project src\tools\MacroRunner\MacroRunner.csproj -- --macro samples\baseline.mcrx --send --pixels skip
```

Build the Inno Setup installer:

```powershell
.\scripts\Build-Installer.ps1 -Configuration Release
```

Build and launch a local framework-dependent verification folder:

```powershell
.\scripts\Build-LocalRun.ps1 -Configuration Release
.\artifacts\local-run\MacroStudio\MacroStudio.exe
```

Use `.\scripts\Build-LocalRun.ps1 -Configuration Release -Launch` to start MacroStudio immediately after publishing. The local run folder depends on the installed .NET 8 runtime.

## GitHub Actions

The repository includes `.github/workflows/ci.yml`.

- The `.NET core, tools, and WPF` job builds `MacroHID.sln`, runs the core tests, runs a short `LatencyProbe` smoke test, runs a `MacroRunner` dry-run smoke test, and uploads `.NET` artifacts.
- The `Installer` job builds `MacroHID-Setup-x64.exe` with Inno Setup and uploads it as an artifact.

## Project Layout

- `src/shared/MacroHid.Core` - macro document model, parser, scheduler, input action compiler, pixel condition helpers, and latency statistics.
- `src/shared/MacroHid.Converter` - built-in import/export adapters for MacroHID, MacroConverter XML, Razer, Lua, XMouse, and QMacro formats.
- `src/shared/MacroHid.Runtime` - SendInput backend, playback runtime, pixel sampler, diagnostics, and Win32 interop.
- `src/tools/LatencyProbe` - user-mode scheduler jitter and input encoding benchmark.
- `src/tools/MacroRunner` - `.mcrx` dry-run and SendInput execution harness.
- `src/ui/MacroStudio` - WPF macro editor, hotkey playback, diagnostics, localization, and built-in import/export tools.
- `samples` - sample `.mcrx` macros.
- `installer` - Inno Setup script and localized installer language files.

## Scope Boundary

MacroHID targets normal desktop applications and elevated local applications. MacroStudio requests Administrator privileges by default; run MacroRunner as Administrator too when targeting elevated/admin applications. It does not include bypasses for anti-cheat, protected processes, secure desktop, UAC prompts, or other security boundaries.
