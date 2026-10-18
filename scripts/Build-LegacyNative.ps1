# Builds each snapshot's native playback DLL from the release's own native source (src/legacy/<V>/Native) as
# MacroHid.NativePlayback.<suffix>.dll, so it loads next to the current MacroHid.NativePlayback.dll.
param([switch]$Force)
$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
. (Join-Path $PSScriptRoot "Legacy-Versions.ps1")
$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
$msbuild = (Get-Command "MSBuild.exe" -ErrorAction SilentlyContinue).Source
if (-not $msbuild -and (Test-Path $vswhere)) {
    $msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1
}
if (-not $msbuild) { throw "MSBuild.exe was not found. Install Visual Studio 2022 with Desktop development with C++." }

foreach ($version in $LegacyVersions) {
    $native = Join-Path $repoRoot (Join-Path "src\legacy" (Join-Path $version.Folder "Native"))
    $name = "MacroHid.NativePlayback.$($version.Suffix)"
    $dll = Join-Path $native "x64\Release\$name.dll"
    $sources = Get-ChildItem $native -File | Where-Object { $_.Extension -in ".cpp", ".h", ".inl", ".vcxproj" }
    if (-not $Force -and (Test-Path $dll) -and ($sources | Where-Object { $_.LastWriteTimeUtc -gt (Get-Item $dll).LastWriteTimeUtc }).Count -eq 0) {
        "up to date $name"; continue
    }
    & $msbuild (Join-Path $native "MacroHid.NativePlayback.vcxproj") /m /nologo /v:m `
        /p:Configuration=Release /p:Platform=x64 /p:TargetName=$name `
        "/p:OutDir=$native\x64\Release\\" "/p:IntDir=$native\obj\x64\Release\\"
    if ($LASTEXITCODE -ne 0) { throw "native build failed for $($version.Folder)" }
    if (-not (Test-Path $dll)) { throw "native DLL not produced: $dll" }
    "built $name"
}
exit 0
