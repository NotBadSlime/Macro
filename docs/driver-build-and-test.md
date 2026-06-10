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

The native projects are intentionally not added to `MacroHID.sln` on this machine because `VCTargetsPath` is unavailable without the C++ workload.

## Test Signing

Development builds use Windows test signing:

```powershell
bcdedit /set testsigning on
shutdown /r /t 0
```

After reboot, create a test certificate, sign the catalog, and install the driver package from the WDK build output. Exact certificate commands depend on your local certificate store policy.

## Install Smoke Test

After the test-signed driver is installed as `Root\MacroHid`, run:

```powershell
src\service\MacroEngineService\x64\Debug\MacroEngineService.exe
```

Expected behavior:

- The service finds the `GUID_DEVINTERFACE_MACROHID` device.
- `IOCTL_MACROHID_PING` succeeds.
- Two keyboard reports are submitted: Ctrl+A down, then release.
- `IOCTL_MACROHID_GET_STATS` reports at least two submitted reports and zero rejected reports.

## Notes

- VHF submission timing should be measured on the final driver build, not inferred from the `.NET` latency probe.
- Windows is not a hard real-time OS. Use p50/p95/p99 diagnostics as acceptance evidence instead of assuming sub-millisecond behavior on every machine.
