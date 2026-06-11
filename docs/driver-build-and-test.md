# Driver Build and Test

## Requirements

- Windows 10/11 x64.
- Visual Studio 2022 with Desktop development with C++.
- Windows Driver Kit matching the target SDK.
- Administrator PowerShell for install and test-signing steps.

## Build

Open a VS 2022 Developer Command Prompt with WDK installed:

```powershell
msbuild src\driver\MacroHidDriver\MacroHidDriver.vcxproj /p:Configuration=Debug /p:Platform=x64
msbuild src\service\MacroEngineService\MacroEngineService.vcxproj /p:Configuration=Debug /p:Platform=x64
```

Or use the repo script:

```powershell
.\scripts\Build-Native.ps1 -Configuration Release
```

## Test Signing

Development builds use Windows test signing:

```powershell
bcdedit /set testsigning on
shutdown /r /t 0
```

If `bcdedit /set testsigning on` reports that the value is protected by Secure Boot policy, disable Secure Boot in UEFI/BIOS first. Windows will not load a test-signed kernel driver while Secure Boot blocks test-signing mode.

After reboot, create a test certificate, sign the catalog, and install the driver package from the WDK build output. Exact certificate commands depend on your local certificate store policy.

The install script automates the normal development path:

```powershell
.\scripts\Install-TestDriverInteractive.ps1 -Configuration Release -EnableTestSigning
```

The interactive installer asks for Administrator rights when needed, keeps the PowerShell window open, and writes a log under `C:\ProgramData\MacroHID\logs`. If the script enables test signing, reboot Windows and run it again without `-EnableTestSigning`.

## Install Smoke Test

After the test-signed driver is installed as `Root\MacroHid`, run:

```powershell
src\service\MacroEngineService\x64\Debug\MacroEngineService.exe
dotnet run --project src\tools\MacroRunner\MacroRunner.csproj -- --macro samples\baseline.mcrx --send --pixels skip
```

Expected behavior:

- The service finds the `GUID_DEVINTERFACE_MACROHID` device.
- `IOCTL_MACROHID_PING` succeeds.
- Two keyboard reports are submitted: Ctrl+A down, then release.
- `IOCTL_MACROHID_GET_STATS` reports at least two submitted reports and zero rejected reports.
- `MacroRunner --send` parses a `.mcrx`, schedules HID reports with QPC timing, submits them, and prints submit latency percentiles.

Dry-run smoke test without an installed driver:

```powershell
.\scripts\Invoke-SmokeTest.ps1 -Pixels match
```

Installed-driver smoke test:

```powershell
.\scripts\Invoke-SmokeTest.ps1 -Send -Pixels skip
```

## Notes

- VHF submission timing should be measured on the final driver build, not inferred from the `.NET` latency probe.
- Windows is not a hard real-time OS. Use p50/p95/p99 diagnostics as acceptance evidence instead of assuming sub-millisecond behavior on every machine.
