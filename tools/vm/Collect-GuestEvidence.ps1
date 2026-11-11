# Waits for the acceptance run inside the guest and brings the evidence back to the host.
#
# The guest is driven through the file channels only (stat / copyfrom), because that is what
# works in this environment: guestcontrol's "run" fails here with "The specified user was not
# able to logon on guest" while the file side is fine. The bootstrap in the guest's Startup
# folder does the executing; this script does the watching and the collecting.
#
# ASCII only on purpose: Windows PowerShell 5.1 reads a .ps1 without a BOM using the system
# ANSI code page, so non-ASCII text in this file would fail to parse on a Chinese-locale
# machine.

[CmdletBinding()]
param(
    [string]$StagingDirectory = 'D:\GlassDesk-VM',
    [string]$VmName = 'GlassDesk-Test',
    [string]$UserName = 'gdtester',
    [string]$UserPassword = $env:GLASSDESK_VM_PASSWORD,
    [string]$GuestRepoPath = 'C:\GlassDesk',
    [int]$TimeoutMinutes = 120
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$UserPassword = & (Join-Path $PSScriptRoot 'Get-TestGuestPassword.ps1') -StagingDirectory $StagingDirectory -UserPassword $UserPassword

function Find-VBoxManage {
    $candidates = @(
        (Join-Path $env:ProgramFiles 'Oracle\VirtualBox\VBoxManage.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Oracle\VirtualBox\VBoxManage.exe')
    )

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path $candidate)) { return $candidate }
    }

    throw 'VBoxManage.exe not found.'
}

$manage = Find-VBoxManage
$credentials = @('--username', $UserName, '--password', $UserPassword)
$evidence = Join-Path $StagingDirectory 'evidence'
New-Item -ItemType Directory -Force -Path $evidence | Out-Null

# File checks answer with an exit code; a "not found" on stderr must not become fatal.
function Test-GuestFile {
    param([string]$GuestPath)

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'

    try {
        $null = & $manage guestcontrol $VmName stat $GuestPath @credentials 2>&1
        return $LASTEXITCODE -eq 0
    }
    finally {
        $ErrorActionPreference = $previous
    }
}

function Get-GuestFile {
    param([string]$GuestPath, [string]$LocalName, [switch]$Quiet)

    $target = Join-Path $evidence $LocalName
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'

    try {
        $null = & $manage guestcontrol $VmName copyfrom $GuestPath $target @credentials 2>&1
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }

    if ($code -eq 0 -and -not $Quiet) { Write-Host "  $LocalName" }
    return $code -eq 0
}

$guestBootstrap = Join-Path $GuestRepoPath 'go2.txt'
$guestExit = Join-Path $GuestRepoPath 'go2.exit'

Write-Host "watching $VmName for the acceptance to finish"

$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
$lastReport = Get-Date
$finished = $false

while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 60

    # The progress file is copied every minute: it is the only window into the guest while the
    # run is in progress.
    if (Test-GuestFile $guestBootstrap) {
        $null = Get-GuestFile $guestBootstrap 'guest-bootstrap.txt' -Quiet
    }

    if (Test-GuestFile $guestExit) {
        $null = Get-GuestFile $guestExit 'guest-acceptance.exit' -Quiet
        $exitFile = Join-Path $evidence 'guest-acceptance.exit'
        $content = $null

        if (Test-Path $exitFile) {
            $content = (Get-Content $exitFile -Raw -ErrorAction SilentlyContinue)
        }

        # The script writes a placeholder ("running") before it starts, so the file existing is
        # not the signal: only a numeric exit code means the acceptance is over.
        if ($content -and ($content.Trim() -match '^\d+$')) {
            $finished = $true
            break
        }
    }

    if (((Get-Date) - $lastReport).TotalMinutes -ge 5) {
        $tail = '(no progress file yet)'
        $progress = Join-Path $evidence 'guest-bootstrap.txt'

        if (Test-Path $progress) {
            $tail = (Get-Content $progress -Tail 4 | Out-String).Trim()
        }

        Write-Host "  still running ($((Get-Date).ToString('HH:mm:ss')))"
        Write-Host "    $tail"
        $lastReport = Get-Date
    }
}

Write-Host ''
Write-Host '== collecting =='

$wanted = @(
    @{ Guest = (Join-Path $GuestRepoPath 'go2.txt'); Name = 'guest-bootstrap.txt' },
    @{ Guest = (Join-Path $GuestRepoPath 'go2-acceptance.log'); Name = 'guest-acceptance.log' },
    @{ Guest = (Join-Path $GuestRepoPath 'go2.exit'); Name = 'guest-acceptance.exit' },
    @{ Guest = (Join-Path $GuestRepoPath 'probe-windows.txt'); Name = 'guest-probe-windows.txt' },
    @{ Guest = (Join-Path $GuestRepoPath 'docs\evidence\phase4-desktop-latest.json'); Name = 'guest-phase4-desktop-latest.json' },
    @{ Guest = (Join-Path $GuestRepoPath 'docs\evidence\phase4-desktop-latest.log'); Name = 'guest-phase4-desktop-latest.log' },
    @{ Guest = (Join-Path $GuestRepoPath 'docs\evidence\phase4-ui-latest.json'); Name = 'guest-phase4-ui-latest.json' },
    @{ Guest = (Join-Path $GuestRepoPath 'docs\evidence\phase4-final-build.log'); Name = 'guest-phase4-final-build.log' }
)

$collected = @()
foreach ($item in $wanted) {
    if (-not (Test-GuestFile $item.Guest)) { continue }
    if (Get-GuestFile $item.Guest $item.Name) { $collected += (Join-Path $evidence $item.Name) }
}

# A desktop screenshot says more than any log about what the guest looked like at the end.
$previous = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
try { $null = & $manage controlvm $VmName keyboardputscancode 2a aa 2>&1 } finally { $ErrorActionPreference = $previous }
Start-Sleep -Seconds 4

$screenshot = Join-Path $evidence 'guest-desktop.png'
$null = & $manage controlvm $VmName screenshotpng $screenshot 2>&1
if (Test-Path $screenshot) { $collected += $screenshot }

Write-Host ''
if (-not $finished) {
    Write-Host "No exit code appeared within $TimeoutMinutes minutes." -ForegroundColor Yellow
    Write-Host 'The log copied so far is the place to look.'
    exit 2
}

$code = (Get-Content (Join-Path $evidence 'guest-acceptance.exit') -Raw).Trim()
$meaning = switch ($code) {
    '0' { 'all requested cases passed' }
    '1' { 'the product failed at least one case' }
    '2' { 'blocked or not run (environment, missing artifact, locked session)' }
    default { "unexpected exit code '$code'" }
}

Write-Host "Guest acceptance exit code: $code - $meaning"
Write-Host ''
Write-Host 'Collected:'
$collected | ForEach-Object { Write-Host "  $_" }

if ($code -ne '0') { exit 1 }