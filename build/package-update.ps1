# Retain the verified capture-compatible runtime while replacing selected product components.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$SeedDirectory,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [string]$ProductVersion = '1.1.9',
    [string]$DotnetPath = 'D:\dotnet10\dotnet.exe',
    [string]$InnoCompilerPath = 'D:\Toolchains\InnoSetup-7.1.0-x64\Compiler\ISCC.exe',
    [switch]$IncludeLauncherMorph,
    [switch]$IncludeDock,
    [switch]$IncludePlatformAndCore
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$seed = (Resolve-Path -LiteralPath $SeedDirectory).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Output must be a fresh directory.' }
if ($output.StartsWith($seed.TrimEnd('\')+'\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Output must not be inside the seed directory.'
}
if ($ProductVersion -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') { throw 'A stable three-part product version is required.' }
$version = $ProductVersion
$windows11Compatibility = $true
if ($windows11Compatibility) {
    $IncludeLauncherMorph = $true
    $IncludeDock = $true
    $IncludePlatformAndCore = $true
}
$base = "GlassDesk-$version-win-x64"
$null = New-Item -ItemType Directory -Path $output
$publish = Join-Path $output 'publish'
& $DotnetPath publish (Join-Path $repoRoot 'src/GlassDesk.App/GlassDesk.App.csproj') -c Release -r win-x64 --self-contained true `
    '-p:PublishReadyToRun=true' "-p:Version=$version" '-p:AssemblyVersion=0.2.2.0' "-p:FileVersion=$version.0" `
    "-p:InformationalVersion=$version" '-p:IncludeSourceRevisionInInformationalVersion=false' -o $publish `
    *> (Join-Path $output 'publish.log')
if ($LASTEXITCODE -ne 0) { throw 'Publish failed; see publish.log.' }
$stage = Join-Path $output 'payload'
$null = New-Item -ItemType Directory -Path $stage
Get-ChildItem -LiteralPath $seed -Force | Where-Object { $_.Name -ne 'Data' -and $_.Extension -ne '.lease' } | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $stage -Recurse -Force
}
$updated = @('GlassDesk.exe','GlassDesk.dll','GlassDesk.AppServices.dll','GlassDesk.Settings.dll',
    'GlassDesk.deps.json','GlassDesk.runtimeconfig.json','update-source.json','GlassDesk.ico','LICENSE.txt','NOTICE.txt',
    'licenses\StatusBarPatch-NOTICE.md')
if ($IncludeLauncherMorph) { $updated += @('GlassDesk.UI.dll','GlassDesk.Launcher.dll') }
if ($IncludeDock) { $updated += 'GlassDesk.Dock.dll' }
if ($IncludePlatformAndCore) { $updated += @('GlassDesk.Platform.dll','GlassDesk.Core.dll') }
foreach ($component in $updated) {
    $destination = Join-Path $stage $component
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $publish $component) -Destination $destination -Force
}
$preserved = @()
Get-ChildItem -LiteralPath $seed -File -Recurse | ForEach-Object {
    $relative = [IO.Path]::GetRelativePath($seed, $_.FullName)
    if ($relative -notin $updated -and $relative -notlike 'Data\*' -and $_.Extension -ne '.lease') {
        $before = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        $after = (Get-FileHash -LiteralPath (Join-Path $stage $relative) -Algorithm SHA256).Hash
        if ($before -ne $after) { throw "Preserved payload changed: $relative" }
        $preserved += [ordered]@{path=$relative; sha256=$after.ToLowerInvariant()}
    }
}
$versioned = @('GlassDesk.exe','GlassDesk.dll','GlassDesk.AppServices.dll','GlassDesk.Settings.dll')
if ($IncludeLauncherMorph) { $versioned += @('GlassDesk.UI.dll','GlassDesk.Launcher.dll') }
if ($IncludeDock) { $versioned += 'GlassDesk.Dock.dll' }
if ($IncludePlatformAndCore) { $versioned += @('GlassDesk.Platform.dll','GlassDesk.Core.dll') }
foreach ($component in $versioned) {
    if ([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $stage $component)).FileVersion.Trim() -ne "$version.0") {
        throw "Incorrect file version: $component"
    }
}
$testProject = Join-Path $repoRoot 'tests/GlassDesk.AppServices.Tests/GlassDesk.AppServices.Tests.csproj'
& $DotnetPath build $testProject -c Release *> (Join-Path $output 'test-build.log')
if ($LASTEXITCODE -ne 0) { throw 'Test build failed; see test-build.log.' }
$test = Join-Path $output 'packaged-update-test'
Copy-Item -LiteralPath (Join-Path $repoRoot 'tests/GlassDesk.AppServices.Tests/bin/Release/net10.0-windows10.0.26100.0') -Destination $test -Recurse
Get-ChildItem -LiteralPath $stage -File -Filter '*.dll' | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $test -Force }
& $DotnetPath vstest (Join-Path $test 'GlassDesk.AppServices.Tests.dll') `
    '/TestCaseFilter:FullyQualifiedName~UpdateNoticeTests|FullyQualifiedName~UpdateClientTests|FullyQualifiedName~UpdateInstallerTests|FullyQualifiedName~InstallationLocatorTests|FullyQualifiedName~StartupRegistrationTests|FullyQualifiedName~StartupRegistrationBoundaryTests' `
    '/Logger:trx;LogFileName=update-tests.trx' "/ResultsDirectory:$output" *> (Join-Path $output 'update-tests.log')
if ($LASTEXITCODE -ne 0) { throw 'Packaged update/identity tests failed; see update-tests.log.' }
$priorStage = $env:GLASSDESK_PACKAGE_STAGE
try {
    $env:GLASSDESK_PACKAGE_STAGE = $stage
    $compatibilityDefine = '/DWindows11Compatibility=' + [int]$windows11Compatibility
    & $InnoCompilerPath '/DAppIdValue=GlassDesk.Independent' "/DProductVersion=$version" "/DFileVersion=$version.0" `
        $compatibilityDefine "/DPackageBaseName=$base" "/O$output" (Join-Path $repoRoot 'packaging/GlassDesk-Capture.iss') `
        *> (Join-Path $output 'installer-build.log')
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed; see installer-build.log.' }
} finally { $env:GLASSDESK_PACKAGE_STAGE = $priorStage }
$installer = Join-Path $output ($base+'-Setup.exe')
$hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $base-Setup.exe" | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ascii
[ordered]@{
    version=$version; appId='GlassDesk.Independent'; unsigned=$true; installer=$installer; installerSha256=$hash
    updateRepository='https://github.com/everlosttime/glassdesk-updates'; dataExcluded=$true
    launcherMorphIncluded=[bool]$IncludeLauncherMorph
    dockUpdated=[bool]$IncludeDock
    platformAndCoreUpdated=[bool]$IncludePlatformAndCore
    minimumWindowsBuild=22000
    windows11Compatibility=$windows11Compatibility
    updatedComponents=$updated; preservedPayload=$preserved; packagedUpdateTestsPassed=$true
    realUserInstallationPerformed=$false
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'package-report.json') -Encoding utf8
Write-Output "Created $installer"
