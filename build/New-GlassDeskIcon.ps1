# Generates the multi-resolution Windows icon from the approved shared brand PNG.
[CmdletBinding()]
param(
    [string]$OutputPath,
    [string]$PreviewPath,
    [string]$SourcePath
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputPath) { $OutputPath = Join-Path $repoRoot 'src/GlassDesk.App/Assets/GlassDesk.ico' }
if (-not $SourcePath) { $SourcePath = Join-Path $repoRoot 'src/GlassDesk.App/Assets/GlassDeskBrand.png' }

Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName WindowsBase

$mark = New-Object System.Windows.Media.Imaging.BitmapImage
$mark.BeginInit()
$mark.CacheOption = [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad
$mark.UriSource = [Uri]::new([IO.Path]::GetFullPath($SourcePath))
$mark.EndInit()
$mark.Freeze()

$frames = @()
foreach ($size in @(16, 20, 24, 32, 40, 48, 64, 128, 256)) {
    $visual = New-Object System.Windows.Media.DrawingVisual
    $context = $visual.RenderOpen()
    [System.Windows.Media.RenderOptions]::SetBitmapScalingMode($visual, [System.Windows.Media.BitmapScalingMode]::HighQuality)
    $context.DrawImage($mark, (New-Object System.Windows.Rect(0, 0, $size, $size)))
    $context.Close()

    $bitmap = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $pngStream = New-Object System.IO.MemoryStream
    $encoder.Save($pngStream)
    $frames += [pscustomobject]@{ Size = $size; Bytes = $pngStream.ToArray() }
    if ($PreviewPath -and $size -eq 256) {
        $previewDirectory = Split-Path -Parent $PreviewPath
        if ($previewDirectory -and -not (Test-Path -LiteralPath $previewDirectory)) {
            New-Item -ItemType Directory -Path $previewDirectory | Out-Null
        }
        [System.IO.File]::WriteAllBytes($PreviewPath, $pngStream.ToArray())
    }
    $pngStream.Dispose()
}

$outputDirectory = Split-Path -Parent $OutputPath
if ($outputDirectory -and -not (Test-Path -LiteralPath $outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory | Out-Null
}
$icoStream = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($icoStream)
try {
    $writer.Write([UInt16]0)
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]$frames.Count)
    $imageOffset = 6 + (16 * $frames.Count)
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -eq 256) { [byte]0 } else { [byte]$frame.Size }
        $writer.Write($dimension)
        $writer.Write($dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([UInt16]1)
        $writer.Write([UInt16]32)
        $writer.Write([UInt32]$frame.Bytes.Length)
        $writer.Write([UInt32]$imageOffset)
        $imageOffset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
    $writer.Flush()
    [System.IO.File]::WriteAllBytes($OutputPath, $icoStream.ToArray())
}
finally {
    $writer.Dispose()
    $icoStream.Dispose()
}

Write-Output "Created $OutputPath with $($frames.Count) PNG icon sizes."
