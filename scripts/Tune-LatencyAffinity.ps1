param(
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",

    [string[]]$Masks = @(),
    [int]$WindowSize = 4,
    [int]$Iterations = 1000,
    [int]$Passes = 1,
    [int]$LoopSteps = 10,
    [int]$LoopIntervalUs = 1000,
    [int]$OutlierThresholdUs = 250,
    [ValidateSet("auto", "standby", "inline")]
    [string]$NativeEngine = "auto",
    [string]$OutputDirectory = "artifacts\latency-affinity"
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$outputRoot = Join-Path $repoRoot $OutputDirectory
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null

$latencyProbe = Join-Path $repoRoot "artifacts\local-run\LatencyProbe\LatencyProbe.exe"
if (-not (Test-Path $latencyProbe)) {
    & (Join-Path $PSScriptRoot "Build-LocalRun.ps1") -Configuration $Configuration
}

if (-not (Test-Path $latencyProbe)) {
    throw "LatencyProbe.exe was not found at $latencyProbe"
}

function Add-AffinityMaskCandidate {
    param(
        [System.Collections.Generic.List[string]]$Candidates,
        [System.Collections.Generic.HashSet[string]]$CandidateSet,
        [uint64]$Value
    )

    if ($Value -eq 0) {
        return
    }

    $formatted = "0x{0:X}" -f $Value
    if ($CandidateSet.Add($formatted)) {
        $Candidates.Add($formatted)
    }
}

function New-DefaultAffinityMasks {
    param([int]$Size)

    $logicalCount = [Math]::Min([Environment]::ProcessorCount, 64)
    $effectiveWindow = [Math]::Max(1, [Math]::Min($Size, $logicalCount))
    $generated = New-Object System.Collections.Generic.List[string]
    $candidateSet = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)

    for ($start = 0; $start -lt $logicalCount; $start += $effectiveWindow) {
        $end = [Math]::Min($start + $effectiveWindow, $logicalCount)
        [uint64]$mask = 0
        for ($bit = $start; $bit -lt $end; $bit++) {
            $mask = $mask -bor ([uint64]1 -shl $bit)
        }

        Add-AffinityMaskCandidate -Candidates $generated -CandidateSet $candidateSet -Value $mask

        $windowWidth = $end - $start
        if ($windowWidth -gt 2) {
            $dropFirstMask = $mask -bxor ([uint64]1 -shl $start)
            $dropLastMask = $mask -bxor ([uint64]1 -shl ($end - 1))
            Add-AffinityMaskCandidate -Candidates $generated -CandidateSet $candidateSet -Value $dropFirstMask
            Add-AffinityMaskCandidate -Candidates $generated -CandidateSet $candidateSet -Value $dropLastMask
        }
    }

    return $generated
}

function Format-AffinityMask {
    param([string]$Mask)

    $trimmed = "$Mask".Trim()
    if ([string]::IsNullOrWhiteSpace($trimmed)) {
        throw "Affinity mask cannot be empty."
    }

    if ($trimmed.StartsWith("0x", [StringComparison]::OrdinalIgnoreCase)) {
        $value = [Convert]::ToUInt64($trimmed.Substring(2), 16)
    }
    else {
        $value = [Convert]::ToUInt64($trimmed, 10)
    }

    if ($value -eq 0) {
        throw "Affinity mask cannot be zero."
    }

    return "0x{0:X}" -f $value
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
        [int]$DefaultValue = 999999
    )

    $line = $Lines | Where-Object { $_ -like "*$MetricName*" } | Select-Object -Last 1
    if ([string]::IsNullOrWhiteSpace($line)) {
        return @{
            P999 = $DefaultValue
            Max = $DefaultValue
            Outliers = $DefaultValue
            Line = ""
        }
    }

    return @{
        P999 = [int](Read-RegexValue $line "p99\.9=(-?\d+)us" "999999")
        Max = [int](Read-RegexValue $line "max=(-?\d+)us" "999999")
        Outliers = [int](Read-RegexValue $line "outliersOverTarget=(\d+)" "999999")
        Line = $line
    }
}

function Get-Score {
    param(
        [int]$BatchP999Us,
        [int]$BatchMaxUs,
        [int]$BatchOutliers,
        [int]$LoopEndP999Us,
        [int]$LoopEndMaxUs,
        [int]$LoopEndOutliers
    )

    return ([int64]$BatchOutliers * 1000000000L) +
        ([int64]$LoopEndOutliers * 100000000L) +
        ([int64]$BatchMaxUs * 100000L) +
        ([int64]$LoopEndMaxUs * 10000L) +
        ([int64]$BatchP999Us * 100L) +
        ([int64]$LoopEndP999Us)
}

if ($Masks.Count -eq 0) {
    $Masks = @(New-DefaultAffinityMasks -Size $WindowSize)
}
else {
    $Masks = @($Masks | ForEach-Object { Format-AffinityMask $_ })
}

$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$results = New-Object System.Collections.Generic.List[object]
$effectivePasses = [Math]::Max(1, $Passes)

Write-Host "Scanning $($Masks.Count) affinity mask(s). This does not require administrator privileges."
Write-Host "Iterations=$Iterations passes=$effectivePasses loopSteps=$LoopSteps loopInterval=$($LoopIntervalUs)us outlierThreshold=$($OutlierThresholdUs)us nativeEngine=$NativeEngine"

foreach ($mask in $Masks) {
    $safeMask = ($mask -replace "[^0-9A-Fa-fx]", "_")
    $passResults = New-Object System.Collections.Generic.List[object]

    for ($pass = 1; $pass -le $effectivePasses; $pass++) {
        $logPath = Join-Path $outputRoot "affinity-$timestamp-$safeMask-pass$pass.log"
        $outlierPath = Join-Path $outputRoot "affinity-$timestamp-$safeMask-pass$pass-outliers.csv"

        $probeArgs = @(
            "--backend", "native",
            "--profile", "ultra",
            "--native-engine", $NativeEngine,
            "--warm-native",
            "--cpu-scan",
            "--precreate-native-plan",
            "--affinity-mask", $mask,
            "--loop-steps", "$LoopSteps",
            "--loop-interval-us", "$LoopIntervalUs",
            "--iterations", "$Iterations",
            "--outlier-threshold-us", "$OutlierThresholdUs",
            "--trace-outliers", $outlierPath
        )

        Write-Host "Testing affinity mask $mask pass $pass/$effectivePasses ..."
        $probeOutput = & $latencyProbe @probeArgs 2>&1
        $probeExitCode = $LASTEXITCODE
        $probeOutput | Set-Content -Path $logPath -Encoding UTF8

        if ($probeExitCode -ne 0) {
            throw "LatencyProbe.exe failed for affinity mask $mask pass $pass with exit code $probeExitCode. See $logPath"
        }

        $lines = @($probeOutput | ForEach-Object { "$_" })
        $allText = [string]::Join([Environment]::NewLine, $lines)
        $batch = Read-ProbeMetric -Lines $lines -MetricName "nativeBatchLate"
        $loopEnd = Read-ProbeMetric -Lines $lines -MetricName "nativeLoopEndLate" -DefaultValue 0

        $passResult = [pscustomobject]@{
            Pass = $pass
            NativeBatchP999Us = $batch.P999
            NativeBatchMaxUs = $batch.Max
            NativeBatchOutliers = $batch.Outliers
            NativeLoopEndP999Us = $loopEnd.P999
            NativeLoopEndMaxUs = $loopEnd.Max
            NativeLoopEndOutliers = $loopEnd.Outliers
            NativeWarmupUs = [int](Read-RegexValue $allText "nativeWarmup=(\d+)us" "0")
            NativePlanCreateUs = [int](Read-RegexValue $allText "nativePlanCreate=(\d+)us" "0")
            NativeStartupUs = [int](Read-RegexValue $allText "nativeStartup=(\d+)us" "0")
            NativeRunOverheadUs = [int](Read-RegexValue $allText "nativeRunOverhead=(\d+)us" "0")
            EngineWakeUs = [int](Read-RegexValue $allText "engineWake=(\d+)us" "0")
            SelectedCpu = Read-RegexValue $allText "selectedCpu=([^ ]+)" "unknown"
            LogPath = $logPath
            OutlierCsv = $outlierPath
        }

        $passResults.Add($passResult)
        Write-Host ("mask {0} pass {1}/{2}: nativeBatchLate p99.9={3}us max={4}us outliersOverTarget={5}; loopEnd max={6}us" -f `
            $mask,
            $pass,
            $effectivePasses,
            $passResult.NativeBatchP999Us,
            $passResult.NativeBatchMaxUs,
            $passResult.NativeBatchOutliers,
            $passResult.NativeLoopEndMaxUs)
    }

    $worstNativeBatchP999Us = [int](($passResults | Measure-Object -Property NativeBatchP999Us -Maximum).Maximum)
    $worstNativeBatchMaxUs = [int](($passResults | Measure-Object -Property NativeBatchMaxUs -Maximum).Maximum)
    $totalNativeBatchOutliers = [int](($passResults | Measure-Object -Property NativeBatchOutliers -Sum).Sum)
    $worstNativeLoopEndP999Us = [int](($passResults | Measure-Object -Property NativeLoopEndP999Us -Maximum).Maximum)
    $worstNativeLoopEndMaxUs = [int](($passResults | Measure-Object -Property NativeLoopEndMaxUs -Maximum).Maximum)
    $totalNativeLoopEndOutliers = [int](($passResults | Measure-Object -Property NativeLoopEndOutliers -Sum).Sum)
    $score = Get-Score `
        -BatchP999Us $worstNativeBatchP999Us `
        -BatchMaxUs $worstNativeBatchMaxUs `
        -BatchOutliers $totalNativeBatchOutliers `
        -LoopEndP999Us $worstNativeLoopEndP999Us `
        -LoopEndMaxUs $worstNativeLoopEndMaxUs `
        -LoopEndOutliers $totalNativeLoopEndOutliers

    $result = [pscustomobject]@{
        Mask = $mask
        Passes = $effectivePasses
        Score = $score
        NativeBatchP999Us = $worstNativeBatchP999Us
        NativeBatchMaxUs = $worstNativeBatchMaxUs
        NativeBatchOutliers = $totalNativeBatchOutliers
        NativeLoopEndP999Us = $worstNativeLoopEndP999Us
        NativeLoopEndMaxUs = $worstNativeLoopEndMaxUs
        NativeLoopEndOutliers = $totalNativeLoopEndOutliers
        WorstNativeBatchP999Us = $worstNativeBatchP999Us
        WorstNativeBatchMaxUs = $worstNativeBatchMaxUs
        TotalNativeBatchOutliers = $totalNativeBatchOutliers
        WorstNativeLoopEndP999Us = $worstNativeLoopEndP999Us
        WorstNativeLoopEndMaxUs = $worstNativeLoopEndMaxUs
        TotalNativeLoopEndOutliers = $totalNativeLoopEndOutliers
        NativeWarmupUs = [int](($passResults | Measure-Object -Property NativeWarmupUs -Maximum).Maximum)
        NativePlanCreateUs = [int](($passResults | Measure-Object -Property NativePlanCreateUs -Maximum).Maximum)
        NativeStartupUs = [int](($passResults | Measure-Object -Property NativeStartupUs -Maximum).Maximum)
        NativeRunOverheadUs = [int](($passResults | Measure-Object -Property NativeRunOverheadUs -Maximum).Maximum)
        EngineWakeUs = [int](($passResults | Measure-Object -Property EngineWakeUs -Maximum).Maximum)
        SelectedCpu = (($passResults | Select-Object -ExpandProperty SelectedCpu -Unique) -join ";")
        LogPath = (($passResults | Select-Object -ExpandProperty LogPath) -join ";")
        OutlierCsv = (($passResults | Select-Object -ExpandProperty OutlierCsv) -join ";")
    }

    $results.Add($result)
    Write-Host ("mask {0}: worst nativeBatchLate p99.9={1}us max={2}us totalOutliers={3}; worst loopEnd max={4}us totalLoopOutliers={5}; Score={6}" -f `
        $result.Mask,
        $result.WorstNativeBatchP999Us,
        $result.WorstNativeBatchMaxUs,
        $result.TotalNativeBatchOutliers,
        $result.WorstNativeLoopEndMaxUs,
        $result.TotalNativeLoopEndOutliers,
        $result.Score)
}

$summaryPath = Join-Path $outputRoot "affinity-$timestamp-summary.csv"
$results | Sort-Object Score, NativeBatchMaxUs, NativeBatchP999Us | Export-Csv -Path $summaryPath -NoTypeInformation -Encoding UTF8
$best = $results | Sort-Object Score, NativeBatchMaxUs, NativeBatchP999Us | Select-Object -First 1

Write-Host "Affinity summary: $summaryPath"
Write-Host ("recommended affinity mask: {0}" -f $best.Mask)
Write-Host ("recommended score: {0}; nativeBatchLate p99.9={1}us max={2}us outliersOverTarget={3}; nativeLoopEndLate max={4}us" -f `
    $best.Score,
    $best.NativeBatchP999Us,
    $best.NativeBatchMaxUs,
    $best.NativeBatchOutliers,
    $best.NativeLoopEndMaxUs)
Write-Host "Re-test the recommended mask with:"
Write-Host (".\artifacts\local-run\LatencyProbe\LatencyProbe.exe --backend native --profile ultra --native-engine $NativeEngine --warm-native --cpu-scan --precreate-native-plan --affinity-mask {0} --loop-steps {1} --loop-interval-us {2} --iterations 5000 --outlier-threshold-us {3}" -f `
    $best.Mask,
    $LoopSteps,
    $LoopIntervalUs,
    $OutlierThresholdUs)
