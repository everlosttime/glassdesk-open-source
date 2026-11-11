[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$ProductDirectory,
    [Parameter(Mandatory=$true)][string]$NativeDll,
    [Parameter(Mandatory=$true)][string]$OutputDirectory
)
$ErrorActionPreference='Stop'
$product=[IO.Path]::GetFullPath($ProductDirectory).TrimEnd('\')
$output=[IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
if (Test-Path -LiteralPath $output) { throw 'Use a fresh package output directory.' }
if ($output -eq $product -or $output.StartsWith($product+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Package output must be outside the source product.' }
if (!(Test-Path -LiteralPath (Join-Path $product 'GlassDesk.exe'))) { throw 'Complete published product required.' }
$payload=Join-Path $output 'GlassDesk-NativeDock-Test'
$null=New-Item -ItemType Directory -Path $payload
Get-ChildItem -LiteralPath $product | Where-Object { $_.Name -notin @('Data','.native-static-test-root') } | Copy-Item -Destination $payload -Recurse
Copy-Item -LiteralPath $NativeDll -Destination (Join-Path $payload 'GlassDesk.Rendering.Native.dll')
@'
param([ValidateSet('legacy','native-static')][string]$Backend='legacy')
$env:GLASSDESK_DOCK_RENDERER=$Backend
$env:GLASSDESK_NATIVE_RENDER_DLL=$null
$env:GLASSDESK_RENDER_TEST_DIRECTORY=$null
Start-Process -FilePath (Join-Path $PSScriptRoot 'GlassDesk.exe') -WorkingDirectory $PSScriptRoot -WindowStyle Hidden
'@ | Set-Content -LiteralPath (Join-Path $payload 'Start-Test.ps1') -Encoding utf8
@'
GlassDesk Dock static wallpaper native rendering EXPERIMENTAL TEST PACKAGE

Not a release or installer. Default backend is legacy. Existing installation is untouched.
Close the running GlassDesk normally before starting this copy (same product single-instance guard).
Copy your Data into this separate directory only if you want your existing library/icons/layouts.
Keep the original Data and installation as your recovery source. Personal Data is not in this ZIP.

Choose static wallpaper background (UseSystemBackdrop=false) and desktop-layer pinning off in
the TEST COPY settings. AndroidLiquidGlass and FrostedGlass are supported. PlainGlass bypasses
capture and native material. Dynamic/desktop-only modes stay on legacy; no window attachment changes.

PowerShell:
  .\Start-Test.ps1 -Backend legacy
  .\Start-Test.ps1 -Backend native-static

Exit via the tray before switching. To roll back, close the test copy and start the original EXE.
An unclean native session disables native on the next launch; preserve the marker in
Data/recovery/native-static before investigating. Never auto-delete it to bypass the guard.

Remaining gates: Win10 product material tests; Win+D, lock/sleep, mixed DPI/cross-adapter input
and long-run recovery on the integrated product. Phase 1 aggregate gate is still false.
This package does not claim a 25% memory reduction or measured displayed FPS.
'@ | Set-Content -LiteralPath (Join-Path $payload 'README-Test.txt') -Encoding utf8
$files=@(Get-ChildItem -LiteralPath $payload -Recurse -File | ForEach-Object {
    [pscustomobject]@{path=$_.FullName.Substring($payload.Length+1);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
})
if ($files.path | Where-Object { $_ -match '(^|\\)Data(\\|$)|session-active|product\.json|dock\.png' }) { throw 'Personal/test evidence must not enter a portable package.' }
$files | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $payload 'payload-sha256.json')
$zip=Join-Path $output 'GlassDesk-NativeDock-Test-win-x64.zip'
Compress-Archive -Path (Join-Path $payload '*') -DestinationPath $zip -CompressionLevel Optimal
$hash=(Get-FileHash -LiteralPath $zip).Hash
[IO.File]::WriteAllText($zip+'.sha256',$hash+'  '+[IO.Path]::GetFileName($zip))
[ordered]@{zip=$zip;sha256=$hash;files=$files.Count;personalDataIncluded=$false;published=$false;
    nativeSha256=(Get-FileHash -LiteralPath (Join-Path $payload 'GlassDesk.Rendering.Native.dll')).Hash} |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'package.json')
Get-Item -LiteralPath $zip | Select-Object FullName,Length
