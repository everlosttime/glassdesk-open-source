# Read-only calibration probe: start the picker fixture, dump the native dialog's UIA tree,
# then stop only that process after checking its image path. No input is injected.
$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

# The repo root is derived from this script's own location: Windows PowerShell 5.1 reads a
# .ps1 without a BOM using the system ANSI code page, so any non-ASCII path inside the file
# would be mangled.
$repo = Split-Path -Parent $PSScriptRoot
$fixture = Join-Path $repo 'tests\GlassDesk.UiAcceptance\bin\Release\net10.0-windows\GlassDesk.UiAcceptance.exe'
$driver = Join-Path $repo 'tests\GlassDesk.DesktopAcceptance\bin\Release\net10.0-windows\GlassDesk.DesktopAcceptance.exe'
$temp = $env:TEMP
$report = Join-Path $temp 'gd-picker-probe.json'
$stdout = Join-Path $temp 'gd-picker-probe.out'
$tree = Join-Path $temp 'gd-picker-probe-tree.txt'

if (-not (Test-Path $fixture)) { throw "fixture not found: $fixture" }

Write-Host "fixture: $fixture"
$p = Start-Process -FilePath $fixture -ArgumentList '--setup-only', '--picker', '--report', $report -PassThru -RedirectStandardOutput $stdout
Write-Host "pid: $($p.Id)"

# Give the fixture time to show its Dock and open the real file dialog.
Start-Sleep -Seconds 12

if ($p.HasExited) {
    Write-Host "fixture exited early, exit code $($p.ExitCode)"; Get-Content $stdout; exit 1
}

Write-Host '--- top level windows of the fixture process ---'
& $driver --dump-uia $p.Id *> $tree
Get-Content $tree | Select-Object -First 60

Write-Host '--- keyword scan in the dump ---'
Select-String -Path $tree -Pattern 'Edit|Button|Dialog|32770|文件名|File name|打开|Open' | Select-Object -First 40 | ForEach-Object { $_.Line.Trim() }

# Cleanup: verify the process is still the one we started before stopping it.
$current = Get-Process -Id $p.Id -ErrorAction SilentlyContinue
if ($null -ne $current -and $current.Path -eq $fixture) {
    Stop-Process -Id $p.Id -Force
    Write-Host "stopped fixture pid $($p.Id)"
} elseif ($null -ne $current) {
    Write-Host "NOT stopping pid $($p.Id): image path is $($current.Path)"
} else {
    Write-Host "pid $($p.Id) already gone"
}

Write-Host "evidence: $tree"