param(
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",

    [int]$Iterations = 1000,
    [int]$LoopSteps = 10,
    [int]$LoopIntervalUs = 1000,
    [string]$AffinityMask = "",
    [ValidateSet("auto", "standby", "inline")]
    [string]$NativeEngine = "auto",
    [switch]$AutoTuneAffinity,
    [int]$TuneIterations = 1000,
    [int]$TunePasses = 1,
    [int]$TuneWindowSize = 4,
    [string]$OutputDirectory = "artifacts\precision-profiles",
    [switch]$FailOnTargetMiss
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$outputRoot = Join-Path $repoRoot $OutputDirectory
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"

$latencyProbe = Join-Path $repoRoot "artifacts\local-run\LatencyProbe\LatencyProbe.exe"
if (-not (Test-Path $latencyProbe)) {
    & (Join-Path $PSScriptRoot "Build-LocalRun.ps1") -Configuration $Configuration
}

if (-not (Test-Path $latencyProbe)) {
    throw "LatencyProbe.exe was not found at $latencyProbe"
}

function Read-RegexValue {
    param(
        [string]$Text,
        [string]$Pattern,
        [string]$Fallback = ""
    )

    if ($Text -match $Pattern) {
        return $Matches[1]
    }

    return $Fallback
}

function Read-ProbeMetric {
    param(
        [string[]]$Lines,
        [string]$MetricName,
        [int]$DefaultValue = 0
    )

    $line = $Lines | Where-Object { $_ -like "*$MetricName*" } | Select-Object -Last 1
    if ([string]::IsNullOrWhiteSpace($line)) {
        return @{
            P50 = $DefaultValue
            P95 = $DefaultValue
            P99 = $DefaultValue
            P999 = $DefaultValue
            Max = $DefaultValue
            OutliersOverTarget = $DefaultValue
            Line = ""
        }
    }

    return @{
        P50 = [int](Read-RegexValue $line "p50=(-?\d+)us" "$DefaultValue")
        P95 = [int](Read-RegexValue $line "p95=(-?\d+)us" "$DefaultValue")
        P99 = [int](Read-RegexValue $line "p99=(-?\d+)us" "$DefaultValue")
        P999 = [int](Read-RegexValue $line "p99\.9=(-?\d+)us" "$DefaultValue")
        Max = [int](Read-RegexValue $line "max=(-?\d+)us" "$DefaultValue")
        OutliersOverTarget = [int](Read-RegexValue $line "outliersOverTarget=(\d+)" "$DefaultValue")
        Line = $line
    }
}

if ($AutoTuneAffinity -and [string]::IsNullOrWhiteSpace($AffinityMask)) {
    $tuneScript = Join-Path $PSScriptRoot "Tune-LatencyAffinity.ps1"
    $affinityOutputDirectory = Join-Path $OutputDirectory "affinity"
    $affinityTuneLog = Join-Path $outputRoot "affinity-autotune-$timestamp.log"
    Write-Host "AutoTuneAffinity enabled. Scanning affinity masks before precision profiles..."

    $tuneParameters = @{
        Configuration = $Configuration
        Iterations = $TuneIterations
        Passes = $TunePasses
        WindowSize = $TuneWindowSize
        LoopSteps = $LoopSteps
        LoopIntervalUs = $LoopIntervalUs
        OutlierThresholdUs = 100
        NativeEngine = $NativeEngine
        OutputDirectory = $affinityOutputDirectory
    }

    $tuneOutput = & $tuneScript @tuneParameters *>&1
    $tuneExitCode = $LASTEXITCODE
    $tuneOutput | Set-Content -Path $affinityTuneLog -Encoding UTF8
    if ($tuneExitCode -ne 0) {
        throw "Tune-LatencyAffinity.ps1 failed with exit code $tuneExitCode. See $affinityTuneLog"
    }

    $tuneText = [string]::Join([Environment]::NewLine, @($tuneOutput | ForEach-Object { "$_" }))
    $recommendedMask = Read-RegexValue $tuneText "recommended affinity mask:\s*(0x[0-9A-Fa-f]+)" ""
    if ([string]::IsNullOrWhiteSpace($recommendedMask)) {
        throw "Tune-LatencyAffinity.ps1 did not report a recommended affinity mask. See $affinityTuneLog"
    }

    $AffinityMask = $recommendedMask
    Write-Host "Auto-tuned affinity mask: $AffinityMask"
}

$profiles = @(
    [pscustomobject]@{
        Name = "Basic"
        Profile = "basic"
        Backend = "managed"
        NativeTier = "managed"
        TargetUs = 500
        MetricName = "loopStepJitter"
    },
    [pscustomobject]@{
        Name = "HighPerformance"
        Profile = "high"
        Backend = "auto"
        NativeTier = "native-lite"
        TargetUs = 250
        MetricName = "nativeBatchLate"
    },
    [pscustomobject]@{
        Name = "Extreme"
        Profile = "ultra"
        Backend = "native"
        NativeTier = "native-ultra"
        TargetUs = 100
        MetricName = "nativeBatchLate"
    }
)

$results = New-Object System.Collections.Generic.List[object]

foreach ($profile in $profiles) {
    $logPath = Join-Path $outputRoot "precision-$timestamp-$($profile.Name).log"
    $outlierPath = Join-Path $outputRoot "precision-$timestamp-$($profile.Name)-outliers.csv"

    $probeArgs = @(
        "--backend", $profile.Backend,
        "--profile", $profile.Profile,
        "--loop-steps", "$LoopSteps",
        "--loop-interval-us", "$LoopIntervalUs",
        "--iterations", "$Iterations",
        "--outlier-threshold-us", "$($profile.TargetUs)",
        "--trace-outliers", $outlierPath
    )

    if ($profile.NativeTier -eq "native-lite") {
        $probeArgs += @(
            "--native-engine", "inline"
        )
    }
    elseif ($profile.NativeTier -eq "native-ultra") {
        $probeArgs += @(
            "--native-engine", $NativeEngine,
            "--warm-native",
            "--cpu-scan",
            "--precreate-native-plan"
        )

        if (-not [string]::IsNullOrWhiteSpace($AffinityMask)) {
            $probeArgs += @("--affinity-mask", $AffinityMask)
        }
    }

    Write-Host "Running $($profile.Name) precision profile..."
    $probeOutput = & $latencyProbe @probeArgs 2>&1
    $probeExitCode = $LASTEXITCODE
    $probeOutput | Set-Content -Path $logPath -Encoding UTF8

    if ($probeExitCode -ne 0) {
        throw "LatencyProbe.exe failed for $($profile.Name) with exit code $probeExitCode. See $logPath"
    }

    $lines = @($probeOutput | ForEach-Object { "$_" })
    $allText = [string]::Join([Environment]::NewLine, $lines)
    $stepMetric = Read-ProbeMetric -Lines $lines -MetricName $profile.MetricName
    $usesNativeMetrics = $profile.MetricName -eq "nativeBatchLate"
    $loopEndMetric = if ($usesNativeMetrics) {
        Read-ProbeMetric -Lines $lines -MetricName "nativeLoopEndLate" -DefaultValue 0
    }
    else {
        Read-ProbeMetric -Lines $lines -MetricName "loopEndDrift"
    }

    $passed = $stepMetric.OutliersOverTarget -eq 0 -and $loopEndMetric.OutliersOverTarget -eq 0
    $result = [pscustomobject]@{
        Name = $profile.Name
        TargetUs = $profile.TargetUs
        Backend = $profile.Backend
        NativeTier = $profile.NativeTier
        EffectiveBackend = Read-RegexValue $stepMetric.Line "backend=([^ ]+)" $profile.Backend
        AffinityMask = if ($profile.NativeTier -eq "native-ultra" -and -not [string]::IsNullOrWhiteSpace($AffinityMask)) { $AffinityMask } else { "" }
        StepP50Us = $stepMetric.P50
        StepP95Us = $stepMetric.P95
        StepP99Us = $stepMetric.P99
        StepP999Us = $stepMetric.P999
        StepMaxUs = $stepMetric.Max
        StepOutliersOverTarget = $stepMetric.OutliersOverTarget
        LoopEndP999Us = $loopEndMetric.P999
        LoopEndMaxUs = $loopEndMetric.Max
        LoopEndOutliersOverTarget = $loopEndMetric.OutliersOverTarget
        PassedTarget = $passed
        NativeWarmupUs = if ($usesNativeMetrics) { [int](Read-RegexValue $allText "nativeWarmup=(\d+)us" "0") } else { 0 }
        NativeStartupUs = if ($usesNativeMetrics) { [int](Read-RegexValue $allText "nativeStartup=(\d+)us" "0") } else { 0 }
        SelectedCpu = if ($usesNativeMetrics) { Read-RegexValue $allText "selectedCpu=([^ ]+)" "unknown" } else { "" }
        LogPath = $logPath
        OutlierCsv = $outlierPath
    }

    $results.Add($result)
    Write-Host ("{0}: target={1}us step p99.9={2}us max={3}us outliers={4}; loopEnd max={5}us; pass={6}" -f `
        $result.Name,
        $result.TargetUs,
        $result.StepP999Us,
        $result.StepMaxUs,
        $result.StepOutliersOverTarget,
        $result.LoopEndMaxUs,
        $result.PassedTarget)
}

$summaryPath = Join-Path $outputRoot "precision-$timestamp-summary.csv"
$reportPath = Join-Path $outputRoot "precision-$timestamp-report.md"
$results | Export-Csv -Path $summaryPath -NoTypeInformation -Encoding UTF8
$AllProfilesPassed = -not ($results | Where-Object { -not $_.PassedTarget } | Select-Object -First 1)

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("# MacroHID precision report")
$lines.Add("")
$lines.Add("- Iterations: $Iterations")
$lines.Add("- Loop steps: $LoopSteps")
$lines.Add("- Loop interval: $($LoopIntervalUs)us")
$lines.Add("- Native engine: $NativeEngine")
$lines.Add("- Auto tune affinity: $AutoTuneAffinity")
$lines.Add("- Auto-tuned affinity mask: " + ($(if ($AutoTuneAffinity -and -not [string]::IsNullOrWhiteSpace($AffinityMask)) { $AffinityMask } else { "n/a" })))
$lines.Add("- Affinity mask: " + ($(if ([string]::IsNullOrWhiteSpace($AffinityMask)) { "default" } else { $AffinityMask })))
$lines.Add("- All profiles passed: $AllProfilesPassed")
$lines.Add("")
$lines.Add("| Profile | Target | Backend | Native tier | Effective backend | Step p99.9 | Step max | OutliersOverTarget | Loop-end max | Pass |")
$lines.Add("| --- | ---: | --- | --- | --- | ---: | ---: | ---: | ---: | --- |")
foreach ($result in $results) {
    $lines.Add("| $($result.Name) | $($result.TargetUs)us | $($result.Backend) | $($result.NativeTier) | $($result.EffectiveBackend) | $($result.StepP999Us)us | $($result.StepMaxUs)us | $($result.StepOutliersOverTarget) | $($result.LoopEndMaxUs)us | $($result.PassedTarget) |")
}

$lines.Add("")
$lines.Add("CSV: $summaryPath")
$lines.Add("Logs and outlier CSV files are stored next to this report.")
$lines | Set-Content -Path $reportPath -Encoding UTF8

Write-Host "Precision summary CSV: $summaryPath"
Write-Host "precision report: $reportPath"
Write-Host "AllProfilesPassed=$AllProfilesPassed"

if ($FailOnTargetMiss -and -not $AllProfilesPassed) {
    Write-Error "One or more precision profiles missed their target. See $reportPath"
    exit 2
}
