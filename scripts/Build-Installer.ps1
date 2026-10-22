param(
    [string]$Version = "2.1.0",

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [switch]$InstallInno,

    [string]$OutputRoot = "",

    [string]$LocalOutputRoot = "",

    [switch]$Launch,

    [switch]$SkipShortcut,

    [string]$ShortcutDirectory = "D:\360MoveData\Users\12849\Desktop\Start Item"
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$artifactRoot = Join-Path $repoRoot "artifacts"
$packageRoot = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    Join-Path $artifactRoot "packages"
} else { [IO.Path]::GetFullPath($OutputRoot) }
. (Join-Path $PSScriptRoot "Packaging-Helpers.ps1")
Assert-ArtifactPath $packageRoot
$fixedLocalRoot = if ([string]::IsNullOrWhiteSpace($LocalOutputRoot)) {
    Join-Path $artifactRoot "local-run"
} else { [IO.Path]::GetFullPath($LocalOutputRoot) }
Assert-ArtifactPath $fixedLocalRoot
if ($packageRoot.TrimEnd('\') -eq $fixedLocalRoot -or
    $packageRoot.StartsWith($fixedLocalRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
    $fixedLocalRoot.StartsWith($packageRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw "Package and local-run directories must not overlap."
}
# Build in a fresh staging directory. Published paths remain stable.
$outputRoot = Join-Path $artifactRoot ("packaging-stage-" + [Guid]::NewGuid().ToString('N'))
Assert-ArtifactPath $outputRoot

$inputRoot = Join-Path $outputRoot "build-input"
$issPath = Join-Path $repoRoot "installer\MacroHID.iss"

function Find-InnoCompiler {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
    )

    $pathCommand = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
    if ($pathCommand) {
        $candidates += $pathCommand.Source
    }

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path $candidate)) {
            return (Resolve-Path $candidate).Path
        }
    }

    return $null
}

function Copy-DirectoryIfExists([string]$Source, [string]$Destination) {
    if (-not (Test-Path $Source)) {
        return
    }

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Copy-Item -Path (Join-Path $Source "*") -Destination $Destination -Recurse -Force
}

function Publish-Checked([string]$Project, [string]$Destination, [bool]$SelfContained) {
    dotnet publish $Project --configuration $Configuration --runtime win-x64 `
        --self-contained $SelfContained.ToString().ToLowerInvariant() --output $Destination `
        --disable-build-servers -p:BuildInParallel=false -v:minimal
    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $Project (exit $LASTEXITCODE)." }
}

function Copy-PublicDocumentation([string]$Source, [string]$Destination) {
    Assert-ArtifactPath $Destination
    $sourcePrefix = [IO.Path]::GetFullPath($Source).TrimEnd('\') + '\'
    foreach ($file in Get-ChildItem -LiteralPath $Source -Recurse -File) {
        $relative = $file.FullName.Substring($sourcePrefix.Length).Replace('\', '/')
        if ($relative -in @('unlocker-target-cards.md', 'fufu-target-analysis.md') -or
            $relative.StartsWith('research/evidence-', [StringComparison]::OrdinalIgnoreCase)) { continue }
        $target = Join-Path $Destination $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target -Force
    }
}

function Copy-NativePlaybackDll([string]$DllPath, [string]$DestinationRoot) {
    foreach ($component in @("MacroStudio", "MacroRunner", "LatencyProbe")) {
        $destination = Join-Path $DestinationRoot $component
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        Copy-Item -LiteralPath $DllPath -Destination (Join-Path $destination "MacroHid.NativePlayback.dll") -Force
    }
}

function Copy-WindowsOcrServer([string]$SourceRoot, [string]$DestinationRoot) {
    foreach ($component in @("MacroStudio", "MacroRunner")) {
        $destination = Join-Path (Join-Path $DestinationRoot $component) "ocr"
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        Copy-Item -Path (Join-Path $SourceRoot "*") -Destination $destination -Recurse -Force
    }
}

function Assert-LegacyRuntimes([string]$StudioRoot) {
    . (Join-Path $PSScriptRoot "Legacy-Versions.ps1")
    foreach ($version in $LegacyVersions) {
        $dir = Join-Path $StudioRoot (Join-Path "legacy" $version.Folder)
        foreach ($file in @("MacroHid.Legacy.$($version.Folder).dll", "MacroHid.NativePlayback.$($version.Suffix).dll")) {
            if (-not (Test-Path (Join-Path $dir $file))) { throw "Legacy runtime file missing: $dir\$file" }
        }
    }
}

function Assert-WindowsOcrServerCopied([string]$DestinationRoot) {    foreach ($component in @("MacroStudio", "MacroRunner")) {
        $exe = Join-Path (Join-Path $DestinationRoot $component) "ocr\WindowsOcrServer.exe"
        if (-not (Test-Path $exe)) {
            throw "WindowsOcrServer.exe was not copied for ${component}: $exe"
        }
    }
}

function Remove-InstallerDebugArtifacts([string]$Root) {
    Get-ChildItem -Path $Root -Recurse -Include "*.pdb", "*.ilk" -File -ErrorAction SilentlyContinue |
        Remove-Item -Force
}

function New-PortableRootShortcut([string]$ShortcutPath, [string]$TargetPath, [string]$WorkingDirectory, [string]$Description) {
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($ShortcutPath)
    $shortcut.TargetPath = $TargetPath
    $shortcut.Arguments = ""
    $shortcut.WorkingDirectory = $WorkingDirectory
    $shortcut.WindowStyle = 1
    $shortcut.Description = $Description
    $shortcut.IconLocation = "$TargetPath,0"
    $shortcut.Save()
}

function New-PortableAppLayout([string]$SourceRoot, [string]$AppRoot) {
    Assert-ArtifactPath $AppRoot
    if (Test-Path $AppRoot) {
        Remove-Item -LiteralPath $AppRoot -Recurse -Force
    }

    New-Item -ItemType Directory -Path $AppRoot -Force | Out-Null

    foreach ($name in @("MacroStudio", "MacroRunner", "LatencyProbe", "scripts", "samples", "docs")) {
        Copy-DirectoryIfExists (Join-Path $SourceRoot $name) (Join-Path $AppRoot $name)
    }

    Copy-Item -LiteralPath (Join-Path $SourceRoot "README.md") -Destination (Join-Path $AppRoot "README.md") -Force

    $launchVbs = @"
Option Explicit
Dim fso, shell, root, exe
Set fso = CreateObject("Scripting.FileSystemObject")
root = fso.GetParentFolderName(WScript.ScriptFullName)
exe = fso.BuildPath(fso.BuildPath(root, "MacroStudio"), "MacroStudio.exe")
If Not fso.FileExists(exe) Then
    MsgBox "找不到 MacroStudio\MacroStudio.exe。请把快捷方式留在解压后的 MacroHID 根目录。", 16, "MacroHID"
    WScript.Quit 1
End If
Set shell = CreateObject("WScript.Shell")
shell.CurrentDirectory = fso.GetParentFolderName(exe)
shell.Run """" & exe & """", 1, False
"@
    Set-Content -LiteralPath (Join-Path $AppRoot "MacroStudio.vbs") -Value $launchVbs -Encoding Unicode

    $launchCmd = @"
@echo off
set "ROOT=%~dp0"
start "" "%ROOT%MacroStudio\MacroStudio.exe"
"@
    Set-Content -LiteralPath (Join-Path $AppRoot "MacroStudio.cmd") -Value $launchCmd -Encoding ASCII

    $missing = @(
        (Join-Path $AppRoot "MacroStudio\MacroStudio.exe"),
        (Join-Path $AppRoot "MacroStudio.cmd")
    ) | Where-Object { -not (Test-Path $_) }
    if ($missing.Count -gt 0) {
        throw "Portable layout is missing: $($missing -join ', ')"
    }
}

if ($InstallInno -and -not (Find-InnoCompiler)) {
    winget install --id JRSoftware.InnoSetup --exact --source winget --silent --accept-package-agreements --accept-source-agreements --disable-interactivity
}

$iscc = Find-InnoCompiler
if (-not $iscc) {
    throw "Inno Setup compiler ISCC.exe was not found. Install Inno Setup 6 or rerun with -InstallInno."
}

New-Item -ItemType Directory -Path $inputRoot -Force | Out-Null

# Prevent simultaneous packagers from replacing the same fixed outputs.
New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
$buildLock = [IO.File]::Open((Join-Path $artifactRoot 'packaging.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
Push-Location $repoRoot
try {
    Confirm-StopBuildProcesses @((Join-Path $repoRoot "src"), $fixedLocalRoot, $packageRoot)
    $nativeDll = & (Join-Path $PSScriptRoot "Build-NativePlayback.ps1") -Configuration $Configuration | Select-Object -Last 1
    # Earlier releases' native playback DLLs for the version switcher (published under MacroStudio\legacy\<V>\).
    & (Join-Path $PSScriptRoot "Build-LegacyNative.ps1") | Out-Host
    if (-not $?) { throw "legacy native build failed" }

    Publish-Checked "src\ui\MacroStudio\MacroStudio.csproj" (Join-Path $inputRoot "MacroStudio") $false
    Publish-Checked "src\tools\MacroRunner\MacroRunner.csproj" (Join-Path $inputRoot "MacroRunner") $false
    Publish-Checked "src\tools\LatencyProbe\LatencyProbe.csproj" (Join-Path $inputRoot "LatencyProbe") $false
    $ocrServerOutput = Join-Path $inputRoot "WindowsOcrServer"
    Publish-Checked "src\tools\WindowsOcrServer\WindowsOcrServer.csproj" $ocrServerOutput $false
    Copy-NativePlaybackDll $nativeDll $inputRoot
    Copy-WindowsOcrServer $ocrServerOutput $inputRoot
    Assert-WindowsOcrServerCopied $inputRoot
    Assert-LegacyRuntimes (Join-Path $inputRoot "MacroStudio")

    Copy-DirectoryIfExists (Join-Path $repoRoot "scripts") (Join-Path $inputRoot "scripts")
    Copy-DirectoryIfExists (Join-Path $repoRoot "samples") (Join-Path $inputRoot "samples")
    Copy-PublicDocumentation (Join-Path $repoRoot "docs") (Join-Path $inputRoot "docs")
    Copy-Item -LiteralPath (Join-Path $repoRoot "README.md") -Destination $inputRoot -Force
    Remove-InstallerDebugArtifacts $inputRoot

    $isccArgs = @(
        "/DSourceDir=$inputRoot",
        "/DOutputDir=$outputRoot",
        "/DAppVersion=$Version",
        $issPath
    )

    & $iscc @isccArgs
    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup compiler failed with exit code $LASTEXITCODE."
    }

    $installer = Join-Path $outputRoot "MacroHID-Setup-x64.exe"
    if (-not (Test-Path $installer)) {
        throw "Expected installer was not produced: $installer"
    }

    $portableStage = Join-Path $outputRoot "portable-stage"
    $portableAppRoot = Join-Path $portableStage "MacroHID"
    $portableZip = Join-Path $outputRoot "MacroHID-Portable-x64.zip"
    Assert-ArtifactPath $portableStage
    if (Test-Path $portableStage) {
        Remove-Item -LiteralPath $portableStage -Recurse -Force
    }

    if (Test-Path $portableZip) {
        Remove-Item -LiteralPath $portableZip -Force
    }

    New-PortableAppLayout -SourceRoot $inputRoot -AppRoot $portableAppRoot

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $portableStage,
        $portableZip,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false)

    # Keep the standalone desktop build separate from the smaller distribution packages.
    $localRoot = Join-Path $outputRoot "local-run"
    $localStudio = Join-Path $localRoot "MacroStudio"
    Publish-Checked "src\ui\MacroStudio\MacroStudio.csproj" $localStudio $true
    Publish-Checked "src\tools\WindowsOcrServer\WindowsOcrServer.csproj" (Join-Path $localStudio "ocr") $true
    Copy-Item -LiteralPath $nativeDll -Destination (Join-Path $localStudio "MacroHid.NativePlayback.dll") -Force
    Assert-LegacyRuntimes $localStudio
    $localExe = Join-Path $localStudio "MacroStudio.exe"
    if (-not (Test-Path -LiteralPath $localExe)) { throw "Standalone executable missing: $localExe" }
    # Recheck after publishing in case a user started the old build meanwhile.
    Confirm-StopBuildProcesses @($fixedLocalRoot, $packageRoot)
    New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
    $fixedInstaller = Join-Path $packageRoot "MacroHID-Setup-x64.exe"
    $fixedZip = Join-Path $packageRoot "MacroHID-Portable-x64.zip"
    $checksums = Join-Path $outputRoot "SHA256SUMS.txt"
    $checksumLines = foreach ($packageFile in @($installer, $portableZip)) {
        "{0}  {1}" -f (Get-FileHash -LiteralPath $packageFile -Algorithm SHA256).Hash.ToLowerInvariant(), (Split-Path -Leaf $packageFile)
    }
    Set-Content -LiteralPath $checksums -Value $checksumLines -Encoding ascii
    Publish-StagedOutputs @(
        @{ Source = $installer; Target = $fixedInstaller },
        @{ Source = $portableZip; Target = $fixedZip },
        @{ Source = $checksums; Target = (Join-Path $packageRoot "SHA256SUMS.txt") },
        @{ Source = $localRoot; Target = $fixedLocalRoot }
    ) $outputRoot
    $installer = $fixedInstaller
    $portableZip = $fixedZip
    $localStudio = Join-Path $fixedLocalRoot "MacroStudio"
    $localExe = Join-Path $localStudio "MacroStudio.exe"
    # Antivirus (e.g. Kaspersky VHO:Trojan.Win32.Sdum) can quarantine the freshly published apphost.
    Start-Sleep -Seconds 3
    $missingOutputs = @($installer, $portableZip, $localExe) | Where-Object { -not (Test-Path -LiteralPath $_) }
    if ($missingOutputs.Count -gt 0) {
        throw ("Published outputs disappeared after packaging (likely removed by antivirus): " + ($missingOutputs -join ', ') +
            ". Add an exclusion for $artifactRoot and restore the files from quarantine, then rerun packaging.")
    }
    if (-not $SkipShortcut) {
        New-Item -ItemType Directory -Path $ShortcutDirectory -Force | Out-Null
        $shortcutName = "MacroHID " + [char]0x672C + [char]0x5730 + [char]0x7248 + ".lnk"
        $desktopShortcut = Join-Path $ShortcutDirectory $shortcutName
        New-PortableRootShortcut -ShortcutPath $desktopShortcut -TargetPath $localExe `
            -WorkingDirectory $localStudio -Description "MacroHID $Version local standalone build"
        Write-Host "Shortcut: $desktopShortcut"
    }
    Write-Host "Installer created: $installer"
    Write-Host "Portable zip created: $portableZip"
    Write-Host "Standalone executable: $localExe"
    Write-Host "Previous outputs and build staging retained at: $outputRoot"
    if ($Launch) { Start-Process -FilePath $localExe -WorkingDirectory $localStudio -WindowStyle Hidden }
}
finally {
    $buildLock.Dispose()
    Pop-Location
}
