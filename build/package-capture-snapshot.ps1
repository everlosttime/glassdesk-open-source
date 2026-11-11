# Packages the exact acceptance binaries; deliberately does not rebuild src/.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$AcceptanceDirectory,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [string]$InnoCompilerPath='D:\Toolchains\InnoSetup-7.1.0-x64\Compiler\ISCC.exe'
)
$ErrorActionPreference='Stop'
$repoRoot=Split-Path -Parent $PSScriptRoot
$original=[IO.Path]::GetFullPath($AcceptanceDirectory)
$output=[IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Output must be a fresh directory.' }
& (Join-Path $PSScriptRoot 'Verify-CapturePayload.ps1') -PayloadDirectory $original
$manifest=Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'packaging/capture-e3f9f152.json') | ConvertFrom-Json
New-Item -ItemType Directory -Path $output | Out-Null
$stage=Join-Path $output 'payload'
New-Item -ItemType Directory -Path $stage | Out-Null
foreach ($file in $manifest.files) { Copy-Item -LiteralPath (Join-Path $original $file.path) -Destination (Join-Path $stage $file.path) }
# Seed the user's chosen layout on first install only. No log, backup or recovery data.
$data=Join-Path $stage 'Data'
New-Item -ItemType Directory -Path $data | Out-Null
foreach ($file in @('settings.json','apps.json','dock-layout.json','launcher-layout.json')) {
    Copy-Item -LiteralPath (Join-Path $original ('Data/'+$file)) -Destination $data
}
Copy-Item -LiteralPath (Join-Path $original 'Data/icons') -Destination $data -Recurse
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs/third-party/dotnet-10.0.12/LICENSE.txt') -Destination (Join-Path $stage 'DOTNET-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs/third-party/dotnet-10.0.12/ThirdPartyNotices.txt') -Destination $stage
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs/third-party/inno-setup-license.txt') -Destination (Join-Path $stage 'INNO-SETUP-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs/third-party/android-liquid-glass') -Destination (Join-Path $stage 'android-liquid-glass-license') -Recurse
& (Join-Path $PSScriptRoot 'Verify-CapturePayload.ps1') -PayloadDirectory $stage
$base='GlassDesk-0.2.4-e3f9f152-win-x64'
$priorStage=$env:GLASSDESK_PACKAGE_STAGE
try {
    $env:GLASSDESK_PACKAGE_STAGE=$stage
    & $InnoCompilerPath '/DAppIdValue=GlassDesk.Capture.e3f9f152' '/DProductVersion=0.2.4 (e3f9f152)' '/DFileVersion=0.2.4.0' "/DPackageBaseName=$base" "/O$output" (Join-Path $repoRoot 'packaging/GlassDesk-Capture.iss') *> (Join-Path $output 'installer-build.log')
    if ($LASTEXITCODE -ne 0) { throw 'Inno compilation failed; see installer-build.log.' }
} finally { $env:GLASSDESK_PACKAGE_STAGE=$priorStage }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath (Join-Path $output ($base+'.zip'))
$artifacts=@(Get-ChildItem -LiteralPath $output -File | Where-Object Extension -in @('.exe','.zip') | ForEach-Object {
    [ordered]@{file=$_.Name;bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
})
[ordered]@{acceptanceId=$manifest.acceptanceId;unchangedProgramFiles=$manifest.files.Count;unsigned=$true;artifacts=$artifacts} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.json') -Encoding utf8
Copy-Item -LiteralPath (Join-Path $repoRoot 'packaging/capture-e3f9f152.json') -Destination $output
Write-Output "Created exact acceptance installer and ZIP in $output"
