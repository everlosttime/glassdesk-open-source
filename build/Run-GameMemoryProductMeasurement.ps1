[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$CandidateDirectory,
    [Parameter(Mandatory=$true)][string]$ProfileDirectory,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [string]$InstalledDirectory='D:\GlassDesk',
    [switch]$IncludeBaseline,
    [switch]$IncludeGameSleep,
    [switch]$SleepOnly,
    [ValidateRange(2,120)][int]$PhaseSeconds=60
)
$ErrorActionPreference='Stop'
if($PSVersionTable.PSVersion.Major -lt 7){throw 'Use PowerShell 7 (pwsh).'}
$output=[IO.Path]::GetFullPath($OutputDirectory)
$profile=[IO.Path]::GetFullPath($ProfileDirectory)
$installed=[IO.Path]::GetFullPath($InstalledDirectory)
$candidate=[IO.Path]::GetFullPath($CandidateDirectory)
if($SleepOnly -and -not $IncludeGameSleep){throw 'SleepOnly requires IncludeGameSleep.'}
if(Test-Path -LiteralPath $output){throw 'Use a fresh product measurement directory.'}
if($output.StartsWith($installed+'\',[StringComparison]::OrdinalIgnoreCase) -or $output.StartsWith($profile+'\',[StringComparison]::OrdinalIgnoreCase)) {throw 'Output overlaps protected data.'}
if(-not(Test-Path -LiteralPath (Join-Path $profile 'settings.json'))){throw 'A copied acceptance profile is required.'}
if($profile -eq (Join-Path $installed 'Data')){throw 'Pass the acceptance input-profile copy, not live Data.'}
if(Get-CimInstance Win32_Process | Where-Object {$_.CommandLine -match '--full-host-(performance|input)' -and $_.Name -eq 'GlassDesk.LauncherAcceptance.exe'}){throw 'Wait for full-host acceptance to finish before product-only sampling.'}
function Send-Command([string]$Command) {
    $sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $session=[Diagnostics.Process]::GetCurrentProcess().SessionId
    $pipe=[IO.Pipes.NamedPipeClientStream]::new('.',"GlassDesk.Pipe.$sid.$session.GlassDesk.Host",[IO.Pipes.PipeDirection]::Out)
    try {$pipe.Connect(3000);$bytes=[Text.Encoding]::UTF8.GetBytes($Command+"`n");$pipe.Write($bytes);$pipe.Flush()}
    finally {$pipe.Dispose()}
}
$owners=@(Get-CimInstance Win32_Process | Where-Object {$_.ExecutablePath -eq (Join-Path $installed 'GlassDesk.exe')} | ForEach-Object {[Diagnostics.Process]::GetProcessById($_.ProcessId)})
$resume=$owners.Count -gt 0
$results=[Collections.Generic.List[object]]::new();$process=$null;$sampler=$null;$fixture=$null
try {
    if($resume){Send-Command 'exit';foreach($owner in $owners){if(-not $owner.WaitForExit(10000)){throw 'Original installation did not exit normally.'}}}
    $null=New-Item -ItemType Directory -Path $output
    foreach($kind in @('baseline','candidate')) {
        if($kind -eq 'baseline' -and -not $IncludeBaseline){continue}
        $stage=Join-Path $output ($kind+'-payload');$evidence=Join-Path $output $kind
        $null=New-Item -ItemType Directory -Path $stage,$evidence
        $source=if($kind -eq 'baseline'){$installed}else{Join-Path $candidate 'App'}
        foreach($item in Get-ChildItem -LiteralPath $source -Force){if($item.Name -ne 'Data'){Copy-Item -LiteralPath $item.FullName -Destination $stage -Recurse}}
        Copy-Item -LiteralPath $profile -Destination (Join-Path $stage 'Data') -Recurse
        $phase=Join-Path $evidence 'phase.txt';[IO.File]::WriteAllText($phase,'startup')
        $process=Start-Process -FilePath (Join-Path $stage 'GlassDesk.exe') -ArgumentList '--autostart' -WorkingDirectory $stage -WindowStyle Hidden -PassThru
        $sampler=Start-Job -ScriptBlock {
            param($script,$root,$phasePath,$outputPath)
            & $script -RootProcessId $root -PhaseFile $phasePath -OutputPath $outputPath -MaximumSeconds 600 -CollectGpu
        } -ArgumentList (Join-Path $PSScriptRoot 'Measure-GlassDeskProcessGroup.ps1'),$process.Id,$phase,(Join-Path $evidence 'process-group-samples.json')
        $states=@('idle','open','closed');if($IncludeGameSleep){$states+='sleeping'}
        if($SleepOnly){
            [IO.File]::WriteAllText($phase,'prepare-open')
            Send-Command 'show-launcher';Start-Sleep -Seconds 10
            Send-Command 'hide-launcher';Start-Sleep -Seconds 2
            $states=@('sleeping')
        }
        foreach($state in $states) {
            if($state -eq 'open'){Send-Command 'show-launcher'}
            if($state -eq 'closed'){Send-Command 'hide-launcher'}
            if($state -eq 'sleeping'){
                $ready=Join-Path $evidence 'fullscreen-ready.json'
                $fixture=Start-Process -FilePath (Join-Path $candidate 'Testing\GlassDesk.LauncherAcceptance.exe') -ArgumentList @('--fullscreen-fixture','--fixture-ready',('"'+$ready+'"')) -WindowStyle Hidden -PassThru
                $deadline=[Diagnostics.Stopwatch]::StartNew()
                while(-not(Test-Path -LiteralPath $ready) -and $deadline.Elapsed.TotalSeconds -lt 5){Start-Sleep -Milliseconds 100}
                if(-not(Test-Path -LiteralPath $ready)){throw 'Fullscreen fixture did not initialize.'}
                $proof=Get-Content -LiteralPath $ready -Raw | ConvertFrom-Json
                if(-not $proof.foreground){throw 'Fullscreen fixture did not obtain foreground.'}
            }
            foreach($suffix in @('-warmup','')) {
                [IO.File]::WriteAllText($phase,$state+$suffix)
                Write-Output "$kind real GlassDesk.exe: $state$suffix ($PhaseSeconds seconds)."
                Start-Sleep -Seconds $PhaseSeconds
                if($process.HasExited){throw 'Product exited during sampling.'}
            }
        }
        if($fixture){if(-not $fixture.HasExited){$null=$fixture.CloseMainWindow();if(-not $fixture.WaitForExit(3000)){$fixture.Kill();$fixture.WaitForExit(3000)|Out-Null}};$fixture.Dispose();$fixture=$null}
        Send-Command 'exit';if(-not $process.WaitForExit(10000)){throw 'Staged product did not exit normally.'}
        $null=Wait-Job $sampler;Receive-Job $sampler;Remove-Job $sampler;$sampler=$null
        $results.Add([ordered]@{kind=$kind;processId=$process.Id;exitCode=$process.ExitCode;actualProductExecutable=$true;consoleAcceptanceHost=$false})
        $process.Dispose();$process=$null
    }
} finally {
    if($fixture){if(-not $fixture.HasExited){$fixture.Kill();$fixture.WaitForExit(3000)|Out-Null};$fixture.Dispose()}
    if($process){if(-not $process.HasExited){$process.Kill($true);$process.WaitForExit(5000)|Out-Null};$process.Dispose()}
    if($sampler){Stop-Job $sampler;Remove-Job $sampler}
    foreach($owner in $owners){$owner.Dispose()}
    if($resume){
        $restored=Start-Process -FilePath (Join-Path $installed 'GlassDesk.exe') -ArgumentList '--autostart' -WindowStyle Hidden -PassThru
        $null=$restored.WaitForExit(2000);$restored.Dispose()
    }
    if(Test-Path -LiteralPath $output){[ordered]@{timestamp=[DateTimeOffset]::Now;copiedProfile=$profile;liveInstallationReplaced=$false;
        phaseSeconds=$PhaseSeconds;automaticFullscreenSleep=[bool]$IncludeGameSleep;sleepOnly=[bool]$SleepOnly;
        measurement='Actual WinExe GlassDesk.exe and its capture children. Commands use the existing production named pipe. Each stable phase follows a warmup of the same duration. A foreign foreground fullscreen fixture triggers automatic sleep; this is not a game FPS test.';results=$results.ToArray()}|
        ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $output 'product-measurement-run.json') -Encoding utf8}
}
