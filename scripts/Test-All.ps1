param(
    [switch]$SkipManaged,
    [switch]$SkipNative
)
# Build the solution and native DLL, then run the managed suite and the native scheduler regression.
$ErrorActionPreference = "Continue"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Push-Location $repoRoot
try {
    & .\scripts\Build-NativePlayback.ps1 -Configuration Release *> $null
    if ($LASTEXITCODE -ne 0) { throw "native build failed" }
    $build = dotnet build MacroHID.sln -c Release -v q 2>&1
    $errors = $build | Select-String " error " | ForEach-Object { $_.Line } | Sort-Object -Unique
    if ($errors) { $errors | Select-Object -First 20; throw "managed build failed" }
    if (-not $SkipManaged) {
        $out = dotnet run --project tests\MacroHid.Core.Tests\MacroHid.Core.Tests.csproj -c Release --no-build 2>&1
        $out | Select-String "^FAIL|test\(s\)" -Context 0, 3 | ForEach-Object { $_.ToString() }
        if ($LASTEXITCODE -ne 0) { throw "managed tests failed" }
    }
    if (-not $SkipNative) {
        $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
        $msb = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1
        & $msb tests\MacroHid.NativePlayback.Tests\MacroHid.NativePlayback.Tests.vcxproj /p:Configuration=Release /p:Platform=x64 /v:q /nologo | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "native test build failed" }
        $exe = Get-ChildItem tests\MacroHid.NativePlayback.Tests -Recurse -Filter MacroHid.NativePlayback.Tests.exe |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        & $exe.FullName 2>&1 | Select-String "PASS|FAIL|error|exception" | ForEach-Object { $_.ToString() }
        if ($LASTEXITCODE -ne 0) { throw "native tests failed" }
        & $exe.FullName --batch-trace 2>&1 | Select-String "PASS|FAIL" | ForEach-Object { $_.ToString() }
        if ($LASTEXITCODE -ne 0) { throw "native batch trace test failed" }
    }
    "ALL OK"
}
finally { Pop-Location }
