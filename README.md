# MacroHID

MacroHID is a Windows automation project for legal local desktop workflows. It combines a virtual HID driver, a high-priority macro engine, a WPF editor, and latency diagnostics.

## Current State

- `.NET` solution: builds the core macro model, `.mcrx` parser, HID report encoder, latency probe, and WPF studio.
- Macro execution plan: expands high-level steps such as `key.tap`, `mouse.click`, `consumer.tap`, waits, repeats, and pixel branches into scheduled HID reports.
- `MacroRunner`: dry-runs `.mcrx` files into HID report timelines and can submit those reports to the MacroHID driver with `--send`.
- Native driver/service source: VHF/KMDF driver skeleton and C++ driver client are present under `src/driver` and `src/service`.
- Local build environment: Visual Studio 2022 Build Tools and WDK 10.0.26100 are supported by the native build scripts.

## Build and Test

```powershell
dotnet build MacroHID.sln
dotnet run --project tests\MacroHid.Core.Tests\MacroHid.Core.Tests.csproj
dotnet run --project src\tools\LatencyProbe\LatencyProbe.csproj -- --iterations 2000 --interval-us 1000
dotnet run --project src\tools\MacroRunner\MacroRunner.csproj -- --macro samples\baseline.mcrx --pixels match
dotnet run --project src\ui\MacroStudio\MacroStudio.csproj
```

Native build:

```powershell
.\scripts\Build-Native.ps1 -Configuration Release
```

Driver install and smoke test require Administrator PowerShell and Windows test signing:

```powershell
.\scripts\Install-TestDriver.ps1 -Configuration Release -EnableTestSigning
# Reboot if the script asks for it, then run the install script again without -EnableTestSigning.
.\scripts\Invoke-SmokeTest.ps1 -Send -Pixels skip
```

## GitHub Actions

The repository includes `.github/workflows/ci.yml`.

- The `.NET core, tools, and WPF` job builds `MacroHID.sln`, runs the core tests, runs a short `LatencyProbe` smoke test, and uploads `MacroStudio` plus `LatencyProbe` artifacts.
- The `.NET core, tools, and WPF` job also runs a `MacroRunner` dry-run smoke test and uploads `MacroRunner`.
- The `Native service and VHF driver` job runs on `windows-2022`, initializes MSBuild, verifies WDK/driver build tools, and builds the C++ service plus VHF/KMDF driver with `msbuild`.

Use `windows-2022` instead of `windows-latest` for now so the CI environment stays aligned with Visual Studio 2022 and the WDK assumptions in this project.

## Project Layout

- `src/shared/MacroHid.Core` - macro document model, parser, scheduler, HID report encoder, pixel condition helpers, latency statistics.
- `src/shared/MacroHidProtocol` - shared C/C++ IOCTL protocol for user-mode service and kernel driver.
- `src/driver/MacroHidDriver` - KMDF + Virtual HID Framework source driver skeleton.
- `src/service/MacroEngineService` - C++ driver client and first smoke-test service entry.
- `src/tools/LatencyProbe` - user-mode scheduler jitter and report encoding benchmark.
- `src/tools/MacroRunner` - `.mcrx` dry-run and optional driver-send execution harness.
- `src/ui/MacroStudio` - WPF macro editor and diagnostics shell.
- `samples` - sample `.mcrx` macros.

## Scope Boundary

MacroHID targets normal desktop applications and elevated local applications. It does not include bypasses for anti-cheat, protected processes, secure desktop, or other security boundaries.
