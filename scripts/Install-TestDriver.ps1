#requires -RunAsAdministrator

param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [ValidateSet("x64")]
    [string]$Platform = "x64",

    [switch]$SkipBuild,

    [switch]$EnableTestSigning
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")

function Test-TestSigningEnabled {
    $bcd = bcdedit /enum "{current}" 2>$null
    return ($bcd -match "testsigning\s+Yes") -or ($bcd -match "testsigning\s+on")
}

function Find-WdkTool([string]$name) {
    $kitsRoot = "${env:ProgramFiles(x86)}\Windows Kits\10\Tools"
    if (-not (Test-Path $kitsRoot)) {
        return $null
    }

    return Get-ChildItem -Path $kitsRoot -Recurse -Filter $name -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match "\\x64\\" } |
        Select-Object -First 1 -ExpandProperty FullName
}

if ($EnableTestSigning) {
    $bcdeditOutput = bcdedit /set testsigning on 2>&1
    $bcdeditOutput | Out-Host
    if ($LASTEXITCODE -ne 0) {
        $message = ($bcdeditOutput | ForEach-Object {
            if ($_ -is [System.Management.Automation.ErrorRecord]) {
                $_.Exception.Message
            }
            else {
                [string]$_
            }
        }) -join [Environment]::NewLine
        $message = $message.Trim()
        throw "Failed to enable Windows test signing. If bcdedit says the value is protected by Secure Boot policy, disable Secure Boot in UEFI/BIOS, boot Windows again, then rerun this script with -EnableTestSigning. bcdedit exit code $LASTEXITCODE. Output: $message"
    }

    if (-not (Test-TestSigningEnabled)) {
        Write-Warning "Test signing was requested. Reboot Windows, then run this script again."
        exit 10
    }
}
elseif (-not (Test-TestSigningEnabled)) {
    throw "Windows test signing is not enabled. Run this script with -EnableTestSigning, reboot, then run it again."
}

if (-not $SkipBuild -and (Test-Path (Join-Path $repoRoot "src\driver\MacroHidDriver\MacroHidDriver.vcxproj"))) {
    & (Join-Path $PSScriptRoot "Build-Native.ps1") -Configuration $Configuration -Platform $Platform
}

$installedPackageDir = Join-Path $repoRoot "driver"
$sourceDriverOut = Join-Path $repoRoot "src\driver\MacroHidDriver\$Platform\$Configuration"
$sourcePackageDir = Join-Path $sourceDriverOut "MacroHidDriver"

if (Test-Path (Join-Path $installedPackageDir "MacroHidDriver.inf")) {
    $packageDir = $installedPackageDir
    $certPath = Join-Path $installedPackageDir "MacroHidDriver.cer"
}
else {
    $packageDir = $sourcePackageDir
    $certPath = Join-Path $sourceDriverOut "MacroHidDriver.cer"
}

$infPath = Join-Path $packageDir "MacroHidDriver.inf"

if (-not (Test-Path $infPath)) {
    throw "Driver package INF was not found at $infPath. Build the driver first."
}

if (Test-Path $certPath) {
    Import-Certificate -FilePath $certPath -CertStoreLocation Cert:\LocalMachine\TrustedPublisher | Out-Null
    Import-Certificate -FilePath $certPath -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
    Write-Host "Imported test certificate: $certPath"
}
else {
    Write-Warning "Test certificate was not found at $certPath. Driver install may fail if the certificate is not trusted."
}

pnputil /add-driver "$infPath" /install | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw "pnputil failed with exit code $LASTEXITCODE."
}

$devcon = Find-WdkTool "devcon.exe"
if ($devcon) {
    & $devcon install "$infPath" "Root\MacroHid" | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "devcon install failed with exit code $LASTEXITCODE."
    }
}
else {
    Write-Warning "devcon.exe was not found. The driver package was staged, but the Root\MacroHid device may not have been created."
}

Write-Host "MacroHID test driver install step finished."
Write-Host "Smoke test:"
Write-Host "  dotnet run --project src\tools\MacroRunner\MacroRunner.csproj -- --macro samples\baseline.mcrx --send --pixels skip"
