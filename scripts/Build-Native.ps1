param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [ValidateSet("x64")]
    [string]$Platform = "x64"
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$vsDevCmd = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\BuildTools\Common7\Tools\VsDevCmd.bat"

if (-not (Test-Path $vsDevCmd)) {
    throw "VsDevCmd.bat was not found at $vsDevCmd. Install Visual Studio 2022 Build Tools with C++ and WDK build tools."
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
