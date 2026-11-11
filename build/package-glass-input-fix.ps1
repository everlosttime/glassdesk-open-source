# Local candidate only; publication is a separate action.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [string]$ProductVersion = '1.2.2',
    [string]$DotnetPath = 'D:\dotnet10\dotnet.exe',
    [string]$InnoCompilerPath = 'D:\Toolchains\InnoSetup-7.1.0-x64\Compiler\ISCC.exe',
    [switch]$Repackage
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$output = [IO.Path]::GetFullPath($OutputDirectory)
if ((Test-Path -LiteralPath $output) -and -not $Repackage) { throw 'Use a fresh output directory.' }
$null = New-Item -ItemType Directory -Path $output -Force
if (-not $Repackage) {
foreach ($entry in @(
    @{project='src/GlassDesk.App/GlassDesk.App.csproj';folder='App'},
    @{project='tests/GlassDesk.LauncherAcceptance/GlassDesk.LauncherAcceptance.csproj';folder='Testing'},
    @{project='tests/GlassDesk.CompatibilityProbe/GlassDesk.CompatibilityProbe.csproj';folder='Probe'}
)) {
    & $DotnetPath publish (Join-Path $repo $entry.project) -c Release -r win-x64 --self-contained true `
        '-p:PublishReadyToRun=true' "-p:Version=$ProductVersion" "-p:FileVersion=$ProductVersion.0" "-p:InformationalVersion=$ProductVersion" '-p:IncludeSourceRevisionInInformationalVersion=false' `
        -o (Join-Path $output $entry.folder) *> (Join-Path $output ($entry.folder+'-publish.log'))
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $($entry.folder)" }
}
}
$stage = Join-Path $output 'App'
if (-not (Test-Path -LiteralPath (Join-Path $stage 'GlassDesk.exe'))) { throw 'Missing published application.' }
Get-ChildItem -LiteralPath $stage -File -Filter '*.pdb' | Remove-Item
if (Test-Path -LiteralPath (Join-Path $stage 'Data')) { throw 'User data cannot be packaged.' }
foreach ($folder in @('Testing','Probe')) {
    Get-ChildItem -LiteralPath $stage -File -Filter 'GlassDesk*.dll' | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $output $folder) -Force
    }
    foreach ($name in @('GlassDesk.exe','GlassDesk.runtimeconfig.json','GlassDesk.deps.json')) {
        Copy-Item -LiteralPath (Join-Path $stage $name) -Destination (Join-Path $output $folder) -Force
    }
}
$prior = $env:GLASSDESK_PACKAGE_STAGE
try {
    $env:GLASSDESK_PACKAGE_STAGE = $stage
    & $InnoCompilerPath '/DAppIdValue=GlassDesk.Independent' "/DProductVersion=$ProductVersion" "/DFileVersion=$ProductVersion.0" `
        "/DPackageBaseName=GlassDesk-$ProductVersion-win-x64" "/O$output" (Join-Path $repo 'packaging/GlassDesk-Capture.iss') `
        *> (Join-Path $output 'installer-build.log')
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
} finally { $env:GLASSDESK_PACKAGE_STAGE = $prior }
$installer = Join-Path $output "GlassDesk-$ProductVersion-win-x64-Setup.exe"
$hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  GlassDesk-$ProductVersion-win-x64-Setup.exe" | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ascii
$files = @(Get-ChildItem -LiteralPath $stage -File -Recurse | ForEach-Object {
    [ordered]@{path=[IO.Path]::GetRelativePath($stage,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()}
})
[ordered]@{version=$ProductVersion;baseline='1.2.1-R10';iteration='glass-input-r11';installerSha256=$hash;unsigned=$true;
    dataExcluded=$true;published=$false;existingUserInstallationReplaced=$false;files=$files} |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'package-report.json') -Encoding utf8
Write-Output "Created $installer"
