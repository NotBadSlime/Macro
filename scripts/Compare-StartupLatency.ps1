param(
    # Git revision to compare against the working tree.
    [string]$Baseline = "HEAD",
    [ValidateSet("low", "balanced", "extreme")]
    [string[]]$Precision = @("balanced", "extreme"),
    [int]$Rounds = 3,
    [int]$ColdProcesses = 8,
    [int]$SteadyRuns = 1000
)
# Interleaved A/B of the startup latency probe: A = $Baseline built in a temporary worktree, B = working tree.
# Rounds alternate A and B so machine noise hits both. Prints medians across rounds.
$ErrorActionPreference = "Continue"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$worktree = Join-Path $env:TEMP ("macrohid-ab-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
git -C $repoRoot worktree add --detach $worktree $Baseline 2>&1 | Out-Null
try {
    Push-Location $worktree
    & .\scripts\Build-NativePlayback.ps1 -Configuration Release *> $null
    Pop-Location
    $probeProject = "src\tools\StartupLatencyProbe\StartupLatencyProbe.csproj"
    foreach ($root in @($worktree, $repoRoot)) {
        dotnet build (Join-Path $root $probeProject) -c Release -v q | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "probe build failed in $root" }
    }
    $exe = @{
        A = Join-Path $worktree "src\tools\StartupLatencyProbe\bin\Release\net8.0-windows\StartupLatencyProbe.exe"
        B = Join-Path $repoRoot "src\tools\StartupLatencyProbe\bin\Release\net8.0-windows\StartupLatencyProbe.exe"
    }
    function Summary([string]$path, [string[]]$probeArgs) {
        for ($i = 0; $i -lt 3; $i++) {
            $line = (& $path @probeArgs 2>&1 | Where-Object { "$_" -like "SUMMARY *" } | Select-Object -Last 1)
            if ($line) {
                $map = @{}
                foreach ($pair in ("$line" -split " " | Select-Object -Skip 1)) { $k, $v = $pair -split "=", 2; $n = 0.0; if ([double]::TryParse($v, [ref]$n)) { $map[$k] = $n } }
                return $map
            }
            Start-Sleep -Milliseconds 300
        }
        throw "probe failed: $path"
    }
    function Median([double[]]$v) { $s = $v | Sort-Object; $s[[int][Math]::Floor(($s.Count - 1) / 2)] }
    $results = @()
    foreach ($p in $Precision) {
        $acc = @{ A = @{ cold = @(); p50 = @(); p99 = @(); p999 = @() }; B = @{ cold = @(); p50 = @(); p99 = @(); p999 = @() } }
        for ($r = 0; $r -lt $Rounds; $r++) {
            foreach ($side in @("A", "B")) {
                $cold = for ($c = 0; $c -lt $ColdProcesses; $c++) { (Summary $exe[$side] @("--precision", $p, "--runs", "3", "--interval-ms", "10")).first }
                $acc[$side].cold += Median $cold
                $s = Summary $exe[$side] @("--precision", $p, "--runs", "$SteadyRuns", "--interval-ms", "10")
                $acc[$side].p50 += $s.p50; $acc[$side].p99 += $s.p99; $acc[$side].p999 += $s.p999
            }
        }
        foreach ($side in @("A", "B")) {
            $results += [pscustomobject]@{
                side = $(if ($side -eq "A") { "A $Baseline" } else { "B worktree" }); precision = $p
                cold_us = Median $acc[$side].cold; p50_us = Median $acc[$side].p50
                p99_us = Median $acc[$side].p99; p999_us = Median $acc[$side].p999
            }
        }
    }
    $results | Format-Table -AutoSize | Out-String -Width 200
}
finally {
    git -C $repoRoot worktree remove --force $worktree 2>&1 | Out-Null
}
