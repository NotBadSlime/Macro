# Verifies each src/legacy/<V> snapshot is the release's code: every file under Core/, Runtime/ and Native/ must match the
# file at the release tag (line endings aside), except the files listed in patched-files.txt, and every file of the tag
# must be present. Prints a line per snapshot and fails on any difference.
$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
. (Join-Path $PSScriptRoot "Legacy-Versions.ps1")
$parts = @{ "Core" = "src/shared/MacroHid.Core"; "Runtime" = "src/shared/MacroHid.Runtime"; "Native" = "src/native/MacroHid.NativePlayback" }
$failures = 0
Push-Location $repoRoot
try {
    foreach ($version in $LegacyVersions) {
        if (-not (git tag --list $version.Tag)) { "skip $($version.Folder): tag $($version.Tag) not available"; continue }
        $folder = Join-Path "src\legacy" $version.Folder
        $patchedFile = Join-Path $folder "patched-files.txt"
        $patched = if (Test-Path $patchedFile) { Get-Content $patchedFile | ForEach-Object { ($_ -split ' ')[0] } | Where-Object { $_ } } else { @() }
        $checked = 0; $different = @()
        foreach ($part in $parts.Keys) {
            $tagFiles = git ls-tree -r --name-only $version.Tag -- $parts[$part]
            foreach ($tagPath in $tagFiles) {
                $relative = $part + "/" + $tagPath.Substring($parts[$part].Length + 1)
                if ($patched -contains $relative) { continue }
                $local = Join-Path $folder ($relative -replace '/', '\')
                if ($relative.EndsWith(".csproj")) { $local += ".original" }
                if (-not (Test-Path $local)) { $different += "missing $relative"; continue }
                $blob = git rev-parse "$($version.Tag):$tagPath"
                # git hash-object with the repository's attributes normalizes line endings the way the tag stored them.
                $hash = git hash-object --path $tagPath $local
                if ($hash -ne $blob) { $different += "changed $relative" }
                $checked++
            }
        }
        $extra = Get-ChildItem $folder -Recurse -File | Where-Object {
            $_.FullName -notmatch '\\(bin|obj|x64)\\' -and $_.Name -notmatch '^(MacroHid\.Legacy\..*\.csproj|patched-files\.txt)$'
        } | ForEach-Object { $_.FullName.Substring((Resolve-Path $folder).Path.Length + 1) -replace '\\', '/' } | Where-Object {
            $relative = $_ -replace '\.csproj\.original$', '.csproj'
            $part = $relative.Split('/')[0]
            $tagPath = $parts[$part] + "/" + $relative.Substring($part.Length + 1)
            -not (git ls-tree --name-only $version.Tag -- $tagPath) -and $patched -notcontains $relative
        }
        $different += $extra | ForEach-Object { "added $_" }
        if ($different.Count -gt 0) { $failures++; "FAIL $($version.Folder): $($different -join '; ')" }
        else { "OK $($version.Folder): $checked files identical to $($version.Tag); patched: $($patched -join ', ')" }
    }
}
finally { Pop-Location }
if ($failures -gt 0) { throw "$failures snapshot(s) differ from their release" }
