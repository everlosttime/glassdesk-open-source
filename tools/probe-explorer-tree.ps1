# Read-only calibration probe for the Explorer drag source.
#
# It opens a real Explorer window on a unique folder, dumps that window's UIA tree, and then
# closes ONLY that window (matched through the Shell COM window collection by our unique
# folder name). No input is injected, no process is killed.
#
# The target path is read from a UTF-8 file written by the caller: Windows PowerShell 5.1
# reads a .ps1 without a BOM using the ANSI code page, so a non-ASCII literal inside this
# script would be mangled.

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$repo = Split-Path -Parent $PSScriptRoot
$driver = Join-Path $repo 'tests\GlassDesk.DesktopAcceptance\bin\Release\net10.0-windows\GlassDesk.DesktopAcceptance.exe'
$targetFile = Join-Path $env:TEMP 'gd-explorer-target.txt'

if (-not (Test-Path $targetFile)) { throw "target file not found: $targetFile" }

$folder = (Get-Content -Path $targetFile -Raw -Encoding UTF8).Trim()
$leaf = Split-Path -Leaf $folder
Write-Host "folder: $folder"
Write-Host "leaf:   $leaf"

Start-Process -FilePath 'explorer.exe' -ArgumentList $folder
Start-Sleep -Seconds 7

$dump = Join-Path $env:TEMP 'gd-explorer-tree.txt'
if (Test-Path $dump) { Remove-Item $dump -Force }

$explorers = Get-Process -Name explorer -ErrorAction SilentlyContinue
Write-Host "explorer processes: $($explorers.Count)"

foreach ($process in $explorers) {
    & $driver --dump-uia $process.Id *>> $dump
}

$lines = Get-Content -Path $dump -Encoding UTF8
Write-Host "dump lines: $($lines.Count)"

Write-Host '--- windows whose title contains the unique folder name ---'
$indexes = @()
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -like "*$leaf*" -and $lines[$i] -like '*ControlType.Window*') { $indexes += $i }
}

if ($indexes.Count -eq 0) {
    Write-Host 'NOT FOUND: no window title matched the unique folder name'
} else {
    $start = $indexes[0]
    Write-Host "window line $start"
}

Write-Host '--- item-ish lines (ListItem / DataItem / Image) ---'
Select-String -Path $dump -Pattern 'ControlType.ListItem|ControlType.DataItem|ControlType.Image' |
    Select-Object -First 25 | ForEach-Object { $_.Line.Trim() }

Write-Host '--- lines mentioning the file name ---'
$pattern = [Regex]::Escape($leaf)
Select-String -Path $dump -Pattern $pattern -SimpleMatch | Select-Object -First 15 | ForEach-Object { $_.Line.Trim() }

# Close only our window. It is matched through the Shell window collection by the folder the
# window is actually showing: the LocationURL is percent-encoded and may hold the 8.3 short
# path, so neither a raw nor an escaped leaf name matches it reliably.
$shell = New-Object -ComObject Shell.Application
$closed = 0
foreach ($window in @($shell.Windows())) {
    $path = $null
    try { $path = $window.Document.Folder.Self.Path } catch { $path = $null }

    if ($null -ne $path -and $path.EndsWith($leaf)) {
        $window.Quit()
        $closed++
    }
}

Start-Sleep -Seconds 2
$remaining = 0
foreach ($window in @($shell.Windows())) {
    $path = $null
    try { $path = $window.Document.Folder.Self.Path } catch { $path = $null }

    if ($null -ne $path -and $path.EndsWith($leaf)) { $remaining++ }
}

Write-Host "closed windows: $closed; remaining: $remaining"
Write-Host "evidence: $dump"