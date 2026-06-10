# MacroHID

MacroHID is a Windows automation project for legal local desktop workflows. It combines a virtual HID driver, a high-priority macro engine, a WPF editor, and latency diagnostics.

## Current State

- `.NET` solution: builds the core macro model, `.mcrx` parser, HID report encoder, latency probe, and WPF studio.
- Native driver/service source: VHF/KMDF driver skeleton and C++ driver client are present under `src/driver` and `src/service`.
- Driver build/install requires Visual Studio 2022 with C++ workload and WDK. This machine currently has .NET 8 but not the VC/WDK command-line tools in `PATH`.

## Build and Test

```powershell
dotnet build MacroHID.sln
dotnet run --project tests\MacroHid.Core.Tests\MacroHid.Core.Tests.csproj
dotnet run --project src\tools\LatencyProbe\LatencyProbe.csproj -- --iterations 2000 --interval-us 1000
dotnet run --project src\ui\MacroStudio\MacroStudio.csproj
```

## GitHub Actions

The repository includes `.github/workflows/ci.yml`.

- The `.NET core, tools, and WPF` job builds `MacroHID.sln`, runs the core tests, runs a short `LatencyProbe` smoke test, and uploads `MacroStudio` plus `LatencyProbe` artifacts.
- The `Native service and VHF driver` job runs on `windows-2022`, initializes MSBuild, restores native packages, and builds the C++ service plus VHF/KMDF driver with `msbuild`.

Use `windows-2022` instead of `windows-latest` for now so the CI environment stays aligned with Visual Studio 2022 and the WDK assumptions in this project.

## Project Layout

- `src/shared/MacroHid.Core` - macro document model, parser, scheduler, HID report encoder, pixel condition helpers, latency statistics.
- `src/shared/MacroHidProtocol` - shared C/C++ IOCTL protocol for user-mode service and kernel driver.
- `src/driver/MacroHidDriver` - KMDF + Virtual HID Framework source driver skeleton.
- `src/service/MacroEngineService` - C++ driver client and first smoke-test service entry.
- `src/tools/LatencyProbe` - user-mode scheduler jitter and report encoding benchmark.
- `src/ui/MacroStudio` - WPF macro editor and diagnostics shell.
- `samples` - sample `.mcrx` macros.

## Scope Boundary

MacroHID targets normal desktop applications and elevated local applications. It does not include bypasses for anti-cheat, protected processes, secure desktop, or other security boundaries.
