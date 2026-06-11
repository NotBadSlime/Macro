param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [ValidateSet("x64")]
    [string]$Platform = "x64",

    [switch]$BuildNative,

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

if (Test-Path $outputRoot) {
    Remove-Item -LiteralPath $outputRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $outputRoot | Out-Null

Push-Location $repoRoot
try {
    dotnet publish "src\ui\MacroStudio\MacroStudio.csproj" --configuration $Configuration --runtime win-x64 --self-contained false --output (Join-Path $outputRoot "MacroStudio")
    dotnet publish "src\tools\MacroRunner\MacroRunner.csproj" --configuration $Configuration --runtime win-x64 --self-contained false --output (Join-Path $outputRoot "MacroRunner")
    dotnet publish "src\tools\LatencyProbe\LatencyProbe.csproj" --configuration $Configuration --runtime win-x64 --self-contained false --output (Join-Path $outputRoot "LatencyProbe")

    if ($BuildNative) {
        & (Join-Path $PSScriptRoot "Build-Native.ps1") -Configuration $Configuration -Platform $Platform
    }

    Copy-DirectoryIfExists (Join-Path $repoRoot "samples") (Join-Path $outputRoot "samples")
    Copy-DirectoryIfExists (Join-Path $repoRoot "docs") (Join-Path $outputRoot "docs")
    Copy-DirectoryIfExists (Join-Path $repoRoot "scripts") (Join-Path $outputRoot "scripts")
    Copy-Item -LiteralPath (Join-Path $repoRoot "README.md") -Destination $outputRoot -Force

    $serviceOut = Join-Path $repoRoot "src\service\MacroEngineService\$Platform\$Configuration"
    $driverOut = Join-Path $repoRoot "src\driver\MacroHidDriver\$Platform\$Configuration"
    $driverPackage = Join-Path $driverOut "MacroHidDriver"
    Copy-DirectoryIfExists $serviceOut (Join-Path $outputRoot "service")
    Copy-DirectoryIfExists $driverPackage (Join-Path $outputRoot "driver")

    $certPath = Join-Path $driverOut "MacroHidDriver.cer"
    if (Test-Path $certPath) {
        New-Item -ItemType Directory -Path (Join-Path $outputRoot "driver") -Force | Out-Null
        Copy-Item -LiteralPath $certPath -Destination (Join-Path $outputRoot "driver") -Force
    }

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
