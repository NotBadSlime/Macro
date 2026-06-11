param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [ValidateSet("x64")]
    [string]$Platform = "x64"
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")

function Find-VsDevCmd {
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $installationPath = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
        if ($installationPath) {
            $candidate = Join-Path $installationPath "Common7\Tools\VsDevCmd.bat"
            if (Test-Path $candidate) {
                return (Resolve-Path $candidate).Path
            }
        }
    }

    $fallbacks = @(
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\BuildTools\Common7\Tools\VsDevCmd.bat",
        "${env:ProgramFiles}\Microsoft Visual Studio\2022\Enterprise\Common7\Tools\VsDevCmd.bat",
        "${env:ProgramFiles}\Microsoft Visual Studio\2022\Professional\Common7\Tools\VsDevCmd.bat",
        "${env:ProgramFiles}\Microsoft Visual Studio\2022\Community\Common7\Tools\VsDevCmd.bat"
    )

    foreach ($candidate in $fallbacks) {
        if ($candidate -and (Test-Path $candidate)) {
            return (Resolve-Path $candidate).Path
        }
    }

    return $null
}

$vsDevCmd = Find-VsDevCmd
if (-not $vsDevCmd) {
    throw "VsDevCmd.bat was not found. Install Visual Studio 2022 with C++, MSBuild, and WDK build tools."
}

$serviceProject = Join-Path $repoRoot "src\service\MacroEngineService\MacroEngineService.vcxproj"
$driverProject = Join-Path $repoRoot "src\driver\MacroHidDriver\MacroHidDriver.vcxproj"

Push-Location $repoRoot
try {
    $command = "`"$vsDevCmd`" -arch=x64 -host_arch=x64 && " +
        "msbuild `"$serviceProject`" /m /p:Configuration=$Configuration /p:Platform=$Platform && " +
        "msbuild `"$driverProject`" /m /p:Configuration=$Configuration /p:Platform=$Platform"
    cmd.exe /d /s /c $command
    if ($LASTEXITCODE -ne 0) {
        throw "Native build failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}
