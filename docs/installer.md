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
- `MacroEngineService`
- the test-signed MacroHID driver package
- scripts, samples, and documentation

The installer is written to:

```text
artifacts\installer\MacroHID-Setup-x64.exe
```

## Driver Option

The installer can be used without installing the driver. This is useful for editing macros, validating `.mcrx` files, running dry-runs, and inspecting diagnostics on machines that are not configured for test drivers.

Installing the driver enables the real MacroHID path:

- virtual HID keyboard reports
- virtual HID mouse movement, buttons, and wheels
- consumer-control/media reports
- `MacroRunner --send`
- driver submit/reject statistics
- end-to-end latency measurements that include driver submission

Without the driver:

- `MacroStudio` still opens
- `MacroRunner` dry-run still prints scheduled HID reports
- `LatencyProbe` still measures user-mode scheduling
- no real virtual HID input is submitted
- `MacroRunner --send` fails with "MacroHID device not found"

## Test Driver Requirements

The current driver package is for development and test only. Installing it requires:

- Administrator rights
- Windows test-signing mode
- Secure Boot disabled if Windows blocks test-signing mode

For a production release, the driver must go through the Microsoft driver signing process. The installer can carry the production-signed package later without changing the app packaging model.

## Driver Install Shortcut

Use the Start menu shortcut named `Install MacroHID test driver` when installing the development driver after setup. The shortcut runs an interactive wrapper that:

- asks for Administrator rights when needed
- keeps the PowerShell window open on success or failure
- writes a log under `C:\ProgramData\MacroHID\logs`
- explains common test-signing and Secure Boot failures

If the optional driver install step is selected on the last installer page, the same interactive wrapper is used. If Windows reports that the test-signing value is protected by Secure Boot policy, disable Secure Boot in UEFI/BIOS, boot Windows again, run:

```powershell
powershell -ExecutionPolicy Bypass -File "C:\Program Files\MacroHID\scripts\Install-TestDriverInteractive.ps1" -EnableTestSigning -SkipBuild
```

Then reboot and run the Start menu shortcut again.
