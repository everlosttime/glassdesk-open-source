[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [string]$DotnetPath = 'D:\dotnet10\dotnet.exe',
    [string]$Iteration = 'friend-25h2-performance-r2'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$destination = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $destination) { throw 'Use a fresh candidate directory.' }
$null = New-Item -ItemType Directory -Path $destination
foreach ($entry in @(
    @{ project='src/GlassDesk.App/GlassDesk.App.csproj'; folder='App' },
    @{ project='tests/GlassDesk.LauncherAcceptance/GlassDesk.LauncherAcceptance.csproj'; folder='Testing' }
)) {
    & $DotnetPath publish (Join-Path $repo $entry.project) -c Release -r win-x64 --self-contained true `
        '-p:PublishReadyToRun=true' '-p:InformationalVersion=1.2.0' '-p:IncludeSourceRevisionInInformationalVersion=false' `
        -o (Join-Path $destination $entry.folder) *> (Join-Path $destination ($entry.folder+'-publish.log'))
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $($entry.folder)" }
}
Copy-Item -LiteralPath (Join-Path $repo 'docs/third-party') -Destination (Join-Path $destination 'licenses') -Recurse
$commands = @{
    '启动独立验收.cmd' = '@echo off
cd /d "%~dp0"
if exist "Reports\manual\stop-manual" del "Reports\manual\stop-manual"
"Testing\GlassDesk.LauncherAcceptance.exe" --ios-manual --data-dir "Testing\Data-manual" --report "Reports\manual\report.json" --isolated-worker "%~dp0App\GlassDesk.exe"
pause
'
    '结束独立验收.cmd' = '@echo off
cd /d "%~dp0"
if not exist "Reports\manual" mkdir "Reports\manual"
type nul > "Reports\manual\stop-manual"
'
    '采集失败验收.cmd' = '@echo off
cd /d "%~dp0"
"Testing\GlassDesk.LauncherAcceptance.exe" --expect-basic-background --data-dir "Testing\Data-failure" --report "Reports\failed-capture\report.json" --isolated-worker "%~dp0App\intentionally-missing-worker.exe"
pause
'
    '背景诊断.cmd' = '@echo off
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "collect-environment.ps1" -OutputPath "Reports\diagnostics\environment.json"
"Testing\GlassDesk.LauncherAcceptance.exe" --desktop-backdrop --sample-seconds 30 --data-dir "Testing\Data-diagnostic" --report "Reports\diagnostics\report.json" --isolated-worker "%~dp0App\GlassDesk.exe"
pause
'
    '开关翻页压力验收.cmd' = '@echo off
cd /d "%~dp0"
"Testing\GlassDesk.LauncherAcceptance.exe" --compatibility-stress --data-dir "Testing\Data-stress" --report "Reports\stress\report.json" --isolated-worker "%~dp0App\GlassDesk.exe"
pause
'
    '快速拖动翻页复测.cmd' = '@echo off
cd /d "%~dp0"
"Testing\GlassDesk.LauncherAcceptance.exe" --rapid-interaction --data-dir "Testing\Data-rapid" --report "Reports\rapid\report.json" --isolated-worker "%~dp0App\GlassDesk.exe"
pause
'
    '大范围拖动复测.cmd' = '@echo off
cd /d "%~dp0"
"Testing\GlassDesk.LauncherAcceptance.exe" --wide-drag --data-dir "Testing\Data-wide" --report "Reports\wide\report.json" --isolated-worker "%~dp0App\GlassDesk.exe"
pause
'
    '真实鼠标大范围拖动复测.cmd' = '@echo off
cd /d "%~dp0"
echo Automatic mouse test. Do not move the mouse until the test finishes.
"Testing\GlassDesk.LauncherAcceptance.exe" --wide-drag-input --data-dir "Testing\Data-wide-input-%RANDOM%-%RANDOM%" --report "Reports\wide-input\report.json" --isolated-worker "%~dp0App\GlassDesk.exe"
pause
'
    '不规则快速拖动复测.cmd' = '@echo off
cd /d "%~dp0"
echo Automatic irregular mouse test. Do not move the mouse until the test finishes.
"Testing\GlassDesk.LauncherAcceptance.exe" --irregular-drag-input --data-dir "Testing\Data-irregular-%RANDOM%-%RANDOM%" --report "Reports\irregular\report.json" --isolated-worker "%~dp0App\GlassDesk.exe"
pause
'
    '跟手帧调度检查.cmd' = '@echo off
cd /d "%~dp0"
"Testing\GlassDesk.LauncherAcceptance.exe" --drag-cadence --data-dir "Testing\Data-cadence" --report "Reports\cadence\report.json" --isolated-worker "%~dp0App\GlassDesk.exe"
pause
'
    '持续快速乱拖复测.cmd' = '@echo off
cd /d "%~dp0"
echo Automatic sustained mouse test. Do not move the mouse until the test finishes.
"Testing\GlassDesk.LauncherAcceptance.exe" --sustained-drag-input --data-dir "Testing\Data-sustained-%RANDOM%-%RANDOM%" --report "Reports\sustained\report.json" --isolated-worker "%~dp0App\GlassDesk.exe"
pause
'
    '整页让位负载检查.cmd' = '@echo off
cd /d "%~dp0"
"Testing\GlassDesk.LauncherAcceptance.exe" --drag-motion-load --data-dir "Testing\Data-motion-%RANDOM%-%RANDOM%" --report "Reports\motion\report.json" --isolated-worker "%~dp0App\GlassDesk.exe"
pause
'
}
foreach ($entry in $commands.GetEnumerator()) {
    [IO.File]::WriteAllText((Join-Path $destination $entry.Key), $entry.Value.Replace("`n","`r`n"), [Text.Encoding]::ASCII)
}
Copy-Item -LiteralPath (Join-Path $repo 'docs/win11-stage-a-testing.md') -Destination (Join-Path $destination '使用说明.md')
Copy-Item -LiteralPath (Join-Path $repo 'build/collect-compatibility-environment.ps1') -Destination (Join-Path $destination 'collect-environment.ps1')
foreach ($folder in @('App','Testing')) {
    if (Test-Path -LiteralPath (Join-Path $destination "$folder/Data")) { throw 'Shipped Data must be absent.' }
    foreach ($component in @('GlassDesk.UI.dll','GlassDesk.Launcher.dll','GlassDesk.Platform.dll','GlassDesk.Core.dll','GlassDesk.AppServices.dll','GlassDesk.Settings.dll')) {
        $binary = Join-Path $destination "$folder/$component"
        if ([Diagnostics.FileVersionInfo]::GetVersionInfo($binary).FileVersion -ne '1.2.0.0') { throw "Wrong candidate version: $binary" }
        if ($folder -eq 'Testing' -and (Get-FileHash -LiteralPath $binary).Hash -ne (Get-FileHash -LiteralPath (Join-Path $destination "App/$component")).Hash) {
            throw "Testing must load the shipped product component: $component"
        }
    }
}
$manifest = Get-ChildItem -LiteralPath $destination -File -Recurse | ForEach-Object {
    [ordered]@{ path=[IO.Path]::GetRelativePath($destination,$_.FullName); bytes=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant() }
}
[ordered]@{
    productVersion='1.2.0'; stage='A'; architecture='x64'; minimumWindowsBuild=22000; maximumWindowsBuild=$null
    baseline='bca0e2c'; sourceBranch='feature/win11-compatibility-v1.2.0'; selfContained=$true
    iteration=$Iteration
    realInstallationPerformed=$false; formalRelease=$false; userDataIncluded=$false
    files=$manifest
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $destination 'package-manifest.json') -Encoding utf8
Write-Output "Published stage A candidate: $destination"
