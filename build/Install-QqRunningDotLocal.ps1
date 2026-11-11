param(
    [Parameter(Mandatory=$true)][string]$CandidateDll,
    [Parameter(Mandatory=$true)][string]$EvidenceDirectory
)
$ErrorActionPreference='Stop'
$install='D:\GlassDesk'
$exe=Join-Path $install 'GlassDesk.exe'
$component='GlassDesk.AppServices.dll'
$candidate=[IO.Path]::GetFullPath($CandidateDll)
$evidence=[IO.Path]::GetFullPath($EvidenceDirectory)
$workspace=[IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
if(-not $evidence.StartsWith($workspace+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Evidence must stay in checkout.' }
if(Test-Path -LiteralPath $evidence) { throw 'Fresh evidence directory required.' }
$target=Join-Path $install $component
if([Reflection.AssemblyName]::GetAssemblyName($target).FullName -ne [Reflection.AssemblyName]::GetAssemblyName($candidate).FullName) { throw 'Assembly identity mismatch.' }
if([Diagnostics.FileVersionInfo]::GetVersionInfo($target).FileVersion -ne [Diagnostics.FileVersionInfo]::GetVersionInfo($candidate).FileVersion) { throw 'File version mismatch.' }
$hosts=@(Get-CimInstance Win32_Process -Filter "Name='GlassDesk.exe'" | Where-Object { $_.ExecutablePath -eq $exe -and $_.CommandLine -notmatch '--glass-backdrop-worker' })
if($hosts.Count -ne 1) { throw 'Expected one installed host.' }
$originalHost=$hosts[0]
$backup=Join-Path ('D:\GlassDesk-Backups\before-qq-running-dot-'+(Get-Date -Format yyyyMMdd-HHmmss)) 'Installation'
if(Test-Path -LiteralPath $backup) { throw 'Fresh backup required.' }
[IO.Directory]::CreateDirectory($evidence)|Out-Null
function HashFiles([string]$root,[switch]$StableOnly) {
    $result=[ordered]@{}
    foreach($file in Get-ChildItem -LiteralPath $root -File -Recurse -Force | Where-Object Name -notlike '.glassdesk-data-*.lease' | Sort-Object FullName) {
        $relative=[IO.Path]::GetRelativePath($root,$file.FullName)
        if($StableOnly -and $relative -match '^Data\\(logs|runtime|recovery)\\') { continue }
        $result[$relative]=(Get-FileHash -LiteralPath $file.FullName).Hash
    }
    return $result
}
function StartupState {
    $values=[ordered]@{}
    foreach($key in @('HKCU:\Software\Microsoft\Windows\CurrentVersion\Run','HKCU:\Software\Microsoft\Windows\CurrentVersion\RunOnce')) {
        $item=Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue
        foreach($property in $item.PSObject.Properties|Where-Object Name -like '*GlassDesk*'|Sort-Object Name) { $values[$key+'\'+$property.Name]=[string]$property.Value }
    }
    return ($values|ConvertTo-Json -Compress)
}
function StopHost([int]$hostId) {
    & $exe --exit
    for($attempt=0;$attempt -lt 100;$attempt++) {
        if(-not (Get-Process -Id $hostId -ErrorAction SilentlyContinue)) { break }
        Start-Sleep -Milliseconds 100
    }
    if(Get-Process -Id $hostId -ErrorAction SilentlyContinue) { throw 'Host did not exit normally.' }
    Get-CimInstance Win32_Process -Filter "Name='GlassDesk.exe'" | Where-Object {
        $_.ExecutablePath -eq $exe -and $_.CommandLine.Contains("--glass-backdrop-worker $hostId")
    } | ForEach-Object { Stop-Process -Id $_.ProcessId }
}
$receipt=[ordered]@{kind='QQ running-dot local component fix';startedUtc=[DateTimeOffset]::UtcNow;installation=$install;backup=$backup;component=$component;published=$false;success=$false}
$replaced=$false; $stopped=$false; $app=$null
try {
    $startupBefore=StartupState
    StopHost $originalHost.ProcessId; $stopped=$true
    $before=HashFiles $install
    [IO.Directory]::CreateDirectory($backup)|Out-Null
    Get-ChildItem -LiteralPath $install -Force | Copy-Item -Destination $backup -Recurse
    $backupHashes=HashFiles $backup
    if($before.Count -ne $backupHashes.Count) { throw 'Backup count mismatch.' }
    foreach($name in $before.Keys) { if($before[$name] -ne $backupHashes[$name]) { throw "Backup mismatch: $name" } }
    $before|ConvertTo-Json -Depth 3|Set-Content -LiteralPath (Join-Path $evidence 'before-manifest.json') -Encoding utf8
    $receipt['backupVerifiedFiles']=$before.Count
    $replaced=$true
    Copy-Item -LiteralPath $candidate -Destination $target -Force
    $newHash=(Get-FileHash -LiteralPath $candidate).Hash
    if((Get-FileHash -LiteralPath $target).Hash -ne $newHash) { throw 'Installed component hash mismatch.' }
    $receipt['oldSha256']=$before[$component];$receipt['newSha256']=$newHash
    $info=[Diagnostics.ProcessStartInfo]::new($exe)
    $info.UseShellExecute=$false; $info.CreateNoWindow=$true
    $info.RedirectStandardOutput=$true; $info.RedirectStandardError=$true
    foreach($argument in @('--selftest','--acceptance-data-root',(Join-Path $evidence 'selftest-data'),'--acceptance-report',(Join-Path $evidence 'selftest-report.txt'))) { $info.ArgumentList.Add($argument) }
    $test=[Diagnostics.Process]::Start($info)
    $stdout=$test.StandardOutput.ReadToEndAsync(); $stderr=$test.StandardError.ReadToEndAsync()
    if(-not $test.WaitForExit(30000)) { $test.Kill(); throw 'Installed selftest timed out.' }
    [IO.File]::WriteAllText((Join-Path $evidence 'selftest-stdout.txt'),$stdout.GetAwaiter().GetResult())
    [IO.File]::WriteAllText((Join-Path $evidence 'selftest-stderr.txt'),$stderr.GetAwaiter().GetResult())
    $receipt['selftestExitCode']=$test.ExitCode
    if($test.ExitCode -ne 0) { throw 'Installed selftest failed.' }
    $app=Start-Process -FilePath $exe -ArgumentList '--autostart' -WorkingDirectory $install -WindowStyle Hidden -PassThru
    Start-Sleep -Seconds 4
    if($app.HasExited) { throw 'Installed host did not remain running.' }
    $app.Refresh()
    $loaded=@($app.Modules|Where-Object ModuleName -eq $component|ForEach-Object FileName)
    if($loaded.Count -ne 1 -or $loaded[0] -ne $target) { throw 'New component not loaded by installed host.' }
    $after=HashFiles $install -StableOnly
    $stableCount=0;$otherCount=0
    foreach($name in $before.Keys) {
        if($name -eq $component -or $name -like '*.lease' -or $name -match '^Data\\(logs|runtime|recovery)\\') { continue }
        if(-not $after.Contains($name) -or $before[$name] -ne $after[$name]) { throw "Unrelated file changed: $name" }
        if($name.StartsWith('Data\')) { $stableCount++ } else { $otherCount++ }
    }
    if((StartupState) -ne $startupBefore) { throw 'Login startup registry changed.' }
    $receipt['stableDataFilesUnchanged']=$stableCount;$receipt['otherProgramFilesUnchanged']=$otherCount
    $receipt['startupRegistryUnchanged']=$true;$receipt['loadedModule']=$loaded[0];$receipt['processId']=$app.Id
    $receipt['success']=$true
}
catch {
    $failure=$_
    [IO.File]::WriteAllText((Join-Path $evidence 'failure.txt'),$failure.ToString())
    if($app -and -not $app.HasExited) { StopHost $app.Id }
    if($replaced) {
        Copy-Item -LiteralPath (Join-Path $backup $component) -Destination $target -Force
        $receipt['rolledBack']=$true
    }
    if($stopped) { Start-Process -FilePath $exe -ArgumentList '--autostart' -WindowStyle Hidden }
    throw $failure
}
finally {
    $receipt['endedUtc']=[DateTimeOffset]::UtcNow
    $receipt|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $evidence 'local-install.json') -Encoding utf8
}
$receipt|ConvertTo-Json -Depth 6
