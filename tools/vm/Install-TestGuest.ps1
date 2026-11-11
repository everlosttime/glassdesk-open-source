# Installs Windows in the test virtual machine, unattended, and snapshots the result.
#
# WHY UNATTENDED
#   The whole point of this VM is that nobody has to sit in front of it. VirtualBox generates
#   the answer file from its own template (UnattendedTemplates\win_nt6_unattended.xml), which
#   already carries the LabConfig bypass keys Windows 11 needs when the VM has no TPM or
#   Secure Boot device, and an AutoLogon block so the test account ends up on a real desktop
#   instead of the lock screen. That last part matters: the desktop driver refuses to run
#   unless there is an interactive desktop.
#
#   The VM runs headless. Headless only means the host shows no window; inside the guest there
#   is still a normal session with a real desktop, which is what the driver checks for.
#
# BEFORE RUNNING
#   Put the Windows 11 evaluation ISO where this script can find it. Microsoft's evaluation
#   centre requires a one-time registration, so the ISO cannot be fetched automatically:
#     https://www.microsoft.com/en-us/evalcenter/evaluate-windows-11-enterprise
#   Default location: <StagingDirectory>\Windows11EnterpriseEval.iso (or pass -IsoPath).
#
# ASCII only on purpose: Windows PowerShell 5.1 reads a .ps1 without a BOM using the system
# ANSI code page, so non-ASCII text in this file would fail to parse on a Chinese-locale
# machine.

[CmdletBinding()]
param(
    [string]$StagingDirectory = 'D:\GlassDesk-VM',
    [string]$VmName = 'GlassDesk-Test',
    [string]$IsoPath,
    # A throwaway account that exists only inside this VM. It never touches the user's data.
    [string]$UserName = 'gdtester',
    [string]$UserPassword = $env:GLASSDESK_VM_PASSWORD,
    [string]$FullUserName = 'GlassDesk Tester',
    # Must match a locale that exists in the ISO you put in. The evaluation centre offers both
    # English and Chinese images: pass -Locale zh_CN -Country CN when the ISO is the Chinese
    # one, or the answer file asks for a language the image does not have.
    [string]$Locale = 'en_US',
    [string]$Country = 'US',
    # The answer file's UILanguage has to match the installation media, and the media usually
    # carries exactly one language. Left empty it is derived from -Locale; check what the ISO
    # actually has with: VBoxManage unattended detect --iso=<file>
    [string]$Language,
    # VBoxManage rejects a bare name here: it wants "name.domain" and fails with
    # "Incomplete hostname ... must include both a name and a domain". A caller passing just a
    # name gets the domain appended rather than hitting that wall.
    [string]$HostName = 'glassdesk-test.local',
    [string]$SnapshotName = '01-system-installed',
    [int]$TimeoutMinutes = 120,
    [switch]$NoStart,
    # Hashing a 6 GB image takes about a minute; skip it when re-running the same ISO.
    [switch]$SkipHash,
    # Skip the install and just wait for a logged-in desktop (useful after a manual install).
    [switch]$WaitOnly
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

    throw 'VBoxManage.exe not found. Install VirtualBox first (tools\vm\New-TestVm.ps1).'
}

$manage = Find-VBoxManage
Write-Host "VBoxManage: $manage"

$info = & $manage showvminfo $VmName 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "Virtual machine '$VmName' does not exist. Create it with tools\vm\New-TestVm.ps1."
}

if (-not $WaitOnly) {
    # Installing configures the VM and starts it, so it has to be powered off first.
    if ($info -match 'State:\s+powered off') {
        Write-Host "VM state: powered off"
    }
    else {
        throw "VM '$VmName' is not powered off. Power it off before installing."
    }
}
else {
    # -WaitOnly just watches a guest that is already being installed (or already running).
    if ($info -match 'State:\s+running') {
        Write-Host "VM state: running"
    }
    else {
        throw "VM '$VmName' is not running. Start it first, or drop -WaitOnly to install."
    }
}

$evidenceDirectory = Join-Path $StagingDirectory 'evidence'
New-Item -ItemType Directory -Force -Path $evidenceDirectory | Out-Null
$record = Join-Path $StagingDirectory 'guest-record.txt'

if (-not $WaitOnly) {
    if (-not $IsoPath) {
        $IsoPath = Join-Path $StagingDirectory 'Windows11EnterpriseEval.iso'
    }

    if (-not (Test-Path $IsoPath)) {
        Write-Host ''
        Write-Host "The Windows 11 evaluation ISO is not there: $IsoPath" -ForegroundColor Yellow
        Write-Host 'Download it from the Microsoft evaluation centre (registration required):'
        Write-Host '  https://www.microsoft.com/en-us/evalcenter/evaluate-windows-11-enterprise'
        Write-Host "Then either put it at that exact path or re-run with -IsoPath '<where you saved it>'."
        exit 2
    }

    $iso = Get-Item $IsoPath

    if ($SkipHash) {
        $hash = '(skipped: -SkipHash)'
        Write-Host '  iso sha256 skipped (-SkipHash)'
    }
    else {
        Write-Host '  hashing the ISO (about a minute for 6 GB)...'
        $hash = (Get-FileHash -Path $IsoPath -Algorithm SHA256).Hash
    }

    # Microsoft does not publish a stable hash for the evaluation ISOs (the link is
    # re-pointed at each build), so the record carries the measurement instead of a
    # verification it cannot honestly claim.
    $lines = @(
        "iso path      : $($iso.FullName)",
        "iso size      : $($iso.Length) bytes",
        "iso writetime : $($iso.LastWriteTime.ToString('s'))",
        "iso sha256    : $hash",
        "vm name       : $VmName",
        "test account  : $UserName",
        "recorded at   : $((Get-Date).ToString('s'))"
    )

    $lines | Set-Content -Path $record -Encoding UTF8
    Write-Host ''
    Write-Host 'ISO record:'
    $lines | ForEach-Object { Write-Host "  $_" }

    Write-Host ''
    Write-Host '== unattended install =='
    Write-Host 'The guest installs and reboots by itself; this can take 15 to 40 minutes.'

    if (-not $Language) {
        # en_US -> en-US. This is the form the answer file needs.
        $Language = $Locale.Replace('_', '-')
    }

    if (-not $HostName.Contains('.')) {
        $HostName = "$HostName.local"
        Write-Host "hostname has no domain, using: $HostName"
    }

    Write-Host '== unattended detect =='
    & $manage unattended detect --iso=$IsoPath
    Write-Host ''
    Write-Host "locale=$Locale country=$Country language=$Language hostname=$HostName"

    $arguments = @(
        'unattended', 'install', $VmName,
        "--iso=$IsoPath",
        "--user=$UserName",
        "--user-password=$UserPassword",
        "--full-user-name=$FullUserName",
        "--locale=$Locale",
        "--country=$Country",
        "--language=$Language",
        "--time-zone=Asia/Shanghai",
        "--hostname=$HostName",
        '--install-additions'
    )

    if (-not $NoStart) {
        $arguments += '--start-vm=headless'
    }

    & $manage @arguments
    if ($LASTEXITCODE -ne 0) { throw "unattended install failed with exit code $LASTEXITCODE" }

    Write-Host ''
    Write-Host 'Answer file and ISO attached. VM started headless.'
}

Write-Host ''
Write-Host '== waiting for a logged-in desktop =='
Write-Host 'The guest properties only answer once the Guest Additions are running.'

$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
$loggedIn = $false
$lastReport = Get-Date
$poll = 0

# VBoxManage writes transient complaints to stderr (a screenshot taken while the guest is
# between video modes, for example). With $ErrorActionPreference = 'Stop' and a 2>&1 merge,
# PowerShell turns that into a terminating error and the whole wait dies on something
# harmless. Nothing in this loop is fatal, so errors are tolerated here and only here.
$previousPreference = $ErrorActionPreference
$ErrorActionPreference = 'Continue'

try {
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 20
        $poll++

        # guestproperty enumerate instead of get: enumerate is a single read of everything and
        # does not block on a missing key. A get on a VM that is mid-reboot was observed to hang
        # the whole loop with no output at all, which is the worst kind of failure here.
        $properties = & $manage guestproperty enumerate $VmName

        $users = ($properties | Select-String -Pattern '/VirtualBox/GuestInfo/OS/LoggedInUsers' -SimpleMatch | Select-Object -First 1)
        $version = ($properties | Select-String -Pattern '/VirtualBox/GuestInfo/OS/Version' -SimpleMatch | Select-Object -First 1)

        if ($users -and ($users.ToString() -match "Value:\s*'?(\d+)")) {
            $count = [int]$Matches[1]
            if ($count -ge 1) {
                Write-Host "  logged-in users: $count"
                Write-Host "  guest OS: $version"
                $loggedIn = $true
                break
            }
        }

        # A screenshot every few minutes is the only view into a guest that is still installing.
        if ($poll % 6 -eq 0) {
            $frame = Join-Path $evidenceDirectory ("guest-waiting-{0:HHmm}.png" -f (Get-Date))
            $null = & $manage controlvm $VmName screenshotpng $frame
        }

        if (((Get-Date) - $lastReport).TotalMinutes -ge 2) {
            Write-Host "  still waiting... ($((Get-Date).ToString('HH:mm:ss')))"
            $lastReport = Get-Date
        }
    }
}
finally {
    $ErrorActionPreference = $previousPreference
}

$screenshot = Join-Path $evidenceDirectory 'guest-after-install.png'

# PowerShell 5.1 has no discard assignment for commands, so the result goes to $null.
$null = & $manage controlvm $VmName screenshotpng $screenshot 2>&1
Write-Host "screenshot: $screenshot"

if (-not $loggedIn) {
    Write-Host ''
    Write-Host "No logged-in desktop appeared within $TimeoutMinutes minutes." -ForegroundColor Yellow
    Write-Host 'Look at the screenshot first: the install may still be running, or it may be sitting'
    Write-Host 'at the lock screen (AutoLogon only runs the configured number of times).'
    Write-Host "Take the snapshot manually once the desktop is up:"
    Write-Host "  & `"$manage`" snapshot $VmName take `"$SnapshotName`""
    exit 2
}

Write-Host ''
Write-Host '== snapshot =='
& $manage snapshot $VmName take $SnapshotName
if ($LASTEXITCODE -ne 0) { throw "taking the snapshot failed with exit code $LASTEXITCODE" }

Add-Content -Path $record -Value @(
    "guest os      : $version",
    "snapshot      : $SnapshotName"
)

Write-Host ''
Write-Host "Snapshot '$SnapshotName' taken. Next:"
Write-Host '  1. Copy the build output into the guest and install the .NET SDK the build pins'
Write-Host '     (global.json), then run the acceptance inside the guest:'
Write-Host '       $env:GLASSDESK_ISOLATED_DESKTOP = 1'
Write-Host '       & .\build\build.ps1 -DesktopAcceptance -DesktopRepeat 3'
Write-Host "  2. Take the second snapshot ('02-acceptance-ready') once the guest is prepared."
Write-Host "Record file: $record"