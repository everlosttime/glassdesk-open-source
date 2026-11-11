[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$SeedDirectory,
    [Parameter(Mandatory=$true)][string]$CandidateDirectory,
    [Parameter(Mandatory=$true)][string]$TestKitDirectory,
    [string]$DotnetPath = 'D:\dotnet10\dotnet.exe',
    [string]$InnoCompilerPath = 'D:\Toolchains\InnoSetup-7.1.0-x64\Compiler\ISCC.exe'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$seed = (Resolve-Path -LiteralPath $SeedDirectory).Path
$candidate = (Resolve-Path -LiteralPath $CandidateDirectory).Path
$kit = (Resolve-Path -LiteralPath $TestKitDirectory).Path
$payload = Join-Path $candidate 'payload'
$launcher = Join-Path $candidate 'publish/GlassDesk.Launcher.dll'
if ($seed -eq $payload -or $candidate -eq $kit) { throw 'Distinct seed and candidate directories required.' }
# Retain the already accepted background/runtime bytes. Only launcher code changes.
Get-ChildItem -LiteralPath $seed -Force | Where-Object { $_.Name -ne 'Data' } | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $payload -Recurse -Force
}
Copy-Item -LiteralPath $launcher -Destination $payload -Force
$preserved = @()
Get-ChildItem -LiteralPath $seed -File -Recurse | ForEach-Object {
    $relative = [IO.Path]::GetRelativePath($seed, $_.FullName)
    if ($relative -eq 'GlassDesk.Launcher.dll' -or $relative -like 'Data\*') { return }
    $hash = (Get-FileHash -LiteralPath $_.FullName).Hash
    if ($hash -ne (Get-FileHash -LiteralPath (Join-Path $payload $relative)).Hash) { throw "Changed preserved file: $relative" }
    $preserved += [ordered]@{path=$relative; sha256=$hash.ToLowerInvariant()}
}
foreach ($folder in @('App','Testing')) {
    foreach ($file in Get-ChildItem -LiteralPath $payload -File -Filter 'GlassDesk*') {
        $target = Join-Path $kit "$folder/$($file.Name)"
        if (Test-Path -LiteralPath $target) { Copy-Item -LiteralPath $file.FullName -Destination $target -Force }
    }
    foreach ($component in @('GlassDesk.UI.dll','GlassDesk.Launcher.dll','GlassDesk.Platform.dll','GlassDesk.Core.dll','GlassDesk.AppServices.dll','GlassDesk.Settings.dll')) {
        if ((Get-FileHash -LiteralPath (Join-Path $payload $component)).Hash -ne (Get-FileHash -LiteralPath (Join-Path $kit "$folder/$component")).Hash) {
            throw "Shipped and tested components differ: $folder/$component"
        }
    }
}
$test = Join-Path $candidate 'packaged-update-test'
Get-ChildItem -LiteralPath $payload -File -Filter '*.dll' | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $test -Force }
& $DotnetPath vstest (Join-Path $test 'GlassDesk.AppServices.Tests.dll') `
    '/TestCaseFilter:FullyQualifiedName~UpdateClientTests|FullyQualifiedName~UpdateInstallerTests|FullyQualifiedName~InstallationLocatorTests|FullyQualifiedName~StartupRegistrationTests|FullyQualifiedName~StartupRegistrationBoundaryTests' `
    '/Logger:trx;LogFileName=launcher-only-update-tests.trx' "/ResultsDirectory:$candidate" *> (Join-Path $candidate 'launcher-only-update-tests.log')
if ($LASTEXITCODE -ne 0) { throw 'Final payload update/identity checks failed.' }
$priorStage = $env:GLASSDESK_PACKAGE_STAGE
try {
    $env:GLASSDESK_PACKAGE_STAGE = $payload
    & $InnoCompilerPath '/DAppIdValue=GlassDesk.Independent' '/DProductVersion=1.2.0' '/DFileVersion=1.2.0.0' `
        '/DWindows11Compatibility=1' '/DPackageBaseName=GlassDesk-1.2.0-win-x64' "/O$candidate" (Join-Path $repo 'packaging/GlassDesk-Capture.iss') `
        *> (Join-Path $candidate 'launcher-only-installer-build.log')
    if ($LASTEXITCODE -ne 0) { throw 'Final launcher-only installer compilation failed.' }
} finally { $env:GLASSDESK_PACKAGE_STAGE = $priorStage }
$installer = Join-Path $candidate 'GlassDesk-1.2.0-win-x64-Setup.exe'
$reportPath = Join-Path $candidate 'package-report.json'
$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
$report.updatedComponents = @('GlassDesk.Launcher.dll')
$report.dockUpdated = $false
$report.platformAndCoreUpdated = $false
$report.preservedPayload = $preserved
$report.installerSha256 = (Get-FileHash -LiteralPath $installer).Hash.ToLowerInvariant()
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding utf8
"$($report.installerSha256)  GlassDesk-1.2.0-win-x64-Setup.exe" | Set-Content -LiteralPath (Join-Path $candidate 'SHA256SUMS.txt') -Encoding ascii
$manifestPath = Join-Path $kit 'package-manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$manifest.files = @(Get-ChildItem -LiteralPath $kit -File -Recurse | Where-Object FullName -ne $manifestPath | ForEach-Object {
    [ordered]@{path=[IO.Path]::GetRelativePath($kit,$_.FullName); bytes=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()}
})
$manifest | Add-Member -NotePropertyName preservedR2BackgroundComponents -NotePropertyValue $true -Force
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding utf8
Write-Output 'Finalized launcher-only candidate; background and other runtime bytes match R2.'
