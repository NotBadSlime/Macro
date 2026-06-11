param(
    [string]$Version = "0.1.0",

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [ValidateSet("x64")]
    [string]$Platform = "x64",

    [switch]$SkipNativeBuild,

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

function Copy-Directory([string]$Source, [string]$Destination) {
    if (-not (Test-Path $Source)) {
        throw "Required directory was not found: $Source"
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

    if (-not $SkipNativeBuild) {
        & (Join-Path $PSScriptRoot "Build-Native.ps1") -Configuration $Configuration -Platform $Platform
    }

    $serviceOut = Join-Path $repoRoot "src\service\MacroEngineService\$Platform\$Configuration"
    $driverOut = Join-Path $repoRoot "src\driver\MacroHidDriver\$Platform\$Configuration"
    $driverPackage = Join-Path $driverOut "MacroHidDriver"

    Copy-Directory $serviceOut (Join-Path $inputRoot "service")
    Copy-Directory $driverPackage (Join-Path $inputRoot "driver")

    $certPath = Join-Path $driverOut "MacroHidDriver.cer"
    if (Test-Path $certPath) {
        Copy-Item -LiteralPath $certPath -Destination (Join-Path $inputRoot "driver") -Force
    }

    Copy-Directory (Join-Path $repoRoot "scripts") (Join-Path $inputRoot "scripts")
    Copy-Directory (Join-Path $repoRoot "samples") (Join-Path $inputRoot "samples")
    Copy-Directory (Join-Path $repoRoot "docs") (Join-Path $inputRoot "docs")
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
