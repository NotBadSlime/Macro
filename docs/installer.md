# MacroHID Installer

MacroHID uses Inno Setup to produce `MacroHID-Setup-x64.exe`.

## Build

```powershell
.\scripts\Build-Installer.ps1 -Configuration Release
```

The script publishes:

- `MacroStudio`
- `MacroRunner`
- `LatencyProbe`
- scripts, samples, and documentation
- built-in conversion libraries used by MacroStudio

The installer is written to:

```text
artifacts\installer\MacroHID-Setup-x64.exe
```

For development verification without creating or installing the setup package, use:

```powershell
.\scripts\Build-LocalRun.ps1 -Configuration Release -Launch
```

This creates `artifacts\local-run\MacroStudio\MacroStudio.exe` as a framework-dependent executable that uses the installed .NET 8 runtime.

## Runtime Behavior

MacroHID is pure user-mode and submits input through Windows `SendInput`.

- No driver is installed.
- No Windows test-signing mode is required.
- Secure Boot does not need to be changed.
- `MacroRunner --send` submits input directly through SendInput.
- Diagnostics show the visible-desktop pixel sampler and SendInput backend.
- MacroStudio includes built-in import/export for `.mcrx`, MacroConverter XML, Razer Synapse XML, Lua/Logitech Lua, XMouse, and QMacro files. No external Electron converter is installed.

To control an elevated/admin application, run MacroStudio or MacroRunner as Administrator so both processes are at the same integrity level. Secure desktop, UAC prompts, protected processes, and anti-cheat protected contexts are intentionally out of scope.
