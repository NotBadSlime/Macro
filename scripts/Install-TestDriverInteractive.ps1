param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [ValidateSet("x64")]
    [string]$Platform = "x64",

    [switch]$SkipBuild,

    [switch]$EnableTestSigning
)

$ErrorActionPreference = "Stop"

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function New-InstallLogPath {
    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $candidates = @(
        (Join-Path $env:ProgramData "MacroHID\logs"),
        (Join-Path $env:TEMP "MacroHID")
    )

    foreach ($candidate in $candidates) {
        try {
            New-Item -ItemType Directory -Path $candidate -Force | Out-Null
            return (Join-Path $candidate "Install-TestDriver-$timestamp.log")
        }
        catch {
            continue
        }
    }

    return (Join-Path $env:TEMP "MacroHID-Install-TestDriver-$timestamp.log")
}

function Start-ElevatedSelf {
    $arguments = @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-NoExit",
        "-File", "`"$PSCommandPath`"",
        "-Configuration", $Configuration,
        "-Platform", $Platform
    )

    if ($SkipBuild) {
        $arguments += "-SkipBuild"
    }

    if ($EnableTestSigning) {
        $arguments += "-EnableTestSigning"
    }

    Start-Process -FilePath "powershell.exe" -ArgumentList $arguments -Verb RunAs
}

if (-not (Test-IsAdministrator)) {
    Write-Host "MacroHID driver installation requires Administrator rights."
    Write-Host "Launching an elevated PowerShell window..."
    try {
        Start-ElevatedSelf
        Write-Host "If the UAC prompt appears, approve it and continue in the elevated window."
        return
    }
    catch {
        Write-Host "Could not launch the elevated installer." -ForegroundColor Red
        Write-Host $_.Exception.Message -ForegroundColor Red
        Write-Host "Press Enter to close this window."
        Read-Host | Out-Null
        exit 1
    }
}

$logPath = New-InstallLogPath
$transcriptStarted = $false
$exitCode = 0

try {
    Write-Host "MacroHID test driver installer"
    Write-Host "Log file: $logPath"
    Write-Host ""

    try {
        Start-Transcript -Path $logPath -Force | Out-Null
        $transcriptStarted = $true
    }
    catch {
        Write-Warning "Could not start PowerShell transcript: $($_.Exception.Message)"
    }

    $installer = Join-Path $PSScriptRoot "Install-TestDriver.ps1"
    if (-not (Test-Path $installer)) {
        throw "Install-TestDriver.ps1 was not found next to this script."
    }

    $installArgs = @{
        Configuration = $Configuration
        Platform = $Platform
    }

    if ($SkipBuild) {
        $installArgs.SkipBuild = $true
    }

    if ($EnableTestSigning) {
        $installArgs.EnableTestSigning = $true
    }

    & $installer @installArgs

    Write-Host ""
    Write-Host "MacroHID test driver install step completed."
}
catch {
    $exitCode = 1
    Write-Host ""
    Write-Host "MacroHID test driver install failed." -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red

    if ($_.ScriptStackTrace) {
        Write-Host ""
        Write-Host $_.ScriptStackTrace
    }
}
finally {
    if ($transcriptStarted) {
        try {
            Stop-Transcript | Out-Null
        }
        catch {
            Write-Warning "Could not stop PowerShell transcript: $($_.Exception.Message)"
        }
    }

    Write-Host ""
    Write-Host "Log file: $logPath"
    Write-Host "Press Enter to close this window."
    Read-Host | Out-Null
}

exit $exitCode
