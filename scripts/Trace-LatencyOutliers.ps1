param(
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",

    [int]$Iterations = 5000,
    [int]$LoopSteps = 10,
    [int]$LoopIntervalUs = 1000,
    [int]$OutlierThresholdUs = 250,
    [string]$NativeEngine = "auto",
    [string]$AffinityMask = "",
    [string]$OutputDirectory = "artifacts\latency-trace"
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$outputRoot = Join-Path $repoRoot $OutputDirectory
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null

$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$tracePath = Join-Path $outputRoot "macrohid-latency-$timestamp.etl"
$csvPath = Join-Path $outputRoot "macrohid-latency-$timestamp-outliers.csv"
$logPath = Join-Path $outputRoot "macrohid-latency-$timestamp.log"

$latencyProbe = Join-Path $repoRoot "artifacts\local-run\LatencyProbe\LatencyProbe.exe"
if (-not (Test-Path $latencyProbe)) {
    & (Join-Path $PSScriptRoot "Build-LocalRun.ps1") -Configuration $Configuration
}

if (-not (Test-Path $latencyProbe)) {
    throw "LatencyProbe.exe was not found at $latencyProbe"
}

$wprCommand = Get-Command "wpr.exe" -ErrorAction SilentlyContinue
if (-not $wprCommand) {
    throw "wpr.exe was not found. Install Windows Performance Toolkit or Windows ADK."
}

function Assert-LastExitCode {
    param(
        [string]$Operation,
        [string]$Hint = ""
    )

    if ($LASTEXITCODE -ne 0) {
        $message = "$Operation failed with exit code $LASTEXITCODE."
        if (-not [string]::IsNullOrWhiteSpace($Hint)) {
            $message = "$message $Hint"
        }

        throw $message
    }
}

$probeArgs = @(
    "--backend", "native",
    "--profile", "ultra",
    "--native-engine", $NativeEngine,
    "--warm-native",
    "--cpu-scan",
    "--precreate-native-plan",
    "--loop-steps", "$LoopSteps",
    "--loop-interval-us", "$LoopIntervalUs",
    "--iterations", "$Iterations",
    "--outlier-threshold-us", "$OutlierThresholdUs",
    "--trace-outliers", $csvPath
)

if (-not [string]::IsNullOrWhiteSpace($AffinityMask)) {
    $probeArgs += @("--affinity-mask", $AffinityMask)
}

Write-Host "Starting WPR CPU trace with DPC and ISR context..."
Write-Host "Running: wpr.exe -start CPU -filemode"
& $wprCommand.Source -start CPU -filemode
Assert-LastExitCode "wpr.exe -start" "Run this script from an elevated administrator PowerShell session."

try {
    Write-Host "Running LatencyProbe..."
    & $latencyProbe @probeArgs 2>&1 | Tee-Object -FilePath $logPath
    Assert-LastExitCode "LatencyProbe.exe"
}
finally {
    Write-Host "Stopping WPR trace..."
    Write-Host "Running: wpr.exe -stop $tracePath"
    & $wprCommand.Source -stop $tracePath
    Assert-LastExitCode "wpr.exe -stop" "If WPR reports that no trace profile is running, rerun from an elevated administrator PowerShell session."
    if (-not (Test-Path $tracePath)) {
        throw "WPR did not create the expected ETW trace: $tracePath"
    }
}

Write-Host "ETW trace: $tracePath"
Write-Host "Outlier CSV: $csvPath"
Write-Host "Probe log: $logPath"
Write-Host "Open the .etl in Windows Performance Analyzer and inspect CPU Usage (Precise), DPC, ISR, and context-switch activity around the CSV outlier timestamps."
