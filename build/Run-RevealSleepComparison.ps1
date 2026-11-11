[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$CandidateDirectory,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [string]$InstalledDirectory='D:\GlassDesk',
    [switch]$Quick,
    [switch]$SkipBaseline,
    [switch]$DockOnly
)
$ErrorActionPreference='Stop'
$candidate=[IO.Path]::GetFullPath($CandidateDirectory)
$output=[IO.Path]::GetFullPath($OutputDirectory)
$installed=[IO.Path]::GetFullPath($InstalledDirectory)
if(Test-Path -LiteralPath $output){throw 'Use a fresh comparison directory.'}
if($output.StartsWith($installed+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Output overlaps live installation.'}
function Send-Command([string]$Command){
    $sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $session=[Diagnostics.Process]::GetCurrentProcess().SessionId
    $pipe=[IO.Pipes.NamedPipeClientStream]::new('.',"GlassDesk.Pipe.$sid.$session.GlassDesk.Host",[IO.Pipes.PipeDirection]::Out)
    try{$pipe.Connect(3000);$bytes=[Text.Encoding]::UTF8.GetBytes($Command+"`n");$pipe.Write($bytes);$pipe.Flush()}
    finally{$pipe.Dispose()}
}
$owners=@(Get-CimInstance Win32_Process | Where-Object {$_.ExecutablePath -eq (Join-Path $installed 'GlassDesk.exe')} | ForEach-Object {[Diagnostics.Process]::GetProcessById($_.ProcessId)})
$resume=$owners.Count -gt 0
$test=$null;$sampler=$null
try{
    if($resume){Send-Command 'exit';foreach($owner in $owners){if(-not $owner.WaitForExit(10000)){throw 'Original application did not exit normally.'}}}
    $null=New-Item -ItemType Directory -Path $output
    $profile=Join-Path $output 'input-profile'
    Copy-Item -LiteralPath (Join-Path $installed 'Data') -Destination $profile -Recurse
    foreach($kind in @('baseline','candidate')){
        if($kind -eq 'baseline' -and $SkipBaseline){continue}
        $stage=Join-Path $output ($kind+'-payload');$evidence=Join-Path $output $kind
        $null=New-Item -ItemType Directory -Path $stage,$evidence
        foreach($item in Get-ChildItem -LiteralPath (Join-Path $candidate 'Testing') -Force){if($item.Name -ne 'Data'){Copy-Item -LiteralPath $item.FullName -Destination $stage -Recurse}}
        foreach($name in @('GlassDesk.exe','GlassDesk.runtimeconfig.json','GlassDesk.deps.json')){
            Copy-Item -LiteralPath (Join-Path $candidate ('App\'+$name)) -Destination $stage -Force
        }
        if($kind -eq 'baseline'){foreach($item in Get-ChildItem -LiteralPath $installed -Force){if($item.Name -ne 'Data'){Copy-Item -LiteralPath $item.FullName -Destination $stage -Recurse -Force}}}
        Copy-Item -LiteralPath $profile -Destination (Join-Path $stage 'Data') -Recurse
        [IO.File]::WriteAllText((Join-Path $stage '.full-host-acceptance-root'),$stage.TrimEnd('\')+'\')
        $phase=Join-Path $evidence 'phase.txt';$report=Join-Path $evidence 'full-host-performance.json'
        $arguments=@('--full-host-performance','--reveal-sleep-focused','--report',('"'+$report+'"'),'--phase-file',('"'+$phase+'"'))
        if($Quick){$arguments+='--quick'}
        if($DockOnly){$arguments+='--dock-reveal-only'}
        $test=Start-Process -FilePath (Join-Path $stage 'GlassDesk.LauncherAcceptance.exe') -ArgumentList $arguments -WorkingDirectory $stage -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $evidence 'stdout.log') -RedirectStandardError (Join-Path $evidence 'stderr.log')
        Write-Output "$kind full AppHost comparison PID $($test.Id)."
        $sampler=Start-Job -ScriptBlock {param($script,$root,$phasePath,$outputPath) & $script -RootProcessId $root -PhaseFile $phasePath -OutputPath $outputPath -MaximumSeconds 900} -ArgumentList (Join-Path $PSScriptRoot 'Measure-GlassDeskProcessGroup.ps1'),$test.Id,$phase,(Join-Path $evidence 'process-group-samples.json')
        $test.WaitForExit();$null=Wait-Job $sampler;Receive-Job $sampler;Remove-Job $sampler;$sampler=$null
        Write-Output "$kind exit code $($test.ExitCode)."
        $test.Dispose();$test=$null
    }
}finally{
    if($test){if(-not $test.HasExited){$test.Kill($true);$test.WaitForExit(5000)|Out-Null};$test.Dispose()}
    if($sampler){Stop-Job $sampler;Remove-Job $sampler}
    if($resume){
        $restored=Start-Process -FilePath (Join-Path $installed 'GlassDesk.exe') -ArgumentList '--autostart' -WindowStyle Hidden -PassThru
        $null=$restored.WaitForExit(2000);$restored.Dispose()
    }
    foreach($owner in $owners){$owner.Dispose()}
}
