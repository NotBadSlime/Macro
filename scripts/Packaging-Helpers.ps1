# Shared by the packaging entry point. All filesystem moves stay in artifacts.
function Assert-ArtifactPath([string]$Path) {
    $root = [IO.Path]::GetFullPath($artifactRoot).TrimEnd('\')
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    if (-not $full.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Output must be below the workspace artifacts directory: $full"
    }
    $cursor = $full
    while ($cursor -and $cursor.Length -ge $root.Length) {
        if (Test-Path -LiteralPath $cursor) {
            if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Output paths must not contain directory links: $cursor"
            }
        }
        $cursor = Split-Path -Parent $cursor
    }
}

function Confirm-StopBuildProcesses([string[]]$Roots) {
    $matches = @(Get-CimInstance Win32_Process | Where-Object {
        $exe = $_.ExecutablePath
        if ($exe) {
            foreach ($root in $Roots) {
                if ($exe.StartsWith([IO.Path]::GetFullPath($root).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
                    return $true
                }
            }
        }
        return $false
    })
    if ($matches.Count -eq 0) { return }
    $details = ($matches | ForEach-Object { "$($_.Name) (PID $($_.ProcessId))`n$($_.ExecutablePath)" }) -join "`n`n"
    Add-Type -AssemblyName System.Windows.Forms
    $answer = [Windows.Forms.MessageBox]::Show(
        "The following programs use build/output files.`nSave your work before continuing.`n`n$details`n`nClose these programs? Programs still running after 10 seconds will be terminated.",
        "MacroHID packaging - confirm close", [Windows.Forms.MessageBoxButtons]::YesNo,
        [Windows.Forms.MessageBoxIcon]::Warning, [Windows.Forms.MessageBoxDefaultButton]::Button2)
    if ($answer -ne [Windows.Forms.DialogResult]::Yes) { throw "Packaging cancelled. No running programs were closed." }
    foreach ($match in $matches) {
        $process = Get-Process -Id $match.ProcessId -ErrorAction SilentlyContinue
        if (-not $process) { continue }
        # Guard against PID reuse before acting on a process.
        if ($process.Path -ne $match.ExecutablePath) { throw "Process changed; rerun packaging." }
        $null = $process.CloseMainWindow()
        if (-not $process.WaitForExit(10000)) {
            $process.Kill()
            if (-not $process.WaitForExit(10000)) { throw "Could not close PID $($match.ProcessId)." }
        }
    }
}

function Publish-StagedOutputs([hashtable[]]$Entries, [string]$Stage) {
    $completed = @()
    try {
        foreach ($entry in $Entries) {
            Assert-ArtifactPath $entry.Source
            Assert-ArtifactPath $entry.Target
            if (-not (Test-Path -LiteralPath $entry.Source)) { throw "Missing output: $($entry.Source)" }
            $backup = Join-Path $Stage ("previous-" + [Guid]::NewGuid().ToString('N'))
            Assert-ArtifactPath $backup
            $state = @{ Entry = $entry; Backup = $backup; Saved = $false; Published = $false }
            $completed += $state
            if (Test-Path -LiteralPath $entry.Target) {
                Move-Item -LiteralPath $entry.Target -Destination $backup
                $state.Saved = $true
            }
            Move-Item -LiteralPath $entry.Source -Destination $entry.Target
            $state.Published = $true
        }
    }
    catch {
        $originalError = $_
        [array]::Reverse($completed)
        foreach ($state in $completed) {
            try {
                if ($state.Published) { Move-Item -LiteralPath $state.Entry.Target -Destination $state.Entry.Source }
                if ($state.Saved) { Move-Item -LiteralPath $state.Backup -Destination $state.Entry.Target }
            }
            catch { Write-Warning "Rollback failed; old output is preserved at $($state.Backup): $_" }
        }
        throw $originalError
    }
}
