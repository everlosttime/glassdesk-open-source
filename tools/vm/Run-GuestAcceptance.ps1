# Runs the desktop acceptance inside the guest, in the user's interactive session, and brings
# the evidence back to the host.
#
# WHY A SCHEDULED TASK INSTEAD OF guestcontrol run
#   The desktop driver injects real mouse and keyboard input, so it has to run on the desktop a
#   user is actually looking at. A process started through the guest control service can end up
#   in the service session instead, where there is no interactive desktop. The driver's own
#   environment gate detects exactly that and reports it as blocked, so the failure would be
#   visible rather than mysterious - but it would still be a wasted run. A scheduled task
#   created with an Interactive principal runs in the logged-in user's session, which is
#   the desktop we need.
#
# WHY A .CMD FILE
#   The task is pointed at a batch file instead of carrying the command inline. Passing a
#   PowerShell command through schtasks means nesting quotes through two parsers, which is
#   fragile in a way that only shows up at run time. A file has no quoting to get wrong.
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
    [string]$GuestSdkPath = 'C:\GlassDesk\dotnet10',
    [string]$Configuration = 'Release',
    [int]$Repeat = 3,
    [int]$TimeoutMinutes = 90,
    [ValidateSet('all','picker','explorer','launch','reorder','focus','magnification','autohide','desktop','packaged','settings')]
    [string]$Case = 'all',
    [switch]$DesktopOnly,
    # Read-only diagnostic: run the driver's window dump in the same session and fetch it.
    # This answers "is this really an interactive desktop?" without injecting any input.
    [switch]$ProbeOnly
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
$apiLock = $null
$channel = $null
$ownedChannels = [Collections.Generic.List[object]]::new()

function Open-GuestChannel {
    if ($script:channel) { return }
    $api = New-Object -ComObject VirtualBox.VirtualBox
    $script:apiLock = New-Object -ComObject VirtualBox.Session
    $api.FindMachine($VmName).LockMachine($script:apiLock, 1)
    # A late guest-side session can retain an ID after a CLI startup timeout. Keep the
    # failed object until cleanup so the next explicit attempt receives a different ID.
    for ($attempt = 1; $attempt -le 2; $attempt++) {
        $candidate = $script:apiLock.Console.Guest.CreateSession($UserName, $UserPassword, '', ('GlassDesk-' + $runId + '-' + $attempt))
        $ownedChannels.Add($candidate)
        try {
            $wait = $candidate.WaitFor(1, 30000)
            if ($wait -ne 1 -or $candidate.Status -ne 100) { throw "Guest session not ready ($wait)." }
            $script:channel = $candidate
            Write-Host "Guest channel ready: session $($candidate.Id)"
            return
        }
        catch {
            Write-Host "Guest channel attempt $attempt failed (session $($candidate.Id))."
            if ($attempt -eq 2) { throw }
        }
    }
}

function Wait-GuestTransfer {
    param($Progress)
    $Progress.WaitForCompletion(60000)
    if (-not $Progress.Completed) {
        if ($Progress.Cancelable) { $Progress.Cancel() }
        throw 'Guest file transfer timed out.'
    }
    if ($Progress.ResultCode -ne 0) { throw "Guest file transfer failed: $($Progress.ErrorInfo.Text)" }
}

function Invoke-Guest {
    param([string[]]$Arguments)

    Open-GuestChannel
    switch ($Arguments[0]) {
        'mkdir' { $channel.DirectoryCreate($Arguments[-1], 0, [int[]]@(1)); return }
        'copyto' { Wait-GuestTransfer ($channel.FileCopyToGuest($Arguments[1], $Arguments[2], [int[]]@())); return }
        'copyfrom' { Wait-GuestTransfer ($channel.FileCopyFromGuest($Arguments[1], $Arguments[2], [int[]]@())); return }
        default { throw 'Unsupported guest channel operation.' }
    }
}

function Invoke-GuestPowerShell {
    param([string]$Command)

    Open-GuestChannel
    $commandId = [Guid]::NewGuid().ToString('N')
    $guestOutput = Join-Path $guestRun ($commandId + '.txt')
    $localOutput = Join-Path $evidence ($commandId + '.txt')
    $wrapped = @"
`$ErrorActionPreference='Stop'; `$ProgressPreference='SilentlyContinue'
try { `$result = & {
$Command
}; `$result | Out-String -Width 240 | Set-Content -LiteralPath '$guestOutput' -Encoding UTF8; exit 0 }
catch { `$_.Exception.Message | Set-Content -LiteralPath '$guestOutput' -Encoding UTF8; exit 1 }
"@
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($wrapped))
    $executable = 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe'
    $process = $channel.ProcessCreate($executable,
        [string[]]@($executable,'-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-EncodedCommand',$encoded),
        $GuestRepoPath, [string[]]@(), [int[]]@(4), 60000)
    # WaitFor(Terminate) can report Timeout after the process already reached 500.
    # Poll the authoritative process status and retain the exact exit code.
    $commandDeadline = (Get-Date).AddSeconds(60)
    while ($process.Status -lt 500 -and (Get-Date) -lt $commandDeadline) { Start-Sleep -Milliseconds 250 }
    if ($process.Status -ne 500) { throw "Guest command did not terminate normally (status=$($process.Status))." }
    Invoke-Guest @('copyfrom', $guestOutput, $localOutput)
    $output = Get-Content -LiteralPath $localOutput -Raw -Encoding UTF8
    if ($process.ExitCode -ne 0) { throw "Guest command failed: $output" }
    return $output
}

# Existence checks are answered by the exit code, so a "file not found" on stderr must not be
# allowed to travel as a terminating error.
function Test-GuestFile {
    param([string]$GuestPath)

    Open-GuestChannel
    return [bool]$channel.FileExists($GuestPath, $false)
}

try {
$runId = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssfffZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$startedAt = (Get-Date).ToUniversalTime().ToString('o')
$evidence = Join-Path $StagingDirectory ('evidence\' + $runId)
New-Item -ItemType Directory -Force -Path $evidence | Out-Null

$taskName = 'GlassDeskAcceptance-' + $runId
$guestRun = Join-Path $GuestRepoPath ('acceptance-runs\' + $runId)
$guestLog = Join-Path $guestRun 'acceptance.log'
$guestExit = Join-Path $guestRun 'acceptance.exit'
$guestProbe = Join-Path $guestRun 'probe-windows.txt'
$guestScript = Join-Path $guestRun 'run-acceptance.cmd'

Write-Host "VBoxManage: $manage"

# ---- compose the batch file that the task will run ----

$batch = if ($ProbeOnly) {
    @(
        '@echo off',
        "set DOTNET_ROOT=$GuestSdkPath",
        "set PATH=$GuestSdkPath;%PATH%",
        'set GLASSDESK_ISOLATED_DESKTOP=1',
        "cd /d `"$GuestRepoPath`"",
        "echo RunId=$runId > `"$guestProbe`"",
        "echo Started=%DATE% %TIME% >> `"$guestProbe`"",
        "tests\GlassDesk.DesktopAcceptance\bin\$Configuration\net10.0-windows\GlassDesk.DesktopAcceptance.exe --probe-session >> `"$guestProbe`" 2>&1",
        'set result=%ERRORLEVEL%',
        "echo %result% > `"$guestExit`"",
        'exit /b %result%'
    )
}
elseif ($DesktopOnly) {
    @(
        '@echo off',
        "set DOTNET_ROOT=$GuestSdkPath",
        "set PATH=$GuestSdkPath;%PATH%",
        'set GLASSDESK_ISOLATED_DESKTOP=1',
        "cd /d `"$GuestRepoPath`"",
        "tests\GlassDesk.DesktopAcceptance\bin\$Configuration\net10.0-windows\GlassDesk.DesktopAcceptance.exe --case $Case --repeat $Repeat --timeout $TimeoutMinutes --report `"$guestRun\desktop.json`" > `"$guestLog`" 2>&1",
        'set result=%ERRORLEVEL%',
        "echo %result% > `"$guestExit`"",
        'exit /b %result%'
    )
}
else {
    @(
        '@echo off',
        "set DOTNET_ROOT=$GuestSdkPath",
        "set PATH=$GuestSdkPath;%PATH%",
        # build.ps1 looks for the SDK in this order: -DotnetPath, GLASSDESK_DOTNET, D:\dotnet10,
        # then PATH. The guest's SDK is at C:\dotnet10, so setting this variable is what actually
        # makes the build find it instead of failing with "No .NET SDK found".
        "set GLASSDESK_DOTNET=$GuestSdkPath\dotnet.exe",
        'set GLASSDESK_ISOLATED_DESKTOP=1',
        "cd /d `"$GuestRepoPath`"",
        "powershell -NoProfile -ExecutionPolicy Bypass -File build\build.ps1 -DesktopAcceptance -DesktopRepeat $Repeat -DesktopReport `"$guestRun\desktop.json`" > `"$guestLog`" 2>&1",
        'set result=%ERRORLEVEL%',
        "echo %result% > `"$guestExit`"",
        'exit /b %result%'
    )
}

$localBatch = Join-Path $evidence 'run-acceptance.cmd'
Set-Content -Path $localBatch -Value $batch -Encoding ASCII

Write-Host ''
Write-Host '== sending the task script into the guest =='
$batchLines = $batch -join "`n"
Write-Host $batchLines
Invoke-Guest @('mkdir', '--parents', $guestRun) | Out-Null
Invoke-Guest @('copyto', $localBatch, $guestScript) | Out-Null

# ---- create and start the task in the interactive session ----

Write-Host ''
Write-Host '== starting it as an interactive scheduled task =='

$create = @"
`$action = New-ScheduledTaskAction -Execute 'C:\Windows\System32\cmd.exe' -Argument '/d /c ""$guestScript""' -WorkingDirectory '$GuestRepoPath'
`$principal = New-ScheduledTaskPrincipal -UserId '$UserName' -LogonType Interactive -RunLevel Limited
`$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes $TimeoutMinutes)
Register-ScheduledTask -TaskName '$taskName' -Action `$action -Principal `$principal -Settings `$settings | Out-Null
Start-ScheduledTask -TaskName '$taskName'
"@
Invoke-GuestPowerShell $create | Out-Null

Write-Host '  task started; waiting for the exit code file'

# ---- wait for the exit code ----

$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
$exitCode = $null
$lastReport = Get-Date

while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 3

    if (-not (Test-GuestFile $guestExit)) {
        if (((Get-Date) - $lastReport).TotalMinutes -ge 2) {
            Write-Host "  still running... ($((Get-Date).ToString('HH:mm:ss')))"
            $lastReport = Get-Date
        }

        continue
    }

    $text = Invoke-GuestPowerShell ('Get-Content -Path ''' + $guestExit + ''' -Raw')
    if (($text -join ' ').Trim() -match '^(-?\d+)$') {
        $exitCode = [int]$Matches[1]
        break
    }
}

# ---- bring the evidence back ----

Write-Host ''
Write-Host '== copying evidence back =='

$collected = @()

$wanted = @(
    @{ Guest = $guestExit; Name = 'guest-acceptance.exit' },
    @{ Guest = $guestLog; Name = 'guest-acceptance.log' },
    @{ Guest = $guestProbe; Name = 'guest-probe-windows.txt' }
)
if (-not $ProbeOnly) { $wanted += @(
    @{ Guest = (Join-Path $guestRun 'desktop.json'); Name = 'desktop.json' },
    @{ Guest = (Join-Path $guestRun 'desktop.log'); Name = 'desktop.log' }
) }

foreach ($item in $wanted) {
        if (-not (Test-GuestFile $item.Guest)) { continue }

    $target = Join-Path $evidence $item.Name
    Invoke-Guest @('copyfrom', $item.Guest, $target)
    Write-Host "  $($item.Name)"
    $collected += $target
}

$screenshot = Join-Path $evidence 'guest-desktop-after-acceptance.png'
$null = & $manage controlvm $VmName screenshotpng $screenshot 2>&1
if (Test-Path $screenshot) { $collected += $screenshot }

[ordered]@{
    runId = $runId
    startedAt = $startedAt
    finishedAt = (Get-Date).ToUniversalTime().ToString('o')
    vm = $VmName
    probeOnly = [bool]$ProbeOnly
    desktopOnly = [bool]$DesktopOnly
    requestedCase = $Case
    taskName = $taskName
    guestOutputDirectory = $guestRun
    rawExitCode = $exitCode
    collected = $collected
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $evidence 'run.json') -Encoding UTF8

# This unique task belongs to this invocation. Do not alter other tasks or processes.
if ($null -ne $exitCode) {
    Invoke-GuestPowerShell "Unregister-ScheduledTask -TaskName '$taskName' -Confirm:`$false" | Out-Null
}

Write-Host ''
if ($null -eq $exitCode) {
    Write-Host "No exit code appeared within $TimeoutMinutes minutes." -ForegroundColor Yellow
    Write-Host 'Read the copied log: the run may still be in progress, or the task never started.'
    exit 2
}

$meaning = switch ($exitCode) {
    0 { 'all requested cases passed' }
    1 { 'the product failed at least one case' }
    2 { 'blocked or not run (environment, missing artifact, locked session)' }
    default { "unexpected exit code $exitCode" }
}

Write-Host "Guest acceptance exit code: $exitCode - $meaning"
Write-Host ''
Write-Host 'Collected:'
$collected | ForEach-Object { Write-Host "  $_" }

if ($exitCode -eq 0) { exit 0 }
if ($exitCode -eq 1) { exit 1 }
exit 2
}
finally {
    foreach ($ownedChannel in $ownedChannels) {
        try { $ownedChannel.Close() } catch { Write-Warning "Could not close owned guest session $($ownedChannel.Id)." }
    }
    if ($apiLock) { $apiLock.UnlockMachine() }
}
