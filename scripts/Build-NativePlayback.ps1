param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$projectPath = Join-Path $repoRoot "src\native\MacroHid.NativePlayback\MacroHid.NativePlayback.vcxproj"

function Find-MSBuild {
    $command = Get-Command "MSBuild.exe" -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $found = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1
        if ($found) {
            return $found
        }
    }

    return $null
}

$msbuild = Find-MSBuild
if (-not $msbuild) {
    throw "MSBuild.exe was not found. Install Visual Studio 2022 with Desktop development with C++."
}

if (-not (Test-Path $projectPath)) {
    throw "Native playback project was not found: $projectPath"
}

& $msbuild $projectPath /m /restore /p:Configuration=$Configuration /p:Platform=x64
if ($LASTEXITCODE -ne 0) {
    throw "Native playback build failed with exit code $LASTEXITCODE."
}

$dllPath = Join-Path $repoRoot "src\native\MacroHid.NativePlayback\x64\$Configuration\MacroHid.NativePlayback.dll"
if (-not (Test-Path $dllPath)) {
    throw "Native playback DLL was not produced: $dllPath"
}

Write-Host "Native playback DLL: $dllPath"
Write-Output $dllPath
