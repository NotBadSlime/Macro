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
