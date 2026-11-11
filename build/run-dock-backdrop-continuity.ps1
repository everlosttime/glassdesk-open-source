[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$PayloadDirectory,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [string]$RunnerDirectory,
    [string]$InstalledDirectory='D:\GlassDesk',
    [int]$WindowCycles=32,
    [switch]$StressOnly,
    [switch]$WakeOnly
)
$ErrorActionPreference='Stop'
$caseRepo=Split-Path -Parent $PSScriptRoot
$caseOutput=[IO.Path]::GetFullPath($OutputDirectory)
$casePayload=(Resolve-Path -LiteralPath $PayloadDirectory).Path
$caseInstalled=(Resolve-Path -LiteralPath $InstalledDirectory).Path
if(Test-Path -LiteralPath $caseOutput) {throw 'Use a fresh acceptance output directory.'}
$caseRunner=if($RunnerDirectory) {(Resolve-Path -LiteralPath $RunnerDirectory).Path} else {Join-Path $caseRepo 'tests\GlassDesk.LauncherAcceptance\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish'}
$caseProbe=Join-Path $caseRepo 'tests\GlassDesk.CompatibilityProbe\bin\Release\net10.0-windows10.0.26100.0\GlassCaptureProbe.exe'
if(!(Test-Path (Join-Path $caseRunner 'GlassDesk.LauncherAcceptance.exe')) -or !(Test-Path $caseProbe)) {throw 'Build Release acceptance and compatibility runners first.'}
$caseRuntime=Get-Content -LiteralPath (Join-Path $caseRunner 'GlassDesk.LauncherAcceptance.runtimeconfig.json') -Raw | ConvertFrom-Json
if(!$caseRuntime.runtimeOptions.includedFrameworks) {throw 'Publish the acceptance runner as self-contained before staging the product runtime.'}
New-Item -ItemType Directory -Path $caseOutput | Out-Null
$caseExe=Join-Path $caseInstalled 'GlassDesk.exe'
$caseComponents=@('GlassDesk.exe','GlassDesk.dll','GlassDesk.UI.dll','GlassDesk.Dock.dll','GlassDesk.Launcher.dll','GlassDesk.Core.dll','GlassDesk.Platform.dll','GlassDesk.Settings.dll','GlassDesk.AppServices.dll')
$caseBefore=@{}
foreach($caseName in $caseComponents) {$caseBefore[$caseName]=(Get-FileHash -LiteralPath (Join-Path $caseInstalled $caseName)).Hash}
$caseResults=[Collections.Generic.List[object]]::new()
$caseWasRunning=@(Get-CimInstance Win32_Process -Filter "Name='GlassDesk.exe'" | Where-Object {$_.ExecutablePath -eq $caseExe -and $_.CommandLine -notmatch '--glass-backdrop-worker'}).Count -gt 0
$caseDataBefore=@{}
function New-CaseRoot([string]$CaseName) {
    $caseRoot=Join-Path $caseOutput $CaseName
    New-Item -ItemType Directory -Path $caseRoot | Out-Null
    Get-ChildItem -LiteralPath $caseRunner -Force | ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination $caseRoot -Recurse}
    Get-ChildItem -LiteralPath $casePayload -Force | Where-Object {$_.Name -ne 'Data'} | ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination $caseRoot -Recurse -Force}
    Copy-Item -LiteralPath (Join-Path $caseInstalled 'Data') -Destination $caseRoot -Recurse
    Set-Content -LiteralPath (Join-Path $caseRoot '.full-host-acceptance-root') -Value ($caseRoot.TrimEnd('\')+'\') -Encoding utf8
    return $caseRoot
}
function Invoke-Case([string]$CaseName,[string[]]$CaseArguments,[int]$MaximumSeconds=300) {
    $caseRoot=New-CaseRoot $CaseName
    $caseInfo=[Diagnostics.ProcessStartInfo]::new((Join-Path $caseRoot 'GlassDesk.LauncherAcceptance.exe'))
    $caseInfo.UseShellExecute=$false; $caseInfo.CreateNoWindow=$true; $caseInfo.WindowStyle='Hidden'
    $caseInfo.RedirectStandardOutput=$true; $caseInfo.RedirectStandardError=$true
    $caseInfo.Environment['DOTNET_ROOT']='D:\dotnet10'; $caseInfo.Environment['DOTNET_ROOT_X64']='D:\dotnet10'
    foreach($caseArgument in $CaseArguments) {$caseInfo.ArgumentList.Add($caseArgument)}
    $caseInfo.ArgumentList.Add('--report'); $caseInfo.ArgumentList.Add((Join-Path $caseRoot 'report.json'))
    $caseProcess=[Diagnostics.Process]::Start($caseInfo)
    $caseStdout=$caseProcess.StandardOutput.ReadToEndAsync(); $caseStderr=$caseProcess.StandardError.ReadToEndAsync()
    $caseClock=[Diagnostics.Stopwatch]::StartNew()
    $caseSamples=[Collections.Generic.List[object]]::new()
    while(!$caseProcess.WaitForExit(1000)) {
        if($caseClock.Elapsed.TotalSeconds -gt $MaximumSeconds) {$caseProcess.Kill(); throw "Acceptance exceeded deadline: $CaseName"}
        $casePids=@($caseProcess.Id)
        $casePids+=@(Get-CimInstance Win32_Process -Filter "Name='GlassDesk.exe'" | Where-Object {$_.CommandLine -match ('--glass-backdrop-worker '+$caseProcess.Id+'(?:\s|$)')} | ForEach-Object {[int]$_.ProcessId})
        $caseWorkingSet=0L; $casePrivate=0L; $caseCpu=0.0
        foreach($casePid in $casePids) {
            try {$caseMeasured=Get-Process -Id $casePid -ErrorAction Stop; $caseWorkingSet+=$caseMeasured.WorkingSet64; $casePrivate+=$caseMeasured.PrivateMemorySize64; $caseCpu+=$caseMeasured.TotalProcessorTime.TotalSeconds} catch {}
        }
        $caseSamples.Add([ordered]@{elapsedSeconds=$caseClock.Elapsed.TotalSeconds;processIds=$casePids;workingSetBytes=$caseWorkingSet;privateBytes=$casePrivate;cpuSeconds=$caseCpu})
    }
    $caseStdout.Result | Set-Content -LiteralPath (Join-Path $caseRoot 'stdout.log')
    $caseStderr.Result | Set-Content -LiteralPath (Join-Path $caseRoot 'stderr.log')
    $caseSamples | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $caseRoot 'process-group-samples.json') -Encoding utf8
    $caseExit=$caseProcess.ExitCode; $caseProcess.Dispose()
    $caseResults.Add([ordered]@{name=$CaseName;exitCode=$caseExit;seconds=$caseClock.Elapsed.TotalSeconds;report=(Join-Path $caseRoot 'report.json')})
    Write-Output "$CaseName exit=$caseExit seconds=$([Math]::Round($caseClock.Elapsed.TotalSeconds,1))"
}
try {
    if($caseWasRunning) {
        $caseStop=Start-Process -FilePath $caseExe -ArgumentList '--exit' -WindowStyle Hidden -PassThru
        if(!$caseStop.WaitForExit(10000)) {throw 'Exit command did not finish.'}
        $caseStop.Dispose()
        $caseStopClock=[Diagnostics.Stopwatch]::StartNew()
        while(@(Get-CimInstance Win32_Process -Filter "Name='GlassDesk.exe'" | Where-Object {$_.ExecutablePath -eq $caseExe}).Count -gt 0) {
            if($caseStopClock.Elapsed.TotalSeconds -gt 20) {throw 'Installed instance did not stop gracefully.'}
            Start-Sleep -Milliseconds 200
        }
    }
    Get-ChildItem -LiteralPath (Join-Path $caseInstalled 'Data') -File -Recurse | ForEach-Object {
        $caseRelative=[IO.Path]::GetRelativePath((Join-Path $caseInstalled 'Data'),$_.FullName)
        $caseDataBefore[$caseRelative]=(Get-FileHash -LiteralPath $_.FullName).Hash
    }
    if (!$WakeOnly) { Invoke-Case 'Stress' @('--desktop-icon-continuity','--fixture-exe',$caseProbe,'--window-cycles',$WindowCycles.ToString()) 420 }
    if(!$StressOnly) {
        Invoke-Case 'Wake' @('--full-host-performance','--dock-reveal-only','--dock-reveal-long','--dock-reveal-repeat','--quick') 240
        if (!$WakeOnly) { Invoke-Case 'Input' @('--full-host-input') 120 }
    }
} finally {
    $caseProgramPreserved=$true
    foreach($caseName in $caseComponents) {if((Get-FileHash -LiteralPath (Join-Path $caseInstalled $caseName)).Hash -ne $caseBefore[$caseName]) {$caseProgramPreserved=$false}}
    $caseDataChanged=@()
    foreach($caseRelative in $caseDataBefore.Keys) {if((Get-FileHash -LiteralPath (Join-Path (Join-Path $caseInstalled 'Data') $caseRelative)).Hash -ne $caseDataBefore[$caseRelative]) {$caseDataChanged+=$caseRelative}}
    [ordered]@{timestamp=[DateTimeOffset]::Now;programPreserved=$caseProgramPreserved;dataFilesChecked=$caseDataBefore.Count;dataChanged=$caseDataChanged;results=$caseResults} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $caseOutput 'summary.json') -Encoding utf8
    if($caseWasRunning) {Start-Process -FilePath $caseExe -ArgumentList '--autostart' -WindowStyle Hidden}
}
if(!$caseProgramPreserved -or $caseDataChanged.Count -gt 0 -or @($caseResults | Where-Object {$_.exitCode -ne 0}).Count -gt 0) {throw "Acceptance failed; see $caseOutput"}
