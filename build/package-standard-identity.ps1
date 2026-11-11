[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$SeedDirectory,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [string]$DotnetPath = 'D:\dotnet10\dotnet.exe',
    [string]$InnoCompilerPath = 'D:\Toolchains\InnoSetup-7.1.0-x64\Compiler\ISCC.exe'
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$seed = (Resolve-Path -LiteralPath $SeedDirectory).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Output must be a fresh directory.' }
if ($output.StartsWith($seed.TrimEnd('\')+'\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Output must not be inside the seed directory.'
}
New-Item -ItemType Directory -Path $output | Out-Null
$publish = Join-Path $output 'publish'
& $DotnetPath publish (Join-Path $repoRoot 'src/GlassDesk.App/GlassDesk.App.csproj') -c Release -r win-x64 --self-contained true `
    '-p:PublishReadyToRun=true' '-p:Version=0.2.8' '-p:AssemblyVersion=0.2.2.0' '-p:FileVersion=0.2.8.0' `
    '-p:InformationalVersion=0.2.8' '-p:IncludeSourceRevisionInInformationalVersion=false' -o $publish `
    *> (Join-Path $output 'publish.log')
if ($LASTEXITCODE -ne 0) { throw 'Publish failed; see publish.log.' }

# Retain the delivered visual libraries and data; replace only the host and platform identity.
$stage = Join-Path $output 'payload'
$null = New-Item -ItemType Directory -Path $stage
Get-ChildItem -LiteralPath $seed -Force | Where-Object { $_.Name -ne 'Data' } | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $stage -Recurse -Force
}
$updated = @('GlassDesk.exe','GlassDesk.dll','GlassDesk.Platform.dll','GlassDesk.deps.json','GlassDesk.runtimeconfig.json')
foreach ($component in $updated) {
    Copy-Item -LiteralPath (Join-Path $publish $component) -Destination $stage -Force
}
$preserved = @()
Get-ChildItem -LiteralPath $seed -File -Recurse | ForEach-Object {
    $relative = [IO.Path]::GetRelativePath($seed, $_.FullName)
    if ($relative -notin $updated -and $relative -notlike 'Data\*') {
        $before = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        $after = (Get-FileHash -LiteralPath (Join-Path $stage $relative) -Algorithm SHA256).Hash
        if ($before -ne $after) { throw "Preserved payload changed: $relative" }
        $preserved += [ordered]@{path=$relative; sha256=$after.ToLowerInvariant()}
    }
}
foreach ($component in @('GlassDesk.exe','GlassDesk.dll','GlassDesk.Platform.dll')) {
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $stage $component)).FileVersion.Trim()
    if ($version -ne '0.2.8.0') { throw "Incorrect file version: $component $version" }
}

# Run installation and startup checks against the actual packaged assemblies in isolation.
$testProject = Join-Path $repoRoot 'tests/GlassDesk.AppServices.Tests/GlassDesk.AppServices.Tests.csproj'
& $DotnetPath build $testProject -c Release *> (Join-Path $output 'test-build.log')
if ($LASTEXITCODE -ne 0) { throw 'Test build failed; see test-build.log.' }
$test = Join-Path $output 'packaged-identity-test'
Copy-Item -LiteralPath (Join-Path $repoRoot 'tests/GlassDesk.AppServices.Tests/bin/Release/net10.0-windows10.0.26100.0') -Destination $test -Recurse
Get-ChildItem -LiteralPath $stage -File -Filter '*.dll' | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $test -Force
}
& $DotnetPath vstest (Join-Path $test 'GlassDesk.AppServices.Tests.dll') `
    '/TestCaseFilter:FullyQualifiedName~InstallationLocatorTests|FullyQualifiedName~StartupRegistrationTests|FullyQualifiedName~StartupRegistrationBoundaryTests' `
    '/Logger:trx;LogFileName=identity-tests.trx' "/ResultsDirectory:$output" *> (Join-Path $output 'identity-tests.log')
if ($LASTEXITCODE -ne 0) { throw 'Packaged identity tests failed; see identity-tests.log.' }

$base = 'GlassDesk-0.2.8-win-x64'
$priorStage = $env:GLASSDESK_PACKAGE_STAGE
try {
    $env:GLASSDESK_PACKAGE_STAGE = $stage
    & $InnoCompilerPath '/DAppIdValue=GlassDesk.Independent' '/DProductVersion=0.2.8' '/DFileVersion=0.2.8.0' `
        "/DPackageBaseName=$base" "/O$output" (Join-Path $repoRoot 'packaging/GlassDesk-Capture.iss') `
        *> (Join-Path $output 'installer-build.log')
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed; see installer-build.log.' }
} finally { $env:GLASSDESK_PACKAGE_STAGE = $priorStage }
$installer = Join-Path $output ($base+'-Setup.exe')
$hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $base-Setup.exe" | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ascii
[ordered]@{
    version='0.2.8'; displayName='GlassDesk'; appId='GlassDesk.Independent'; startupValueName='GlassDesk.Independent'
    directorySelectionAlwaysShown=$true; unsigned=$true; installer=$installer; installerSha256=$hash
    updatedComponents=$updated; preservedPayload=$preserved
    excludedMachineProfileFileCount=@(Get-ChildItem -LiteralPath (Join-Path $seed 'Data') -File -Recurse -ErrorAction SilentlyContinue).Count
    packagedIdentityTestsPassed=$true
    realInstallationPerformed=$false; interactiveWizardVerified=$false
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'package-report.json') -Encoding utf8
Write-Output "Created $installer"
