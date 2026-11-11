[CmdletBinding()]
param(
    [string]$DotnetPath = 'D:\dotnet10\dotnet.exe',
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$Version = '1.4.5-win10-merge.5'
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+-win10-merge\.\d+$') { throw 'Use a candidate version.' }
$repoRoot = Split-Path -Parent $PSScriptRoot
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Output must be a fresh directory.' }
$null = New-Item -ItemType Directory -Path $output
$name = "GlassDesk-$Version-win-x64"
$payload = Join-Path $output $name
$probe = Join-Path $output 'probe-publish'
$publishArgs = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
    '-p:PublishReadyToRun=true', "-p:Version=$Version", '-p:AssemblyVersion=0.2.2.0',
    '-p:FileVersion=1.4.5.0', "-p:InformationalVersion=$Version",
    '-p:IncludeSourceRevisionInInformationalVersion=false', '-p:GlassDeskCompatibilityPreview=true')
& $DotnetPath publish (Join-Path $repoRoot 'src/GlassDesk.App/GlassDesk.App.csproj') @publishArgs -o $payload *> (Join-Path $output 'publish-app.log')
if ($LASTEXITCODE -ne 0) { throw 'Application publish failed; see publish-app.log.' }
& $DotnetPath publish (Join-Path $repoRoot 'tests/GlassDesk.CompatibilityProbe/GlassDesk.CompatibilityProbe.csproj') @publishArgs -o $probe *> (Join-Path $output 'publish-probe.log')
if ($LASTEXITCODE -ne 0) { throw 'Probe publish failed; see publish-probe.log.' }
Get-ChildItem -LiteralPath $probe -File | Where-Object { $_.Name -like 'GlassCaptureProbe.*' } | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $payload
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'dual-windows-candidate/Start-GlassDesk.cmd') -Destination $payload
foreach ($collector in @('Collect-Desktop-Background.cmd', 'Collect-Desktop-Background.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot ('dual-windows-candidate/' + $collector)) -Destination $payload
}
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs/win10-transparent-overlay-20261022.md') -Destination (Join-Path $payload 'README-TEST.md')
foreach ($required in @('GlassDesk.exe', 'GlassDesk.UI.dll', 'GlassDesk.Platform.dll', 'GlassDesk.Dock.dll',
    'GlassDesk.Launcher.dll', 'GlassCaptureProbe.exe', 'coreclr.dll', 'hostfxr.dll', 'PresentationFramework.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $payload $required))) { throw "Missing payload: $required" }
}
$baseCommit = (& git -C $repoRoot rev-parse HEAD).Trim()
$files = @(Get-ChildItem -LiteralPath $payload -File -Recurse | Sort-Object FullName | ForEach-Object {
    [ordered]@{ path=$_.FullName.Substring($payload.Length + 1); bytes=$_.Length
        sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
[ordered]@{
    version=$Version; sourceBaseCommit=$baseCommit; sourceHasLocalChanges=$true
    minimumWindowsBuild=19041; architecture='x64'; selfContained=$true; protocol=5
    windows10Provider='IsolatedDocumentedScene/PrintWindow/TransparentOverlayExclusion/ExplicitDesktopComposition/ValidatedWindowsWallpaperCache'
    windows11Provider='IsolatedSharedVisual/WGC'; stableUpdatesEnabled=$false
    mergedWin10RuntimeVerified=$false; buildTimeUtc=[DateTime]::UtcNow.ToString('o'); files=$files
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $payload 'build-info.json') -Encoding UTF8
$zip = Join-Path $output ($name + '.zip')
Compress-Archive -LiteralPath $payload -DestinationPath $zip -CompressionLevel Optimal
((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($zip)) |
    Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ASCII
Write-Output $zip
