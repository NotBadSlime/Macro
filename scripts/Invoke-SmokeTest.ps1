param(
    [string]$MacroPath = "samples\baseline.mcrx",

    [switch]$Send,

    [ValidateSet("skip", "match", "live")]
    [string]$Pixels = "match"
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")

Push-Location $repoRoot
try {
    dotnet run --project src\tools\MacroRunner\MacroRunner.csproj -- --macro $MacroPath --pixels $Pixels
    if ($LASTEXITCODE -ne 0) {
        throw "MacroRunner dry-run failed with exit code $LASTEXITCODE."
    }

    if ($Send) {
        dotnet run --project src\tools\MacroRunner\MacroRunner.csproj -- --macro $MacroPath --pixels $Pixels --send --no-dry-run
        if ($LASTEXITCODE -ne 0) {
            throw "MacroRunner send smoke test failed with exit code $LASTEXITCODE."
        }
    }
}
finally {
    Pop-Location
}
