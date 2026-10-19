param([int]$Rounds = 5, [int]$Iterations = 1200, [int]$IntervalUs = 10000, [string[]]$Variants = @('ultra', 'max', 'max-norace', 'max-nopark'))
# Interleaved A/B of Max variants with LatencyProbe (native, ultra profile). Diagnostics only.
$exe = Join-Path $PSScriptRoot "..\src\tools\LatencyProbe\bin\Release\net8.0\LatencyProbe.exe"
$envs = @{ 'ultra' = @{}; 'max' = @{}; 'max-norace' = @{ MACROHID_MAX_NORACE = '1' }; 'max-nopark' = @{ MACROHID_MAX_NOPARK = '1' };
           'max-nospin' = @{ MACROHID_MAX_NOSPIN = '1' }; 'max-nort' = @{ MACROHID_MAX_NORT = '1' } }
$keys = 'MACROHID_MAX_NORACE', 'MACROHID_MAX_NOPARK', 'MACROHID_MAX_NOSPIN', 'MACROHID_MAX_NORT'
$rows = @()
for ($r = 1; $r -le $Rounds; $r++) {
    foreach ($name in $Variants) {
        foreach ($k in $keys) { Remove-Item "env:$k" -ErrorAction SilentlyContinue }
        foreach ($k in $envs[$name].Keys) { Set-Item "env:$k" $envs[$name][$k] }
        $a = @('--backend', 'native', '--profile', 'ultra', '--warm-native', '--cpu-scan', '--precreate-native-plan', '--iterations', "$Iterations", '--interval-us', "$IntervalUs")
        if ($name -ne 'ultra') { $a += '--max' }
        $o = & $exe @a 2>&1 | Out-String
        $m = [regex]::Match($o, 'nativeBatchLate backend=\S+ p99\.9=(\d+)us max=(\d+)us'); $ov = [regex]::Match($o, 'outliersOverTarget=(\d+)')
        $w = [regex]::Match($o, 'workerWins=(\d+)/(\d+)')
        $rows += [pscustomobject]@{ round = $r; mode = $name; p999 = [int]$m.Groups[1].Value; max = [int]$m.Groups[2].Value; over100 = [int]$ov.Groups[1].Value; helperWins = $w.Groups[2].Value }
    }
}
foreach ($k in $keys) { Remove-Item "env:$k" -ErrorAction SilentlyContinue }
$mid = [math]::Floor(($Rounds - 1) / 2)
foreach ($name in $Variants) {
    $s = $rows | Where-Object mode -eq $name; $mx = $s.max | Sort-Object; $p = $s.p999 | Sort-Object
    "{0,-11} max median={1,5} worst={2,5}  p99.9 median={3,4} worst={4,4}  over100={5}/{6}  maxes=[{7}] helperWins=[{8}]" -f $name, $mx[$mid], $mx[-1], $p[$mid], $p[-1], ($s.over100 | Measure-Object -Sum).Sum, ($Rounds * $Iterations), ($s.max -join ','), ($s.helperWins -join ',')
}
