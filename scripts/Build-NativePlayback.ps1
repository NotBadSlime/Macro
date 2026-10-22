param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$projectDir = Join-Path $repoRoot "src\native\MacroHid.NativePlayback"
$projectPath = Join-Path $projectDir "MacroHid.NativePlayback.vcxproj"
$dllPath = Join-Path $projectDir "x64\$Configuration\MacroHid.NativePlayback.dll"

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

if (-not (Test-Path $projectPath)) {
    throw "Native playback project was not found: $projectPath"
}

# Skip the build only when the existing DLL is newer than every native source file.
if (Test-Path $dllPath) {
    $dllTime = (Get-Item $dllPath).LastWriteTimeUtc
    $newer = @(Get-ChildItem $projectDir -File | Where-Object {
        $_.Extension -in ".cpp", ".h", ".inl", ".vcxproj" -and $_.LastWriteTimeUtc -gt $dllTime
    })
    if ($newer.Count -eq 0) {
        Write-Host "Native playback DLL up to date: $dllPath"
        Write-Output $dllPath
        return
    }
}

$msbuild = Find-MSBuild
if (-not $msbuild) {
    throw "MSBuild.exe was not found. Install Visual Studio 2022 with Desktop development with C++."
}

& $msbuild $projectPath /m /restore /p:Configuration=$Configuration /p:Platform=x64
if ($LASTEXITCODE -ne 0) {
    throw "Native playback build failed with exit code $LASTEXITCODE."
}

if (-not (Test-Path $dllPath)) {
    throw "Native playback DLL was not produced: $dllPath"
}

Write-Host "Native playback DLL: $dllPath"
Write-Output $dllPath
