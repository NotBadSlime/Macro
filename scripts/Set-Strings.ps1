param(
    # Path to a JSON file: { "Key": { "en": "...", "zh-CN": "...", "zh-TW": "..." }, ... }
    [Parameter(Mandatory = $true)][string]$Strings,
    # Only write these languages (default all). Keys missing from a language's JSON entry are skipped for it.
    [string[]]$Languages = @("en", "zh-CN", "zh-TW")
)
# Adds or replaces <data> entries in Strings.resx / Strings.zh-CN.resx / Strings.zh-TW.resx.
# Keeps each file's line endings and writes UTF-8 without a BOM change (preserves an existing BOM).
$ErrorActionPreference = "Stop"
$root = Join-Path (Resolve-Path (Join-Path $PSScriptRoot "..")) "src\ui\MacroStudio\Resources"
$map = Get-Content -Raw -Encoding UTF8 $Strings | ConvertFrom-Json
$files = @{ "en" = "Strings.resx"; "zh-CN" = "Strings.zh-CN.resx"; "zh-TW" = "Strings.zh-TW.resx" }
foreach ($lang in $files.Keys) {
    if ($Languages -notcontains $lang) { continue }
    $path = Join-Path $root $files[$lang]
    $bytes = [IO.File]::ReadAllBytes($path)
    $bom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $text = [Text.Encoding]::UTF8.GetString($bytes, $(if ($bom) { 3 } else { 0 }), $bytes.Length - $(if ($bom) { 3 } else { 0 }))
    $nl = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }
    $added = 0; $updated = 0
    foreach ($prop in $map.PSObject.Properties) {
        $key = $prop.Name
        $value = $prop.Value.$lang
        if ($null -eq $value) { $value = $prop.Value."zh-CN" }
        $escaped = [Security.SecurityElement]::Escape($value)
        $line = "  <data name=`"$key`" xml:space=`"preserve`"><value>$escaped</value></data>"
        $pattern = '(?m)^[ \t]*<data name="' + [regex]::Escape($key) + '"[^>]*>(?:(?!</data>)[\s\S])*</data>[ \t]*\r?$\n?'
        $matches = [regex]::Matches($text, $pattern)
        if ($matches.Count -gt 0) {
            # Replace the first occurrence, drop any duplicates of the same key.
            for ($i = $matches.Count - 1; $i -ge 1; $i--) { $text = $text.Remove($matches[$i].Index, $matches[$i].Length) }
            $text = $text.Remove($matches[0].Index, $matches[0].Length).Insert($matches[0].Index, $line + $nl)
            $updated++
        } else {
            $at = $text.LastIndexOf("</root>")
            $text = $text.Substring(0, $at) + $line + $nl + $text.Substring($at)
            $added++
        }
    }
    $encoding = New-Object Text.UTF8Encoding($bom)
    [IO.File]::WriteAllText($path, $text, $encoding)
    "{0}: added {1}, updated {2}" -f $files[$lang], $added, $updated
}
