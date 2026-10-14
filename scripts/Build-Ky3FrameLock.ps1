param([ValidateSet("Debug", "Release")][string]$Configuration = "Release")
$ErrorActionPreference = "Stop"
$source = Join-Path $PSScriptRoot "..\src\native\FpsHotkeyExt"
$build = Join-Path $source "build"
& cmake -S $source -B $build -G "Visual Studio 17 2022" -A x64
if ($LASTEXITCODE -ne 0) { throw "KY3 frame-lock configure failed." }
& cmake --build $build --config $Configuration
if ($LASTEXITCODE -ne 0) { throw "KY3 frame-lock build failed." }
& ctest --test-dir $build -C $Configuration --output-on-failure
if ($LASTEXITCODE -ne 0) { throw "KY3 frame-lock tests failed." }
$dist = Join-Path $source "dist"
New-Item -ItemType Directory -Path $dist -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $build "bin\$Configuration\FpsHotkeyExt.dll") -Destination $dist -Force
Copy-Item -LiteralPath (Join-Path $source "fps_hotkeys.json") -Destination $dist -Force
Copy-Item -LiteralPath (Join-Path $source "vendor\MinHook\LICENSE.txt") -Destination (Join-Path $dist "MinHook.LICENSE.txt") -Force
