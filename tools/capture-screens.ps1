# Captures the desktop, or a region of it, to a PNG.
#
# Used to verify that the GlassDesk skeleton actually renders. Kept ASCII-only on purpose:
# Windows PowerShell 5.1 reads a .ps1 without a BOM using the system ANSI code page, so a
# script containing non-ASCII text fails to parse on a Chinese-locale machine.
#
# The process is made DPI-aware before measuring, otherwise the virtual screen bounds come
# back in scaled units and the capture is cropped or offset on a high-DPI display.

param(
    [Parameter(Mandatory = $true)]
    [string]$OutPath,

    # Capture only the primary monitor.
    [switch]$PrimaryOnly,

    # Capture an explicit region, in physical pixels of the virtual desktop.
    [int]$X = [int]::MinValue,
    [int]$Y = [int]::MinValue,
    [int]$Width = 0,
    [int]$Height = 0
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

if (-not ('GlassDesk.DpiAware' -as [type])) {
    Add-Type -Namespace 'GlassDesk' -Name 'DpiAware' -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool SetProcessDPIAware();
'@
}

[void][GlassDesk.DpiAware]::SetProcessDPIAware()

if ($PrimaryOnly) {
    $screen = [System.Windows.Forms.Screen]::PrimaryScreen
    $region = $screen.Bounds
}
elseif ($X -ne [int]::MinValue -and $Y -ne [int]::MinValue -and $Width -gt 0 -and $Height -gt 0) {
    $region = New-Object System.Drawing.Rectangle $X, $Y, $Width, $Height
}
else {
    $region = [System.Windows.Forms.SystemInformation]::VirtualScreen
}

$bitmap = New-Object System.Drawing.Bitmap $region.Width, $region.Height
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)

try {
    $graphics.CopyFromScreen($region.X, $region.Y, 0, 0, $bitmap.Size)

    $directory = Split-Path -Parent $OutPath
    if ($directory -and -not (Test-Path $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    $bitmap.Save($OutPath, [System.Drawing.Imaging.ImageFormat]::Png)
}
finally {
    $graphics.Dispose()
    $bitmap.Dispose()
}

Write-Output ("captured {0}x{1} at ({2},{3}) -> {4}" -f $region.Width, $region.Height, $region.X, $region.Y, $OutPath)