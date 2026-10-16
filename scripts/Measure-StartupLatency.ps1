param(
    [ValidateSet("low", "balanced", "extreme")]
    [string[]]$Precision = @("low", "balanced", "extreme"),

    # Fresh processes per precision; the first trigger of each is the cold sample.
    [int]$ColdProcesses = 10,

    # Triggers in one long-running process for the steady-state distribution.
    [int]$SteadyRuns = 2000,

    [int]$IntervalMs = 15,

    [string]$Label = "run",

    # Extra probe arguments, e.g. "--no-native-path-warmup" for the A side of an A/B.
    [string[]]$ProbeArgs = @()
)

# Every trigger injects F24 down/up into the foreground window.
$ErrorActionPreference = "Continue"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$project = Join-Path $repoRoot "src\tools\StartupLatencyProbe\StartupLatencyProbe.csproj"
dotnet build $project -c Release -v q | Out-Null
if ($LASTEXITCODE -ne 0) { throw "build failed" }
$exe = Join-Path $repoRoot "src\tools\StartupLatencyProbe\bin\Release\net8.0-windows\StartupLatencyProbe.exe"

function Invoke-Probe([string[]]$probeArgs) {
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        $all = @($probeArgs) + @($script:ProbeArgs)
        $lines = & $exe @all 2>&1 | ForEach-Object { "$_" }
        if ($LASTEXITCODE -eq 0 -and ($lines | Where-Object { $_ -like "SUMMARY *" })) { return $lines }
        Start-Sleep -Milliseconds 500
    }
    throw "probe failed 3 times:`n$($lines -join "`n")"
}

function Parse-Summary([string[]]$lines) {
    $line = $lines | Where-Object { $_ -like "SUMMARY *" } | Select-Object -Last 1
    if (-not $line) { throw "probe produced no summary:`n$($lines -join "`n")" }
    $map = @{}
    foreach ($pair in ($line -split " " | Select-Object -Skip 1)) {
        $k, $v = $pair -split "=", 2
        $map[$k] = $v
    }
    return $map
}

$rows = foreach ($p in $Precision) {
    $cold = for ($i = 0; $i -lt $ColdProcesses; $i++) {
        [double](Parse-Summary (Invoke-Probe @("--precision", $p, "--runs", "3", "--interval-ms", "$IntervalMs"))).first
    }
    $cold = $cold | Sort-Object
    $steady = Parse-Summary (Invoke-Probe @("--precision", $p, "--runs", "$SteadyRuns", "--interval-ms", "$IntervalMs"))
    [pscustomobject]@{
        label = $Label; precision = $p
        cold_median_us = $cold[[int][Math]::Floor(($cold.Count - 1) / 2)]
        cold_max_us = $cold[-1]
        steady_p50_us = $steady.p50; steady_p99_us = $steady.p99
        steady_p999_us = $steady.p999; steady_max_us = $steady.max
    }
}
$rows | Format-Table -AutoSize | Out-String -Width 200
