#requires -RunAsAdministrator

$ErrorActionPreference = "Stop"

function Find-WdkTool([string]$name) {
    $kitsRoot = "${env:ProgramFiles(x86)}\Windows Kits\10\Tools"
    if (-not (Test-Path $kitsRoot)) {
        return $null
    }

    return Get-ChildItem -Path $kitsRoot -Recurse -Filter $name -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match "\\x64\\" } |
        Select-Object -First 1 -ExpandProperty FullName
}

$devcon = Find-WdkTool "devcon.exe"
if ($devcon) {
    & $devcon remove "Root\MacroHid" | Out-Host
}
else {
    Write-Warning "devcon.exe was not found. Skipping Root\MacroHid device removal."
}

$drivers = Get-CimInstance Win32_PnPSignedDriver |
    Where-Object {
        $_.DeviceID -like "ROOT\MACROHID*" -or
        $_.DeviceName -like "MacroHID*" -or
        $_.DriverProviderName -eq "MacroHID"
    } |
    Where-Object { $_.InfName }

foreach ($driver in $drivers) {
    pnputil /delete-driver $($driver.InfName) /uninstall /force | Out-Host
}

Write-Host "MacroHID test driver uninstall step finished."
