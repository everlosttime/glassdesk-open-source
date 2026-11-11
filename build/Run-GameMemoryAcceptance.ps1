[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$CandidateDirectory,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [string]$InstalledDirectory = 'D:\GlassDesk',
    [int]$LongRunSeconds = 1800,
    [switch]$Quick,
    [switch]$SkipBaseline
)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'Run this acceptance script with PowerShell 7 (pwsh), not Windows PowerShell.' }
$candidate = [IO.Path]::GetFullPath($CandidateDirectory)
$output = [IO.Path]::GetFullPath($OutputDirectory)
$installed = [IO.Path]::GetFullPath($InstalledDirectory)
if (Test-Path -LiteralPath $output) { throw 'Acceptance requires a fresh output directory.' }
if (-not (Test-Path -LiteralPath (Join-Path $candidate 'Testing/GlassDesk.LauncherAcceptance.exe'))) { throw 'Missing candidate test payload.' }
if (-not (Test-Path -LiteralPath (Join-Path $installed 'Data'))) { throw 'Missing installed profile to copy.' }
if ($output.StartsWith($installed + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Do not stage inside the installed application.' }
$null = New-Item -ItemType Directory -Path $output -Force

function Profile-Manifest([string]$Profile) {
    @((Get-ChildItem -LiteralPath $Profile -File -Recurse | Where-Object {
        $relative = [IO.Path]::GetRelativePath($Profile, $_.FullName)
        $relative -notmatch '^(logs|recovery|backup)[\\/]' -and $_.Extension -ne '.lease' -and $_.Name -notlike '*.lock'
    } | Sort-Object FullName) | ForEach-Object {
        [pscustomobject]@{path=[IO.Path]::GetRelativePath($Profile,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
    })
}
function Copy-Payload([string]$Source,[string]$Destination) {
    $null = New-Item -ItemType Directory -Path $Destination -Force
    foreach ($item in Get-ChildItem -LiteralPath $Source -Force) {
        if ($item.Name -eq 'Data') { continue }
        Copy-Item -LiteralPath $item.FullName -Destination $Destination -Recurse -Force
    }
}
function Send-InstalledCommand([string]$Command) {
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $sessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId
    $pipeName = "GlassDesk.Pipe.$sid.$sessionId.GlassDesk.Host"
    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', $pipeName, [IO.Pipes.PipeDirection]::Out)
    try {
        $pipe.Connect(2000)
        $bytes = [Text.Encoding]::UTF8.GetBytes($Command + "`n")
        $pipe.Write($bytes,0,$bytes.Length); $pipe.Flush()
    } finally { $pipe.Dispose() }
}
$installedProcesses = @(Get-CimInstance Win32_Process | Where-Object {
    $_.ExecutablePath -eq (Join-Path $installed 'GlassDesk.exe')
} | ForEach-Object { [Diagnostics.Process]::GetProcessById($_.ProcessId) })
$wasRunning = $installedProcesses.Count -gt 0
$before = Profile-Manifest (Join-Path $installed 'Data')
$results = [Collections.Generic.List[object]]::new()
$test = $null
try {
    if ($wasRunning) {
        Send-InstalledCommand 'exit'
        foreach ($owner in $installedProcesses) {
            if (-not $owner.WaitForExit(10000)) { throw 'Installed application did not exit cleanly. No process was forcibly terminated.' }
        }
    }
    $profile = Join-Path $output 'input-profile'
    Copy-Item -LiteralPath (Join-Path $installed 'Data') -Destination $profile -Recurse
    $before | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'live-profile-before.json') -Encoding utf8
    foreach ($name in @('baseline','candidate')) {
        if ($name -eq 'baseline' -and $SkipBaseline) { continue }
        $stage = Join-Path $output ($name + '-payload')
        $evidence = Join-Path $output $name
        Copy-Payload (Join-Path $candidate 'Testing') $stage
        if ($name -eq 'baseline') { Copy-Payload $installed $stage }
        Copy-Item -LiteralPath $profile -Destination (Join-Path $stage 'Data') -Recurse
        $null = New-Item -ItemType Directory -Path $evidence -Force
        [IO.File]::WriteAllText((Join-Path $stage '.full-host-acceptance-root'),$stage.TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar)
        $phase = Join-Path $evidence 'phase.txt'
        $report = Join-Path $evidence 'full-host-performance.json'
        $arguments = @('--full-host-performance','--report', ('"'+$report+'"'),'--phase-file',('"'+$phase+'"'))
        if ($Quick) { $arguments += '--quick' }
        if ($name -eq 'baseline') { $arguments += '--baseline' }
        elseif ($LongRunSeconds -gt 0) { $arguments += @('--long-run-seconds',$LongRunSeconds.ToString()) }
        $test = Start-Process -FilePath (Join-Path $stage 'GlassDesk.LauncherAcceptance.exe') -ArgumentList $arguments `
            -WorkingDirectory $stage -WindowStyle Hidden -PassThru `
            -RedirectStandardOutput (Join-Path $evidence 'stdout.log') -RedirectStandardError (Join-Path $evidence 'stderr.log')
        Write-Output "Started $name actual AppHost acceptance, PID $($test.Id)."
        & (Join-Path $PSScriptRoot 'Measure-GlassDeskProcessGroup.ps1') -RootProcessId $test.Id -PhaseFile $phase `
            -OutputPath (Join-Path $evidence 'process-group-samples.json') -MaximumSeconds ([Math]::Max(2700,$LongRunSeconds+1800)) -CollectGpu
        $test.WaitForExit()
        $completedExitCode = $test.ExitCode
        $results.Add([ordered]@{kind=$name;processId=$test.Id;exitCode=$test.ExitCode;report=$report})
        Write-Output "Completed $name, exit $($test.ExitCode)."
        $test.Dispose(); $test = $null
        if ($name -eq 'candidate' -and $completedExitCode -eq 0) {
            Get-ChildItem -LiteralPath $evidence -Directory | Where-Object {
                Test-Path -LiteralPath (Join-Path $_.FullName 'first-frame-recording.json')
            } | ForEach-Object { & (Join-Path $PSScriptRoot 'Set-FirstFrameGifTiming.ps1') -RecordingDirectory $_.FullName }
        }
        if ($name -eq 'candidate' -and -not $Quick -and $completedExitCode -eq 0) {
            $inputEvidence = Join-Path $output 'candidate-input'
            $null = New-Item -ItemType Directory -Path $inputEvidence -Force
            $inputReport = Join-Path $inputEvidence 'full-host-input.json'
            Write-Output 'Starting complete-host native mouse, long-press drag and paging regression.'
            $test = Start-Process -FilePath (Join-Path $stage 'GlassDesk.LauncherAcceptance.exe') `
                -ArgumentList @('--full-host-input','--report',('"'+$inputReport+'"')) -WorkingDirectory $stage `
                -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $inputEvidence 'stdout.log') `
                -RedirectStandardError (Join-Path $inputEvidence 'stderr.log')
            $test.WaitForExit()
            $results.Add([ordered]@{kind='candidate-input';processId=$test.Id;exitCode=$test.ExitCode;report=$inputReport})
            Write-Output "Completed native input, exit $($test.ExitCode)."
            $test.Dispose(); $test = $null
        }
    }
} finally {
    if ($test -and -not $test.HasExited) {
        # Only the process started above, on a disposable staged profile.
        $test.Kill($true); $test.WaitForExit(5000) | Out-Null
    }
    if ($test) { $test.Dispose() }
    $after = Profile-Manifest (Join-Path $installed 'Data')
    $unchanged = (ConvertTo-Json -InputObject $before -Depth 4 -Compress) -eq (ConvertTo-Json -InputObject $after -Depth 4 -Compress)
    [ordered]@{timestamp=[DateTimeOffset]::Now;quick=[bool]$Quick;longRunSeconds=$LongRunSeconds;
        liveProfileUnchanged=$unchanged;liveInstallationReplaced=$false;copiedDataUsed=$true;results=$results.ToArray()} |
        ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $output 'acceptance-run.json') -Encoding utf8
    if ($wasRunning) { Start-Process -FilePath (Join-Path $installed 'GlassDesk.exe') -ArgumentList '--autostart' -WindowStyle Hidden }
    foreach ($owner in $installedProcesses) { $owner.Dispose() }
}
if (@($results | Where-Object { $_.exitCode -ne 0 }).Count -gt 0) {
    throw 'Full-host acceptance failed; inspect the recorded reports and stderr logs.'
}
