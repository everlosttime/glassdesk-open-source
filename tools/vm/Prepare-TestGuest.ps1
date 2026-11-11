# Puts the repository and the .NET SDK into the test virtual machine.
#
# WHY THE FILES ARE COPIED RATHER THAN SHARED
#   The acceptance runs inside the guest exactly as it runs on the host: build, unit tests,
#   phase 3 and 4 acceptance, the UI acceptance group, then the desktop driver. That needs
#   the source tree and the SDK the build pins. Copying the two in keeps the guest's picture
#   of the build identical to the host's, and means the guest needs no network of its own.
#
#   The SDK is copied as the same official zip the host uses and extracted to C:\dotnet10,
#   so the guest mirrors the host layout that build.ps1 already knows how to find.
#
# WHAT IT DOES NOT DO
#   Nothing about the user's data: the guest account is a throwaway, and the checks in the
#   desktop driver refuse to run against a machine whose GlassDesk library already has entries.
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
    # Everything the test needs lives under the repo folder. Writing to the root of C: is denied
    # to this account (the drive root's ACL does not grant create-file to a standard token), and
    # a folder created for this purpose is writable.
    [string]$GuestSdkPath = 'C:\GlassDesk\dotnet10',
    [string]$SdkZip,
    [string]$Configuration = 'Release',
    [switch]$SkipRepo,
    [switch]$SkipSdk
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$UserPassword = & (Join-Path $PSScriptRoot 'Get-TestGuestPassword.ps1') -StagingDirectory $StagingDirectory -UserPassword $UserPassword

$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

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

function Invoke-NativeCapture {
    param([string]$Exe, [string[]]$Arguments)

    # VBoxManage writes transient complaints to stderr ("guest session ... still starting"), and
    # with $ErrorActionPreference = 'Stop' a merged 2>&1 turns those into terminating errors.
    # Callers here only care about the exit code and the text, so stderr is merged under
    # 'Continue' and both come back as a value instead of as an exception.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'

    try {
        $output = @(& $Exe @Arguments 2>&1 | ForEach-Object { $_.ToString() })
        return [pscustomobject]@{ Output = $output; ExitCode = $LASTEXITCODE }
    }
    finally {
        $ErrorActionPreference = $previous
    }
}

function Invoke-GuestControl {
    param([string[]]$Arguments)

    $result = Invoke-NativeCapture $manage (@('guestcontrol', $VmName) + $Arguments + $credentials)
    if ($result.ExitCode -ne 0) {
        throw "guestcontrol failed with exit code $($result.ExitCode) : $($Arguments -join ' ')`n$($result.Output -join "`n")"
    }

    return $result.Output
}

function Invoke-GuestPowerShell {
    param([string]$Command)

    return Invoke-GuestControl @('run', '--exe', 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe',
        '--', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', $Command)
}

Write-Host "VBoxManage: $manage"
Write-Host "repository: $repositoryRoot"

$state = (Invoke-NativeCapture $manage @('showvminfo', $VmName, '--machinereadable')).Output -join "`n"
if ($state -notmatch 'VMState="running"') {
    throw "VM '$VmName' is not running. Start it first (tools\vm\Install-TestGuest.ps1 starts it headless)."
}

Write-Host ''
Write-Host '== waiting for the Guest Additions control channel =='
$deadline = (Get-Date).AddMinutes(10)
$ready = $false

while ((Get-Date) -lt $deadline) {
    $probe = Invoke-NativeCapture $manage (@('guestcontrol', $VmName, 'stat', 'C:\') + $credentials)
    if ($probe.ExitCode -eq 0) { $ready = $true; break }
    Start-Sleep -Seconds 10
}

if (-not $ready) {
    throw 'The guest control channel did not come up. Is the guest logged in and are the Guest Additions running?'
}

Write-Host '  guest control channel is up'

# ---- prepare a copy of the source tree without build output ----

if (-not $SkipRepo) {
    Write-Host ''
    Write-Host '== copying the repository into the guest =='

    $staging = Join-Path $StagingDirectory 'repo-copy'
    if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }
    New-Item -ItemType Directory -Force -Path $staging | Out-Null

    $repoCopy = Join-Path $staging 'GlassDesk'
    New-Item -ItemType Directory -Force -Path $repoCopy | Out-Null

    # /MIR mirrors the tree; the exclusions keep build output, git history and scratch out of
    # the guest. The acceptance rebuilds everything from source there anyway.
    $arguments = @(
        $repositoryRoot, $repoCopy, '/MIR',
        '/XD', 'bin', 'obj', '.git', 'tmp', 'artifacts', '.vs', '.zcode',
        '/NFL', '/NDL', '/NJH', '/NJS', '/NP'
    )

    & robocopy @arguments | Out-Null
    # robocopy uses exit codes 0..7 for success; 8 and above are failures.
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed with exit code $LASTEXITCODE" }

    if (-not (Test-Path (Join-Path $repoCopy 'GlassDesk.slnx'))) {
        throw "the staged copy has no GlassDesk.slnx: $repoCopy"
    }

    # One archive instead of thousands of individual file transfers: guestcontrol's copyto has
    # no shell to expand a wildcard, so copying a tree means either one call per file or a
    # single archive. The guest already unpacks the SDK zip with tar.exe, so this reuses a
    # step that is known to work there.
    $archive = Join-Path $staging 'glassdesk-repo.zip'
    if (Test-Path $archive) { Remove-Item -Force $archive }
    Compress-Archive -Path (Join-Path $repoCopy '*') -DestinationPath $archive -CompressionLevel Optimal

    Write-Host ("  archive: {0:N0} bytes" -f (Get-Item $archive).Length)

    $guestArchive = Join-Path $GuestRepoPath 'glassdesk-repo.zip'

    # guestcontrol's createdirectory fails when the directory already exists, so the tolerant
    # New-Item -Force inside the guest is used instead.
    Invoke-GuestPowerShell ("New-Item -ItemType Directory -Force -Path '" + $GuestRepoPath + "' | Out-Null; 'ready'") | Out-Null
    Invoke-GuestControl @('copyto', $archive, $guestArchive)

    $unpack = '& "$env:SystemRoot\System32\tar.exe" -xf ''' + $guestArchive + ''' -C ''' + $GuestRepoPath + '''; ' +
        'Get-ChildItem ''' + $GuestRepoPath + ''' | Select-Object -ExpandProperty Name'

    Invoke-GuestPowerShell $unpack
    Write-Host "  unpacked into $GuestRepoPath"
}

# ---- copy and unpack the SDK ----

if (-not $SkipSdk) {
    Write-Host ''
    Write-Host '== installing the .NET SDK in the guest =='

    if (-not $SdkZip) {
        $SdkZip = Join-Path $StagingDirectory 'dotnet-sdk-10.0.401-win-x64.zip'
    }

    if (-not (Test-Path $SdkZip)) {
        Write-Host "The SDK zip is not there: $SdkZip" -ForegroundColor Yellow
        Write-Host 'Download it from the same place the host uses (see the repository README):'
        Write-Host '  https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.401/dotnet-sdk-10.0.401-win-x64.zip'
        exit 2
    }

    $guestZip = Join-Path $GuestRepoPath 'glassdesk-sdk.zip'
    Invoke-GuestPowerShell ("New-Item -ItemType Directory -Force -Path '" + $GuestSdkPath + "' | Out-Null; 'ready'") | Out-Null
    Invoke-GuestControl @('copyto', $SdkZip, $guestZip)
    Write-Host "  copied the SDK zip to $guestZip"

    # tar.exe ships with Windows and unpacks a zip, so no archive module is needed in the guest.
    $unpack = 'New-Item -ItemType Directory -Force -Path ''' + $GuestSdkPath + ''' | Out-Null; ' +
        '& "$env:SystemRoot\System32\tar.exe" -xf ''' + $guestZip + ''' -C ''' + $GuestSdkPath + '''; ' +
        '& ''' + $GuestSdkPath + '\dotnet.exe'' --version'

    Invoke-GuestControl @('run', '--exe', 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe',
        '--', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', $unpack)

    Write-Host '  SDK unpacked; the version above is what the guest reports'
}

Write-Host ''
Write-Host 'Guest prepared. Inside the guest, run:'
Write-Host "  `$env:DOTNET_ROOT = '$GuestSdkPath'"
Write-Host '  $env:GLASSDESK_ISOLATED_DESKTOP = ''1'''
Write-Host "  cd $GuestRepoPath"
Write-Host "  & .\build\build.ps1 -DesktopAcceptance -DesktopRepeat 3"
Write-Host ''
Write-Host 'Then take the second snapshot (02-acceptance-ready) and bring these back:'
Write-Host '  docs\evidence\phase4-desktop-latest.json (and the .log next to it)'
Write-Host '  %TEMP%\GlassDesk-...-<guid> (the per-round work directories with screenshots)'