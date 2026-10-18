param(
    # Re-extract even if the snapshot folder exists. Overwrites the patched files: re-apply the hook patch afterwards.
    [switch]$Force
)
# Copies the playback code of each earlier MacroHID release, unchanged, into src/legacy/<V>:
#   Core/    = src/shared/MacroHid.Core      at the tag
#   Runtime/ = src/shared/MacroHid.Runtime   at the tag
#   Native/  = src/native/MacroHid.NativePlayback at the tag
# Each snapshot compiles into its own assembly (MacroHid.Legacy.<V>) with its own native DLL
# (MacroHid.NativePlayback.<v>.dll), so the version switcher runs exactly that release's code.
# The only edits to the copied code are the external-card hook, listed per snapshot in patched-files.txt.
$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
. (Join-Path $PSScriptRoot "Legacy-Versions.ps1")
Push-Location $repoRoot
try {
    foreach ($version in $LegacyVersions) {
        $target = Join-Path "src\legacy" $version.Folder
        if ((Test-Path $target) -and -not $Force) { "skip $($version.Folder) (exists)"; continue }
        if (Test-Path $target) {
            foreach ($part in "Core", "Runtime", "Native") { Remove-Item -Recurse -Force (Join-Path $target $part) -ErrorAction SilentlyContinue }
        }
        New-Item -ItemType Directory -Force $target | Out-Null
        $zip = Join-Path ([IO.Path]::GetTempPath()) ("macrohid-legacy-" + $version.Folder + ".zip")
        $stage = Join-Path ([IO.Path]::GetTempPath()) ("macrohid-legacy-" + $version.Folder)
        Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue
        # zip, not tar through the pipeline: PowerShell would re-encode the binary stream.
        git archive --format=zip -o $zip $version.Tag src/shared/MacroHid.Core src/shared/MacroHid.Runtime src/native/MacroHid.NativePlayback
        if ($LASTEXITCODE -ne 0) { throw "git archive failed for $($version.Tag)" }
        Expand-Archive $zip $stage
        Move-Item (Join-Path $stage "src\shared\MacroHid.Core") (Join-Path $target "Core")
        Move-Item (Join-Path $stage "src\shared\MacroHid.Runtime") (Join-Path $target "Runtime")
        Move-Item (Join-Path $stage "src\native\MacroHid.NativePlayback") (Join-Path $target "Native")
        Remove-Item -Recurse -Force $stage, $zip
        # The snapshot builds as one project (MacroHid.Legacy.<V>.csproj); the release's own project files stay for reference
        # but are renamed so MSBuild never picks them up.
        Get-ChildItem $target -Recurse -Include *.csproj | ForEach-Object { Rename-Item $_.FullName ($_.Name + ".original") }
        "imported $($version.Tag) -> $target"
    }
}
finally { Pop-Location }
