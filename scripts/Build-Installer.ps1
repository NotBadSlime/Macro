param(
    [string]$Version = "0.1.0",

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [switch]$InstallInno
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$artifactRoot = Join-Path $repoRoot "artifacts"
$inputRoot = Join-Path $artifactRoot "installer-input"
$outputRoot = Join-Path $artifactRoot "installer"
$issPath = Join-Path $repoRoot "installer\MacroHID.iss"

function Find-InnoCompiler {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
    )

    $pathCommand = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
    if ($pathCommand) {
        $candidates += $pathCommand.Source
    }

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path $candidate)) {
            return (Resolve-Path $candidate).Path
        }
    }

    return $null
}

function Copy-DirectoryIfExists([string]$Source, [string]$Destination) {
    if (-not (Test-Path $Source)) {
        return
    }

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Copy-Item -Path (Join-Path $Source "*") -Destination $Destination -Recurse -Force
}

function Remove-InstallerDebugArtifacts([string]$Root) {
    Get-ChildItem -Path $Root -Recurse -Include "*.pdb", "*.ilk" -File -ErrorAction SilentlyContinue |
        Remove-Item -Force
}

if ($InstallInno -and -not (Find-InnoCompiler)) {
    winget install --id JRSoftware.InnoSetup --exact --source winget --silent --accept-package-agreements --accept-source-agreements --disable-interactivity
}

$iscc = Find-InnoCompiler
if (-not $iscc) {
    throw "Inno Setup compiler ISCC.exe was not found. Install Inno Setup 6 or rerun with -InstallInno."
}

if (Test-Path $inputRoot) {
    Remove-Item -LiteralPath $inputRoot -Recurse -Force
}

if (Test-Path $outputRoot) {
    Remove-Item -LiteralPath $outputRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $inputRoot, $outputRoot | Out-Null

Push-Location $repoRoot
try {
    dotnet publish "src\ui\MacroStudio\MacroStudio.csproj" --configuration $Configuration --runtime win-x64 --self-contained false --output (Join-Path $inputRoot "MacroStudio")
    dotnet publish "src\tools\MacroRunner\MacroRunner.csproj" --configuration $Configuration --runtime win-x64 --self-contained false --output (Join-Path $inputRoot "MacroRunner")
    dotnet publish "src\tools\LatencyProbe\LatencyProbe.csproj" --configuration $Configuration --runtime win-x64 --self-contained false --output (Join-Path $inputRoot "LatencyProbe")

    Copy-DirectoryIfExists (Join-Path $repoRoot "scripts") (Join-Path $inputRoot "scripts")
    Copy-DirectoryIfExists (Join-Path $repoRoot "samples") (Join-Path $inputRoot "samples")
    Copy-DirectoryIfExists (Join-Path $repoRoot "docs") (Join-Path $inputRoot "docs")
    $macroConverterDist = Join-Path (Split-Path -Parent $repoRoot) "MacroConverter\dist\MacroConverter-win32-x64"
    Copy-DirectoryIfExists $macroConverterDist (Join-Path $inputRoot "MacroConverter")
    Copy-Item -LiteralPath (Join-Path $repoRoot "README.md") -Destination $inputRoot -Force
    Remove-InstallerDebugArtifacts $inputRoot

    $isccArgs = @(
        "/DSourceDir=$inputRoot",
        "/DOutputDir=$outputRoot",
        "/DAppVersion=$Version",
        $issPath
    )

    & $iscc @isccArgs
    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup compiler failed with exit code $LASTEXITCODE."
    }

    $installer = Join-Path $outputRoot "MacroHID-Setup-x64.exe"
    if (-not (Test-Path $installer)) {
        throw "Expected installer was not produced: $installer"
    }

    Write-Host "Installer created: $installer"
}
finally {
    Pop-Location
}
