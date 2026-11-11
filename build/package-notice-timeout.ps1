# Keep the installed capture material and replace only the launcher assembly.
[CmdletBinding()]
param(
    [string]$InstalledDirectory = 'D:\GlassDesk-Capture',
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [string]$DotnetPath = 'D:\dotnet10\dotnet.exe',
    [string]$InnoCompilerPath = 'D:\Toolchains\InnoSetup-7.1.0-x64\Compiler\ISCC.exe'
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$output = [IO.Path]::GetFullPath($OutputDirectory)
$installed = [IO.Path]::GetFullPath($InstalledDirectory)
if (Test-Path -LiteralPath $output) { throw 'Output must be a fresh directory.' }
if ($output.StartsWith($installed.TrimEnd('\')+'\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Output must not be inside the installed application.'
}
New-Item -ItemType Directory -Path $output | Out-Null
$publish = Join-Path $output 'launcher-publish'
& $DotnetPath publish (Join-Path $repoRoot 'src/GlassDesk.App/GlassDesk.App.csproj') -c Release -r win-x64 --self-contained true `
    '-p:PublishReadyToRun=true' '-p:Version=0.2.5' '-p:AssemblyVersion=0.2.2.0' '-p:FileVersion=0.2.5.0' `
    '-p:InformationalVersion=0.2.5-notice-timeout' '-p:IncludeSourceRevisionInInformationalVersion=false' -o $publish `
    *> (Join-Path $output 'publish.log')
if ($LASTEXITCODE -ne 0) { throw 'Publish failed; see publish.log.' }

$manifest = Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'packaging/capture-e3f9f152.json') | ConvertFrom-Json
$stage = Join-Path $output 'payload'
New-Item -ItemType Directory -Path $stage | Out-Null
$preserved = @()
foreach ($file in $manifest.files) {
    $source = Join-Path $installed $file.path
    $target = Join-Path $stage $file.path
    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $target
    if ($file.path -notin @('GlassDesk.Launcher.dll','acceptance-build.json')) {
        $sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
        $targetHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
        if ($sourceHash -ne $targetHash) { throw "Copy changed $($file.path)." }
        $preserved += [ordered]@{ path=$file.path; sha256=$targetHash.ToLowerInvariant() }
    }
}
Copy-Item -LiteralPath (Join-Path $publish 'GlassDesk.Launcher.dll') -Destination $stage -Force
$launcherVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $stage 'GlassDesk.Launcher.dll')).FileVersion
if ($launcherVersion -ne '0.2.5.0') { throw "Incorrect launcher version: $launcherVersion" }
$data = Join-Path $stage 'Data'
New-Item -ItemType Directory -Path $data | Out-Null
foreach ($name in @('settings.json','apps.json','dock-layout.json','launcher-layout.json')) {
    Copy-Item -LiteralPath (Join-Path $installed ('Data/'+$name)) -Destination $data
}
Copy-Item -LiteralPath (Join-Path $installed 'Data/icons') -Destination $data -Recurse
$metadata = Get-Content -Raw -LiteralPath (Join-Path $stage 'acceptance-build.json') | ConvertFrom-Json
$metadata | Add-Member -NotePropertyName noticeTimeoutRelease -NotePropertyValue '0.2.5' -Force
$metadata | Add-Member -NotePropertyName successNoticeLifetimeSeconds -NotePropertyValue 3 -Force
$metadata | Add-Member -NotePropertyName launcherAssemblyReplaced -NotePropertyValue $true -Force
$metadata | Add-Member -NotePropertyName originalSourceUnchanged -NotePropertyValue $false -Force
$metadata | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $stage 'acceptance-build.json') -Encoding utf8

# Exercise the packaged launcher with the actual retained production dependencies.
$testSource = Join-Path $repoRoot 'tests/GlassDesk.NoticeAcceptance/bin/Release/net10.0-windows10.0.26100.0'
$test = Join-Path $output 'packaged-notice-test'
Copy-Item -LiteralPath $testSource -Destination $test -Recurse
Get-ChildItem -LiteralPath $stage -File -Filter '*.dll' | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $test -Force
}
& $DotnetPath (Join-Path $test 'GlassDesk.NoticeAcceptance.dll') (Join-Path $output 'packaged-notice-test.json') `
    *> (Join-Path $output 'packaged-notice-test.log')
if ($LASTEXITCODE -ne 0) { throw 'Packaged notice checks failed; see packaged-notice-test.json.' }

$base = 'GlassDesk-0.2.5-notice-timeout-win-x64'
$priorStage = $env:GLASSDESK_PACKAGE_STAGE
try {
    $env:GLASSDESK_PACKAGE_STAGE = $stage
    & $InnoCompilerPath '/DAppIdValue=GlassDesk.Capture.e3f9f152' '/DProductVersion=0.2.5' '/DFileVersion=0.2.5.0' `
        "/DPackageBaseName=$base" "/O$output" (Join-Path $repoRoot 'packaging/GlassDesk-Capture.iss') `
        *> (Join-Path $output 'installer-build.log')
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed; see installer-build.log.' }
} finally { $env:GLASSDESK_PACKAGE_STAGE = $priorStage }
$installer = Join-Path $output ($base+'-Setup.exe')
$hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $([IO.Path]::GetFileName($installer))" | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ascii
[ordered]@{
    version='0.2.5'; successNoticeLifetimeSeconds=3; unsigned=$true
    installer=$installer; installerSha256=$hash; preservedProgramFiles=$preserved
    launcherSha256=(Get-FileHash -LiteralPath (Join-Path $stage 'GlassDesk.Launcher.dll')).Hash.ToLowerInvariant()
    packagedNoticeTestPassed=$true; installedApplicationUnchanged=$true; installationTestPerformed=$false
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'package-report.json') -Encoding utf8
Write-Output "Created $installer"
