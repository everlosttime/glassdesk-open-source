[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [ValidateRange(1000,30000)][int]$ObserveMilliseconds=3000
)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $output){throw 'Use a fresh evidence directory.'}
$probeDirectory=Join-Path $repo 'artifacts\native-rendering\probe'
$executable=Join-Path $probeDirectory 'GlassDesk.NativeRenderingProbe.exe'
$null=New-Item -ItemType Directory -Path $output
$hashes=@(foreach($name in @('GlassDesk.NativeRenderingProbe.exe','GlassDesk.NativeRenderingProbe.dll','GlassDesk.Rendering.Native.dll','GlassDesk.Rendering.dll')){
    [pscustomobject]@{name=$name;sha256=(Get-FileHash -LiteralPath (Join-Path $probeDirectory $name) -Algorithm SHA256).Hash}
})
$runs=[Collections.Generic.List[object]]::new()
$modes=@('independent','shared','shared','independent','independent','shared')
for($run=0;$run -lt $modes.Count;$run++){
    $mode=$modes[$run];$directory=Join-Path $output (('{0:D2}-' -f $run)+$mode)
    $null=New-Item -ItemType Directory -Path $directory
    foreach($item in $hashes){if((Get-FileHash -LiteralPath (Join-Path $probeDirectory $item.name) -Algorithm SHA256).Hash -ne $item.sha256){throw 'Payload changed during A/B.'}}
    $child=Start-Process -FilePath $executable -ArgumentList @('--direct-device-mode',$mode,'--direct-device-observe-ms',$ObserveMilliseconds,'--output',('"'+$directory+'"')) -WindowStyle Hidden -PassThru
    $identity=[pscustomobject]@{processId=$child.Id;startUtc=$child.StartTime.ToUniversalTime().ToString('O');executable=$executable}
    $gpu=[Collections.Generic.List[object]]::new();$unavailable=$null;$watch=[Diagnostics.Stopwatch]::StartNew()
    while(!$child.HasExited){
        if($watch.Elapsed.TotalSeconds -gt 150){throw "Owned probe timed out; inspect PID $($child.Id)."}
        if(!$unavailable){
            try{
                $memory=@(Get-CimInstance Win32_PerfFormattedData_GPUPerformanceCounters_GPUProcessMemory -ErrorAction Stop | Where-Object {$_.Name -match ('^pid_'+$child.Id+'_')} | Select-Object Name,DedicatedUsage,SharedUsage,TotalCommitted)
                $phasePath=Join-Path $directory 'phase.txt';$phase=if(Test-Path -LiteralPath $phasePath){[IO.File]::ReadAllText($phasePath)}else{'startup'}
                $gpu.Add([pscustomobject]@{elapsedSeconds=$watch.Elapsed.TotalSeconds;phase=$phase;members=$memory})
            }catch{$unavailable=$_.Exception.Message}
        }
        Start-Sleep -Milliseconds 250
    }
    $child.WaitForExit()
    [ordered]@{identity=$identity;source='Win32_PerfFormattedData_GPUPerformanceCounters_GPUProcessMemory';unavailable=$unavailable;samples=$gpu.ToArray();
        limitation='Per-owned-process GPU counters are separate from CPU commit and working set. Empty counter instances mean unavailable, never zero. Sampling may miss peaks.'} |
        ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $directory 'gpu-memory.json') -Encoding utf8
    if($child.ExitCode -ne 0){throw "Owned probe failed: $directory"}
    $report=Get-Content -LiteralPath (Join-Path $directory 'direct-device.json') -Raw | ConvertFrom-Json
    $warm=@($report.samples | Where-Object phase -eq warm)
    $expected=if($mode -eq 'shared'){1}else{4}
    if(@($warm | Where-Object {$_.liveDevices -ne $expected -or $_.livePresenters -ne 4}).Count){throw 'Unexpected graphics ownership.'}
    $runs.Add([pscustomobject]@{mode=$mode;directory=$directory;identity=$identity;sourceHash=$report.sourceHash;iconHash=$report.iconHash;
        warmMedianPrivateCommitBytes=@($warm.privateCommitBytes | Sort-Object)[[int][Math]::Floor($warm.Count/2)];
        warmMedianWorkingSetBytes=@($warm.workingSetBytes | Sort-Object)[[int][Math]::Floor($warm.Count/2)];peakPrivateCommitBytes=$report.peakPrivateCommitBytes})
    $child.Dispose()
}
if(@($runs.sourceHash | Select-Object -Unique).Count -ne 1 -or @($runs.iconHash | Select-Object -Unique).Count -ne 1){throw 'Content changed during A/B.'}
[ordered]@{completedUtc=[DateTimeOffset]::UtcNow.ToString('O');payload=$hashes;runs=$runs.ToArray();
    wholeProductAcceptance=$false;displayedFpsMeasured=$false;forcedGc=$false;workingSetTrimmed=$false;
    powerScheme=(& powercfg /getactivescheme | Out-String).Trim();
    limitation='Same-build component A/B only: four owned WPF HWNDs and fixed procedural content. No product host, capture worker, Data, Launcher lifecycle, input or refresh acceptance.'} |
    ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'comparison.json') -Encoding utf8
$runs | Format-List
