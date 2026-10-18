# Applies the external-card hook to the snapshots that predate some effect cards (see src/legacy/Bridge/LegacyExternalStep.cs).
# Three edits, each tagged "External-card hook": McrxParser's unknown-type branch, one case in ManagedMacroControlFlowRunner's
# step switch, and one case in MacroControlFlowInspector so a macro containing such a card takes the managed timeline.
# Idempotent. Records the edited files in patched-files.txt (checked by Test-LegacySnapshots.ps1).
$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
. (Join-Path $PSScriptRoot "Legacy-Versions.ps1")

function Read-Text([string]$path) {
    $bytes = [IO.File]::ReadAllBytes($path)
    $bom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $offset = if ($bom) { 3 } else { 0 }
    [pscustomobject]@{ Text = [Text.Encoding]::UTF8.GetString($bytes, $offset, $bytes.Length - $offset); Bom = $bom }
}
function Write-Text([string]$path, $file, [string]$text) {
    [IO.File]::WriteAllText($path, $text, (New-Object Text.UTF8Encoding($file.Bom)))
}

foreach ($version in $LegacyVersions | Where-Object { [version]$_.Tag.TrimStart('v') -le [version]"1.6.0" }) {
    $folder = Join-Path $repoRoot (Join-Path "src\legacy" $version.Folder)
    $parserPath = Join-Path $folder "Core\McrxParser.cs"
    $runnerPath = Join-Path $folder "Runtime\ManagedMacroControlFlowRunner.cs"

    $parser = Read-Text $parserPath
    $old = '_ => throw new JsonException($"Unsupported macro step type ''{type}''.")'
    $new = '_ => LegacyExternalStep.TryCreate(type, stepElement) ?? throw new JsonException($"Unsupported macro step type ''{type}''.") // External-card hook'
    if (-not $parser.Text.Contains("LegacyExternalStep.TryCreate")) {
        if (-not $parser.Text.Contains($old)) { throw "parser anchor missing in $($version.Folder)" }
        Write-Text $parserPath $parser ($parser.Text.Replace($old, $new))
    }

    $runner = Read-Text $runnerPath
    $text = $runner.Text
    $nl = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }
    if (-not $text.Contains("case LegacyExternalStep external:")) {
        $anchor = "                case ConsumerStep consumer:"
        $at = $text.IndexOf($anchor)
        if ($at -lt 0) { throw "runner anchor missing in $($version.Folder)" }
        $hasWait = $text.Contains("void WaitToCurrentTick()")
        $case = if ($hasWait) {
            @(
                "                case LegacyExternalStep external: // External-card hook: run the card at its point on the timeline.",
                "                    WaitToCurrentTick();",
                "                    LegacyExternalStep.Run(external, cancellationToken);",
                "                    elapsedTicks = Math.Max(elapsedTicks, Math.Max(0, clock.GetTimestamp() - startTick));",
                "                    break;",
                ""
            )
        } else {
            @(
                "                case LegacyExternalStep external: // External-card hook: run the card at its point on the timeline.",
                "                {",
                "                    var externalDueTick = startTick + elapsedTicks;",
                "                    if (pauseCoordinator is null) delayStrategy.WaitUntil(externalDueTick, qpcFrequency, cancellationToken, noWait);",
                "                    else pauseCoordinator.WaitUntil(externalDueTick, delayStrategy, qpcFrequency, cancellationToken, noWait);",
                "                    cancellationToken.ThrowIfCancellationRequested();",
                "                    LegacyExternalStep.Run(external, cancellationToken);",
                "                    elapsedTicks = Math.Max(elapsedTicks, Math.Max(0, clock.GetTimestamp() - startTick));",
                "                    break;",
                "                }",
                ""
            )
        }
        $text = $text.Insert($at, ($case -join $nl))
    }
    if (-not $text.Contains("case LegacyExternalStep: // External-card hook")) {
        $method = $text.IndexOf("private static bool InspectSteps(")
        if ($method -lt 0) { throw "inspector anchor missing in $($version.Folder)" }
        $switchAnchor = "switch (step)$nl            {$nl"
        $switchAt = $text.IndexOf($switchAnchor, $method)
        if ($switchAt -lt 0) { throw "inspector switch missing in $($version.Folder)" }
        $insertAt = $switchAt + $switchAnchor.Length
        $text = $text.Insert($insertAt, "                case LegacyExternalStep: // External-card hook: such a card needs the managed timeline.$nl                    return true;$nl")
    }
    Write-Text $runnerPath $runner $text

    $list = @("Core/McrxParser.cs", "Runtime/ManagedMacroControlFlowRunner.cs")
    if ($version.Tag -eq "v1.6.0") {
        # The driver was not tracked in git at v1.6.0 (added by 4e36944); the release shipped the same WinDivert driver.
        $driver = Join-Path $folder "Runtime\WeakNet\Native\WinDivert64.sys"
        if (-not (Test-Path $driver)) {
            $zip = Join-Path ([IO.Path]::GetTempPath()) "macrohid-windivert.zip"
            git -C $repoRoot archive --format=zip -o $zip v2.0.0 src/shared/MacroHid.Runtime/WeakNet/Native/WinDivert64.sys
            $stage = Join-Path ([IO.Path]::GetTempPath()) "macrohid-windivert"
            Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue
            Expand-Archive $zip $stage
            Copy-Item (Join-Path $stage "src\shared\MacroHid.Runtime\WeakNet\Native\WinDivert64.sys") $driver
            Remove-Item -Recurse -Force $stage, $zip
        }
        $list += "Runtime/WeakNet/Native/WinDivert64.sys (added: not tracked in git at v1.6.0, same driver as v2.0.0)"
    }
    [IO.File]::WriteAllText((Join-Path $folder "patched-files.txt"), ($list -join "`n") + "`n", (New-Object Text.UTF8Encoding($false)))
    "patched $($version.Folder)"
}
