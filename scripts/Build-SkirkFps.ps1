param([string]$Configuration = "Release")
$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$assets = Join-Path $root "src\tools\SkirkFps\Assets"
# Format-only conversion of the generated portrait into a Windows PNG-compressed ICO.
Add-Type -AssemblyName System.Drawing
$source = [Drawing.Image]::FromFile((Join-Path $assets "skirk-icon.png"))
$bitmap = New-Object Drawing.Bitmap 256,256
$graphics = [Drawing.Graphics]::FromImage($bitmap)
try {
    $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.DrawImage($source,0,0,256,256)
    $stream = New-Object IO.MemoryStream
    $bitmap.Save($stream,[Drawing.Imaging.ImageFormat]::Png)
    $png = $stream.ToArray()
    $writer = New-Object IO.BinaryWriter ([IO.File]::Create((Join-Path $assets "skirk.ico")))
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]1)
        $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$png.Length); $writer.Write([uint32]22); $writer.Write($png)
    } finally { $writer.Dispose(); $stream.Dispose() }
} finally { $graphics.Dispose(); $bitmap.Dispose(); $source.Dispose() }
$out = Join-Path $root ("artifacts\skirk-fps\" + (Get-Date -Format "yyyyMMdd-HHmmss-fff"))
dotnet publish (Join-Path $root "src\tools\SkirkFps\SkirkFps.csproj") -c $Configuration -r win-x64 --self-contained true -o $out --disable-build-servers -p:BuildInParallel=false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -v:minimal
if ($LASTEXITCODE -ne 0) { throw "YSRExtend publish failed ($LASTEXITCODE)." }
$exe = Join-Path $out "YSRExtend.exe"
if (-not (Test-Path $exe)) { throw "Missing $exe" }
Write-Host "YSRExtend executable: $exe"
