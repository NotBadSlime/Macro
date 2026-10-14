param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$Launch
)

$ErrorActionPreference = "Stop"
& (Join-Path $PSScriptRoot "Build-Installer.ps1") -Configuration $Configuration -Launch:$Launch
