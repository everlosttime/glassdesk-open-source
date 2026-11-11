[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$ProfileDirectory,
    [Parameter(Mandatory=$true)][string]$CaptureExecutable,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [string]$DotnetPath='D:\dotnet10\dotnet.exe',
    [switch]$DiagnosticGcAfterClose,
    [switch]$DiagnosticDump
)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$profile=[IO.Path]::GetFullPath($ProfileDirectory).TrimEnd('\')
$output=[IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
$capture=[IO.Path]::GetFullPath($CaptureExecutable)
if(Test-Path -LiteralPath $output){throw 'Use a fresh output directory.'}
if($profile -eq $output -or $profile.StartsWith($output+'\',[StringComparison]::OrdinalIgnoreCase) -or $output.StartsWith($profile+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Input and output must not overlap.'}
if(!(Test-Path -LiteralPath (Join-Path $profile settings.json)) -or !(Test-Path -LiteralPath $capture)){throw 'A frozen profile and product capture executable are required.'}
$fixture=Join-Path $repo 'tests\GlassDesk.LauncherAcceptance\bin\Release\net10.0-windows10.0.26100.0\GlassDesk.LauncherAcceptance.dll'
if(!(Test-Path -LiteralPath $fixture)){throw 'Build LauncherAcceptance Release first.'}
$dumpTool=Join-Path $repo 'artifacts\native-rendering\profiling-tools\dotnet-dump.exe'
if($DiagnosticDump -and (!$DiagnosticGcAfterClose -or !(Test-Path -LiteralPath $dumpTool))){throw 'DiagnosticDump requires DiagnosticGcAfterClose and the local dotnet-dump tool.'}
$manifest=@(Get-ChildItem -LiteralPath $profile -Recurse -File | ForEach-Object {
    [pscustomobject]@{path=$_.FullName.Substring($profile.Length+1);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
})
$null=New-Item -ItemType Directory -Path $output
$data=Join-Path $output Data
Copy-Item -LiteralPath $profile -Destination $data -Recurse
foreach($entry in $manifest){if((Get-FileHash -LiteralPath (Join-Path $data $entry.path)).Hash -ne $entry.sha256){throw 'Copied profile did not match its source.'}}
[IO.File]::WriteAllText((Join-Path $data '.launcher-resource-test-root'),$data)
$manifest | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $output profile-manifest.json)
$start=[Diagnostics.ProcessStartInfo]::new($DotnetPath)
$start.UseShellExecute=$false;$start.WindowStyle='Hidden';$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
foreach($argument in @($fixture,'--launcher-resource-lifecycle','--data-dir',$data,'--report',(Join-Path $output lifecycle.json),'--capture-exe',$capture)){$start.ArgumentList.Add($argument)}
if($DiagnosticGcAfterClose){$start.ArgumentList.Add('--diagnostic-gc-after-close')}
if($DiagnosticDump){$start.ArgumentList.Add('--hold-for-diagnostic-dump')}
$start.Environment['GLASSDESK_DOCK_RENDERER']='legacy'
$start.Environment['DOTNET_GCConserveMemory']='5'
$start.Environment.Remove('GLASSDESK_NATIVE_RENDER_DLL')|Out-Null
$process=[Diagnostics.Process]::Start($start)
$stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
[ordered]@{pid=$process.Id;sourceHead=(& git -C $repo rev-parse HEAD).Trim();fixtureSha256=(Get-FileHash -LiteralPath $fixture).Hash;
    captureSha256=(Get-FileHash -LiteralPath $capture).Hash;conserveMemory=5;componentObservationOnly=$true;
    diagnosticGcAfterClose=[bool]$DiagnosticGcAfterClose} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output process.json)
$seen=@{};$clock=[Diagnostics.Stopwatch]::StartNew();$dumpCollected=$false
while(!$process.HasExited){
    if($clock.Elapsed.TotalSeconds -gt 120){throw "Observer remains live: PID $($process.Id). Preserve this handle and inspect it; do not restart."}
    $phaseFile=Join-Path $output phase.json
    if(Test-Path -LiteralPath $phaseFile){
        try{$phase=Get-Content -LiteralPath $phaseFile -Raw|ConvertFrom-Json}catch{$phase=$null}
        if($phase -and !$seen.ContainsKey($phase.phase)){
            if($phase.pid -ne $process.Id -or $phase.phase -notmatch '^[a-z-]+$'){throw 'Invalid phase identity.'}
            $null=& (Join-Path $PSScriptRoot Measure-ProcessCommitLayout.ps1) -ProcessId $process.Id -OutputPath (Join-Path $output ($phase.phase+'-layout.json'))
            $seen[$phase.phase]=$true
        }
    }
    if($DiagnosticDump -and !$dumpCollected -and (Test-Path -LiteralPath (Join-Path $output dump.ready))){
        try{
            & $dumpTool collect -p $process.Id --type Heap -o (Join-Path $output released-graph.dmp) *> (Join-Path $output dump-collection.txt)
            if($LASTEXITCODE -ne 0){throw 'The owned diagnostic dump failed.'}
            $dumpCollected=$true
        }finally{[IO.File]::WriteAllText((Join-Path $output dump.done),'done')}
    }
    Start-Sleep -Milliseconds 120
}
$process.WaitForExit()
[IO.File]::WriteAllText((Join-Path $output stdout.txt),$stdout.GetAwaiter().GetResult())
[IO.File]::WriteAllText((Join-Path $output stderr.txt),$stderr.GetAwaiter().GetResult())
foreach($entry in $manifest){if((Get-FileHash -LiteralPath (Join-Path $profile $entry.path)).Hash -ne $entry.sha256){throw 'Source profile changed during observation.'}}
if($process.ExitCode -ne 0){throw "Invalid lifecycle observation; read lifecycle.json and stderr.txt. Exit $($process.ExitCode)."}
$report=Get-Content -LiteralPath (Join-Path $output lifecycle.json) -Raw | ConvertFrom-Json
$report.observations | Select-Object phase,PrivateMemorySize64,gcCommittedBytes,IsVisible,Width,Height
