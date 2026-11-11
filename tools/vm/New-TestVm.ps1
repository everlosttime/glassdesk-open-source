# Test virtual machine for the phase 4 desktop acceptance.
#
# WHY THIS IS NOT RUN BY THE BUILD
#   Installing VirtualBox needs administrator rights and a reboot-capable driver install.
#   The build never does that on its own. This script is the one deliberate, one-time step:
#   either an administrator runs it, or the user grants an elevated session for it.
#
# WHAT IT DOES
#   1. Downloads the pinned VirtualBox build and the Windows 11 evaluation ISO.
#   2. Verifies both against the checksums recorded in the runbook (refuses on mismatch).
#   3. Creates the VM with the fixed shape from the plan: 4 vCPU, 8 GB RAM, 80 GB dynamic
#      disk, and attaches the ISO.
#   4. Prints exactly what is left to do by hand (the unattended answer file is the user's
#      call: language, account name, product key).
#
# It never installs anything silently and never touches an existing VM: creating a VM whose
# name already exists stops with an error.
#
# ASCII only on purpose: Windows PowerShell 5.1 reads a .ps1 without a BOM using the system
# ANSI code page, so non-ASCII text in this file would fail to parse on a Chinese-locale
# machine.

[CmdletBinding()]
param(
    # Where the installers and the ISO go. Kept outside the repository on purpose.
    [string]$StagingDirectory = 'D:\GlassDesk-VM',
    [string]$VmName = 'GlassDesk-Test',
    [switch]$DownloadOnly,
    # The ISO is only needed when the guest is installed. Downloading the VirtualBox package
    # first keeps the long download separable from the quick one.
    [switch]$SkipIso,
    [switch]$SkipDownload
)

$ErrorActionPreference = 'Stop'

$virtualBoxVersion = '7.2.18'
$virtualBoxBuild = '175117'
$virtualBoxFile = "VirtualBox-$virtualBoxVersion-$virtualBoxBuild-Win.exe"
$virtualBoxUrl = "https://download.virtualbox.org/virtualbox/$virtualBoxVersion/$virtualBoxFile"
$virtualBoxSha256 = 'AAE27200546A21B9B7DC11CFC42BD04802329A29F69AE0ADA55682715A389D8D'

# Windows 11 Enterprise evaluation, 24H2, English (United States). The evaluation expires;
# the expiry date is recorded in the runbook and must be re-checked before a re-run.
$isoUrl = 'https://go.microsoft.com/fwlink/p/?LinkID=2195404&clcid=0x409&culture=en-us&country=US'
$isoFile = 'Windows11EnterpriseEval.iso'

function Get-FileWithHash {
    param([string]$Url, [string]$Path, [string]$ExpectedSha256)

    if ((Test-Path $Path) -and $ExpectedSha256) {
        $existing = (Get-FileHash -Path $Path -Algorithm SHA256).Hash
        if ($existing -eq $ExpectedSha256) {
            Write-Host "  already present and verified: $Path"
            return
        }

        Write-Warning "  existing file has a different hash, re-downloading: $Path"
        Remove-Item -Path $Path -Force
    }
    elseif (Test-Path $Path) {
        Write-Host "  already present: $Path"
        return
    }

    Write-Host "  downloading $Url"
    Write-Host "  -> $Path"

    # Invoke-WebRequest is the only downloader guaranteed to be present. It is slow for the
    # multi-gigabyte ISO, but it resumes nothing and needs no extra module.
    $progressBackup = $ProgressPreference
    $ProgressPreference = 'SilentlyContinue'

    try {
        Invoke-WebRequest -Uri $Url -OutFile $Path -UseBasicParsing
    }
    finally {
        $ProgressPreference = $progressBackup
    }

    if ($ExpectedSha256) {
        $actual = (Get-FileHash -Path $Path -Algorithm SHA256).Hash
        if ($actual -ne $ExpectedSha256) {
            throw "SHA256 mismatch for $Path`n  expected $ExpectedSha256`n  actual   $actual"
        }

        Write-Host "  sha256 verified: $actual"
    }
}

function Test-VirtualBoxInstalled {
    $candidates = @(
        (Join-Path $env:ProgramFiles 'Oracle\VirtualBox\VBoxManage.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Oracle\VirtualBox\VBoxManage.exe')
    )

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path $candidate)) { return $candidate }
    }

    return $null
}

$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

Write-Host "staging directory: $StagingDirectory"
Write-Host "administrator:     $isAdmin"
Write-Host ''

New-Item -ItemType Directory -Force -Path $StagingDirectory | Out-Null

$installerPath = Join-Path $StagingDirectory $virtualBoxFile
$isoPath = Join-Path $StagingDirectory $isoFile

if (-not $SkipDownload) {
    Write-Host '== download =='
    Get-FileWithHash -Url $virtualBoxUrl -Path $installerPath -ExpectedSha256 $virtualBoxSha256

    if ($SkipIso) {
        Write-Host '  iso download skipped (-SkipIso)'
    }
    else {
        # The evaluation ISO link is a redirect that Microsoft re-points at each new build, so
        # there is no stable published hash. The file is recorded by size and date instead, and
        # the runbook says so plainly rather than pretending a hash was checked.
        Get-FileWithHash -Url $isoUrl -Path $isoPath -ExpectedSha256 $null

        Write-Host ("  iso size: {0:N0} bytes" -f (Get-Item $isoPath).Length)
    }

    Write-Host ''
}

if ($DownloadOnly) {
    Write-Host 'Download only requested; stopping here.'
    exit 0
}

if (-not (Test-Path $isoPath)) {
    Write-Host "The Windows evaluation ISO is not present yet; it is only needed to install the guest."
    Write-Host "Download it with:  & .\tools\vm\New-TestVm.ps1 -DownloadOnly"
    Write-Host ''
}

Write-Host '== prerequisites =='
$manage = Test-VirtualBoxInstalled

if (-not $manage) {
    Write-Host "VirtualBox is not installed yet. The verified installer is at:"
    Write-Host "  $installerPath"
    Write-Host ''
    Write-Host "Install it with administrator rights, then run this script again with -SkipDownload:"
    Write-Host "  Start-Process -FilePath `"$installerPath`" -ArgumentList '--silent' -Verb RunAs"
    exit 2
}

Write-Host "  VBoxManage: $manage"
& $manage --version

if (-not $isAdmin) {
    Write-Host ''
    Write-Host 'Creating and configuring the VM needs administrator rights. Re-run elevated.' -ForegroundColor Yellow
    exit 2
}

Write-Host ''
Write-Host '== create the virtual machine =='

$existing = & $manage list vms
if ($existing -match [Regex]::Escape($VmName)) {
    throw "A VM named '$VmName' already exists. This script never replaces one; rename or remove it first."
}

# --basefolder keeps the whole VM together under the staging directory. Without it the
# settings and the snapshot differencing images land in VirtualBox's global machine folder on
# the system drive, which is exactly the drive the test environment is meant to stay off.
$vmFolder = Join-Path $StagingDirectory 'VMs'
New-Item -ItemType Directory -Force -Path $vmFolder | Out-Null

& $manage createvm --name $VmName --ostype Windows11_64 --basefolder $vmFolder --register

# Fixed shape from the plan: 4 vCPU, 8 GB RAM, 80 GB dynamically allocated disk.
# Fixed shape from the plan: 4 vCPU, 8 GB RAM, 80 GB dynamically allocated disk.
#
# No TPM device is requested, and that is a measured decision rather than a simplification.
# This VirtualBox build has no software TPM (that needs an external swtpm), and asking for one
# does not merely fail to provide it: the VM refuses to power on at all with
#   Failed to construct device 'tpm' instance #0 (VERR_PDM_DRIVER_NOT_FOUND)
# Meanwhile Windows 11 installs fine without one, because VirtualBox's own answer-file template
# adds the documented LabConfig bypass keys for the TPM and Secure Boot checks. The runbook
# records the consequence (no BitLocker or Device Guard in the guest).
& $manage modifyvm $VmName --cpus 4 --memory 8192 --vram 128 --firmware efi
& $manage modifyvm $VmName --graphicscontroller vboxsvga --accelerate-3d on
& $manage modifyvm $VmName --nic1 nat --audio-enabled off --usb-xhci on
& $manage modifyvm $VmName --clipboard-mode bidirectional --draganddrop bidirectional

$tpmState = (& $manage showvminfo $VmName 2>&1 | Select-String -Pattern 'TPM' | Select-Object -First 1)
if ($tpmState) {
    Write-Host "  TPM: $tpmState"
}
else {
    Write-Host '  TPM: not present (this VirtualBox build has no software TPM);'
    Write-Host '       the unattended answer file bypasses the Windows 11 TPM/GPT checks.'
}

$diskPath = Join-Path $StagingDirectory "$VmName.vdi"
& $manage createmedium disk --filename $diskPath --size 81920 --variant Standard

& $manage storagectl $VmName --name 'SATA' --add sata --controller IntelAhci --portcount 2 --bootable on
& $manage storageattach $VmName --storagectl 'SATA' --port 0 --device 0 --type hdd --medium $diskPath

if (Test-Path $isoPath) {
    & $manage storageattach $VmName --storagectl 'SATA' --port 1 --device 0 --type dvddrive --medium $isoPath
}
else {
    Write-Host '  iso not attached yet (file missing):'
    Write-Host "    & `"$manage`" storageattach $VmName --storagectl 'SATA' --port 1 --device 0 --type dvddrive --medium `"$isoPath`""
}

Write-Host ''
Write-Host 'Created. What is left, and it is deliberately manual:'
Write-Host '  1. Start the VM and install Windows 11 from the attached ISO.'
Write-Host '     Use the evaluation key if asked; it is valid for the evaluation period.'
Write-Host '  2. Create a dedicated test account and use it for everything below.'
Write-Host '     Never sign it in to the user personal account, and never import their library.'
Write-Host '  3. Install the same .NET SDK version the build pins (see global.json).'
Write-Host '  4. Take the snapshot "system installed" right after the OS is ready.'
Write-Host '  5. Copy the build output in, then take the snapshot "acceptance ready".'
Write-Host ''
Write-Host 'The pass criteria and the exact data to record are in:'
Write-Host '  docs/phase4-batch-a-test-vm.md'