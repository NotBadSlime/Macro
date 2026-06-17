param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [switch]$Launch
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$artifactRoot = Join-Path $repoRoot "artifacts"
$outputRoot = Join-Path $artifactRoot "local-run"

function Copy-DirectoryIfExists([string]$Source, [string]$Destination) {
    if (-not (Test-Path $Source)) {
        return
    }

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Copy-Item -Path (Join-Path $Source "*") -Destination $Destination -Recurse -Force
}

function New-CleanOutputRoot([string]$PreferredRoot) {
    New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null

    $candidates = @(
        $PreferredRoot,
        (Join-Path $artifactRoot "local-run-latest")
    )

    foreach ($candidate in $candidates) {
        try {
            if (Test-Path $candidate) {
                Remove-Item -LiteralPath $candidate -Recurse -Force
            }

            New-Item -ItemType Directory -Path $candidate -Force | Out-Null
            return $candidate
        }
        catch {
            Write-Warning "Could not reset '$candidate': $($_.Exception.Message)"
        }
    }

    $timestampedRoot = Join-Path $artifactRoot ("local-run-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
    New-Item -ItemType Directory -Path $timestampedRoot -Force | Out-Null
    return $timestampedRoot
}

$outputRoot = New-CleanOutputRoot $outputRoot

Push-Location $repoRoot
try {
    dotnet publish "src\ui\MacroStudio\MacroStudio.csproj" --configuration $Configuration --runtime win-x64 --self-contained false --output (Join-Path $outputRoot "MacroStudio")
    dotnet publish "src\tools\MacroRunner\MacroRunner.csproj" --configuration $Configuration --runtime win-x64 --self-contained false --output (Join-Path $outputRoot "MacroRunner")
    dotnet publish "src\tools\LatencyProbe\LatencyProbe.csproj" --configuration $Configuration --runtime win-x64 --self-contained false --output (Join-Path $outputRoot "LatencyProbe")

    Copy-DirectoryIfExists (Join-Path $repoRoot "samples") (Join-Path $outputRoot "samples")
    Copy-DirectoryIfExists (Join-Path $repoRoot "docs") (Join-Path $outputRoot "docs")
    Copy-DirectoryIfExists (Join-Path $repoRoot "scripts") (Join-Path $outputRoot "scripts")
    Copy-Item -LiteralPath (Join-Path $repoRoot "README.md") -Destination $outputRoot -Force

    $studioExe = Join-Path $outputRoot "MacroStudio\MacroStudio.exe"
    if (-not (Test-Path $studioExe)) {
        throw "Expected MacroStudio executable was not produced: $studioExe"
    }

    Write-Host "Local run folder created: $outputRoot"
    Write-Host "MacroStudio executable: $studioExe"

    if ($Launch) {
        Start-Process -FilePath $studioExe -WorkingDirectory (Split-Path $studioExe)
    }
}
finally {
    Pop-Location
}
