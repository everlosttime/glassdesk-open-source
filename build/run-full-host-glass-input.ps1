[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$OutputDirectory,
      [string]$InstalledDirectory='D:\GlassDesk',
      [string]$PayloadDirectory)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$caseRoot=[IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $caseRoot) { throw 'Use a fresh acceptance directory.' }
$null=New-Item -ItemType Directory -Path $caseRoot
Copy-Item -Path (Join-Path $repo 'tests\GlassDesk.LauncherAcceptance\bin\Release\net10.0-windows10.0.26100.0\*') -Destination $caseRoot -Recurse
if (-not $PayloadDirectory) { $PayloadDirectory=Join-Path $repo 'src\GlassDesk.App\bin\Release\net10.0-windows10.0.26100.0' }
Copy-Item -Path (Join-Path $PayloadDirectory 'GlassDesk*.dll') -Destination $caseRoot -Force
Copy-Item -LiteralPath (Join-Path $PayloadDirectory 'update-source.json') -Destination $caseRoot
Set-Content -LiteralPath (Join-Path $caseRoot '.full-host-acceptance-root') -Value ($caseRoot+'\') -Encoding utf8
$installedExe=Join-Path $InstalledDirectory 'GlassDesk.exe'
$stopTask=Start-Process -FilePath $installedExe -ArgumentList '--exit' -WindowStyle Hidden -PassThru
$null=$stopTask.WaitForExit(10000)
for ($i=0;$i -lt 40;$i++) {
    if (-not (Get-Process -Name GlassDesk -ErrorAction SilentlyContinue)) { break }
    Start-Sleep -Milliseconds 100
}
if (Get-Process -Name GlassDesk -ErrorAction SilentlyContinue) { throw 'Installed instance did not stop.' }
try {
    Copy-Item -LiteralPath (Join-Path $InstalledDirectory 'Data') -Destination $caseRoot -Recurse
    $procInfo=[Diagnostics.ProcessStartInfo]::new((Join-Path $caseRoot 'GlassDesk.LauncherAcceptance.exe'))
    $procInfo.UseShellExecute=$false; $procInfo.CreateNoWindow=$true
    $procInfo.RedirectStandardError=$true; $procInfo.RedirectStandardOutput=$true
    $procInfo.Environment['DOTNET_ROOT']='D:\dotnet10'; $procInfo.Environment['DOTNET_ROOT_X64']='D:\dotnet10'
    foreach ($argValue in @('--full-host-input','--report',(Join-Path $caseRoot 'report.json'))) { $procInfo.ArgumentList.Add($argValue) }
    $caseProcess=[Diagnostics.Process]::Start($procInfo)
    $errorRead=$caseProcess.StandardError.ReadToEndAsync(); $outputRead=$caseProcess.StandardOutput.ReadToEndAsync()
    if (-not $caseProcess.WaitForExit(60000)) { $caseProcess.Kill(); throw 'Full-host acceptance timed out.' }
    $errorRead.Result | Set-Content (Join-Path $caseRoot 'stderr.log')
    $outputRead.Result | Set-Content (Join-Path $caseRoot 'stdout.log')
    Write-Output ('Exit='+$caseProcess.ExitCode)
    if (Test-Path (Join-Path $caseRoot 'backdrop-diagnostics.json')) {
        $diagnostic=Get-Content (Join-Path $caseRoot 'backdrop-diagnostics.json') -Raw | ConvertFrom-Json
        $diagnostic.capture.Counts | Where-Object Stage -in @('Recovery','RecoveryTrigger','Rendering') | ConvertTo-Json
    }
    if ($caseProcess.ExitCode -ne 0) { throw "Full-host acceptance failed. Report: $caseRoot" }
} finally { Start-Process -FilePath $installedExe -ArgumentList '--autostart' -WindowStyle Hidden }
