param(
    [string]$MacroPath = "samples\baseline.mcrx",

    [switch]$Send,

    [ValidateSet("skip", "match", "live")]
    [string]$Pixels = "match"
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")

function Invoke-MacroRunner([string[]]$RunnerArgs) {
    $installedRunner = Join-Path $repoRoot "MacroRunner\MacroRunner.exe"
    if (Test-Path $installedRunner) {
        & $installedRunner @RunnerArgs
        return
    }

    dotnet run --project "src\tools\MacroRunner\MacroRunner.csproj" -- @RunnerArgs
}

Push-Location $repoRoot
try {
    Invoke-MacroRunner @("--macro", $MacroPath, "--pixels", $Pixels)
    if ($LASTEXITCODE -ne 0) {
        throw "MacroRunner dry-run failed with exit code $LASTEXITCODE."
    }

    if ($Send) {
        Invoke-MacroRunner @("--macro", $MacroPath, "--pixels", $Pixels, "--send", "--no-dry-run")
        if ($LASTEXITCODE -ne 0) {
            throw "MacroRunner send smoke test failed with exit code $LASTEXITCODE."
        }
    }
}
finally {
    Pop-Location
}
