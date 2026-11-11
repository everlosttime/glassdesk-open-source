# Package the accepted R9 bytes, without rebuilding unrelated visual components.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$TestKitDirectory,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [string]$DotnetPath = 'D:\dotnet10\dotnet.exe',
    [string]$InnoCompilerPath = 'D:\Toolchains\InnoSetup-7.1.0-x64\Compiler\ISCC.exe',
    [switch]$Resume
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$kit = (Resolve-Path -LiteralPath $TestKitDirectory).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
$payload = Join-Path $output 'payload'
if ((Test-Path -LiteralPath $payload) -and -not $Resume) { throw 'Payload must be a fresh directory.' }
$null = New-Item -ItemType Directory -Path $output -Force
$manifest = Get-Content -LiteralPath (Join-Path $kit 'package-manifest.json') -Raw | ConvertFrom-Json
if ($manifest.iteration -ne 'compatible-capture-r9') { throw 'Expected the accepted R9 TestKit.' }
$null = New-Item -ItemType Directory -Path $payload -Force
$files = @()
foreach ($entry in $manifest.files | Where-Object path -like 'App/*') {
    $relative = $entry.path.Substring(4)
    if ($relative -match '(^|/)(Data[^/]*|Reports)(/|$)' -or $relative -match '\.pdb$') { continue }
    $source = Join-Path $kit $entry.path
    if ((Get-FileHash -LiteralPath $source).Hash.ToLowerInvariant() -ne $entry.sha256) { throw "Changed R9 input: $relative" }
    $target = Join-Path $payload $relative
    $null = New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force
    if (Test-Path -LiteralPath $target) {
        if ((Get-FileHash -LiteralPath $target).Hash.ToLowerInvariant() -ne $entry.sha256) { throw "Existing bytes differ: $relative" }
    } else { Copy-Item -LiteralPath $source -Destination $target }
    if ((Get-FileHash -LiteralPath $target).Hash.ToLowerInvariant() -ne $entry.sha256) { throw "Copied bytes differ: $relative" }
    $files += [ordered]@{path=$relative; bytes=$entry.bytes; sha256=$entry.sha256}
}
$expectedLauncher = '3d67e5af723ce766d323bdd923263009467861a6810bc773e7da18fb78c1e4c1'
if ((Get-FileHash -LiteralPath (Join-Path $payload 'GlassDesk.Launcher.dll')).Hash.ToLowerInvariant() -ne $expectedLauncher) { throw 'Launcher is not the physically tested R9.' }
foreach ($name in @('GlassDesk.exe','GlassDesk.dll','GlassDesk.Launcher.dll','GlassDesk.UI.dll')) {
    if ([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $payload $name)).FileVersion.Trim() -ne '1.2.0.0') { throw "Wrong product version: $name" }
}
foreach ($project in @('GlassDesk.AppServices.Tests','GlassDesk.UpdateAcceptance')) {
    & $DotnetPath build (Join-Path $repo "tests/$project/$project.csproj") -c Release '-p:InformationalVersion=1.2.0' '-p:IncludeSourceRevisionInInformationalVersion=false' *> (Join-Path $output "$project-build.log")
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $project" }
    $test = Join-Path $output $project
    $null = New-Item -ItemType Directory -Path $test -Force
    Get-ChildItem -LiteralPath (Join-Path $repo "tests/$project/bin/Release/net10.0-windows10.0.26100.0") -Force | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $test -Recurse -Force }
    Get-ChildItem -LiteralPath $payload -File -Filter '*.dll' | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $test -Force }
    Get-ChildItem -LiteralPath (Join-Path $payload 'licenses') -Force | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $test 'licenses') -Recurse -Force }
}
& $DotnetPath vstest (Join-Path $output 'GlassDesk.AppServices.Tests/GlassDesk.AppServices.Tests.dll') '/TestCaseFilter:FullyQualifiedName~UpdateClientTests|FullyQualifiedName~UpdateInstallerTests|FullyQualifiedName~InstallationLocatorTests|FullyQualifiedName~StartupRegistrationTests|FullyQualifiedName~StartupRegistrationBoundaryTests' '/Logger:trx;LogFileName=update-tests.trx' "/ResultsDirectory:$output" *> (Join-Path $output 'update-tests.log')
if ($LASTEXITCODE -ne 0) { throw 'Frozen-payload update and identity checks failed.' }
& $DotnetPath (Join-Path $output 'GlassDesk.UpdateAcceptance/GlassDesk.UpdateAcceptance.dll') (Join-Path $output 'update-ui') *> (Join-Path $output 'update-ui.log')
if ($LASTEXITCODE -ne 0) { throw 'Frozen-payload updater UI acceptance failed.' }
$selftest = [Diagnostics.ProcessStartInfo]::new((Join-Path $payload 'GlassDesk.exe'))
$selftest.UseShellExecute = $false
$selftest.CreateNoWindow = $true
foreach ($argument in @('--selftest','--acceptance-data-root',(Join-Path $output 'selftest-data'),'--acceptance-report',(Join-Path $output 'selftest.txt'))) { $selftest.ArgumentList.Add($argument) }
$process = [Diagnostics.Process]::Start($selftest)
if (-not $process.WaitForExit(60000)) { throw 'Frozen-payload selftest timed out.' }
if ($process.ExitCode -ne 0) { throw "Frozen-payload selftest failed: $($process.ExitCode)" }
$process.Dispose()
$prior = $env:GLASSDESK_PACKAGE_STAGE
try {
    $env:GLASSDESK_PACKAGE_STAGE = $payload
    & $InnoCompilerPath '/DAppIdValue=GlassDesk.Independent' '/DProductVersion=1.2.0' '/DFileVersion=1.2.0.0' '/DPackageBaseName=GlassDesk-1.2.0-win-x64' "/O$output" (Join-Path $repo 'packaging/GlassDesk-Capture.iss') *> (Join-Path $output 'installer-build.log')
    if ($LASTEXITCODE -ne 0) { throw 'Production installer compilation failed.' }
} finally { $env:GLASSDESK_PACKAGE_STAGE = $prior }
$installer = Join-Path $output 'GlassDesk-1.2.0-win-x64-Setup.exe'
$hash = (Get-FileHash -LiteralPath $installer).Hash.ToLowerInvariant()
"$hash  GlassDesk-1.2.0-win-x64-Setup.exe" | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ascii
[ordered]@{version='1.2.0'; productBaseline='R9'; appId='GlassDesk.Independent'; minimumWindowsBuild=22000; architecture='win-x64'; installerSha256=$hash; launcherSha256=$expectedLauncher; frozenRuntimeFileCount=$files.Count; allRuntimeBytesMatchTestKit=$true; dataExcluded=$true; debugSymbolsExcluded=$true; packagedUpdateTestsPassed=$true; updaterUiPassed=$true; selftestPassed=$true; unsigned=$true; existingUserInstallationReplaced=$false; files=$files} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'package-report.json') -Encoding utf8
Write-Output "Created $installer; $($files.Count) runtime files match accepted R9."
