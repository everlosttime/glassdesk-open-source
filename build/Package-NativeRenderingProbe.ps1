[CmdletBinding()]
param(
    [string]$DotnetPath = 'D:\dotnet10\dotnet.exe',
    [string]$ToolchainRoot = 'D:\Toolchains\GlassDesk-Native',
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $repo 'artifacts\native-rendering'
if (!$SkipBuild) { & (Join-Path $PSScriptRoot 'Run-NativeRenderingChecks.ps1') -DotnetPath $DotnetPath -ToolchainRoot $ToolchainRoot }
$published = Join-Path $artifacts 'probe'
$dllHash = (Get-FileHash -LiteralPath (Join-Path $published 'GlassDesk.Rendering.Native.dll') -Algorithm SHA256).Hash
$build = Get-Content -LiteralPath (Join-Path $artifacts 'build-receipt.json') -Raw | ConvertFrom-Json
if ($dllHash -ne $build.dll.Hash) { throw 'Published native DLL does not match the build receipt.' }
$checks = Get-Content -LiteralPath (Join-Path $artifacts 'checks-receipt.json') -Raw | ConvertFrom-Json
if ($checks.failed -ne 0 -or $checks.passed -lt 1 -or $checks.payload.Count -ne 6) { throw 'A passing checks receipt is required.' }
foreach ($checked in $checks.payload) {
    if ((Get-FileHash -LiteralPath (Join-Path $published $checked.name) -Algorithm SHA256).Hash -ne $checked.sha256) { throw "Payload changed after testing: $($checked.name)" }
}
$id = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
$stage = Join-Path $artifacts ('package-' + $id)
$zip = Join-Path $artifacts ('GlassDesk-NativeRenderingProbe-win-x64-' + $id + '.zip')
if ((Test-Path -LiteralPath $stage) -or (Test-Path -LiteralPath $zip)) { throw 'Package output already exists.' }
$null = New-Item -ItemType Directory -Path $stage
foreach ($file in Get-ChildItem -LiteralPath $published -File) {
    if ($file.Extension -in '.exe','.dll','.json','.pdb','.dat','.txt') { Copy-Item -LiteralPath $file.FullName -Destination $stage }
}
Copy-Item -LiteralPath (Join-Path $published 'licenses') -Destination (Join-Path $stage 'licenses') -Recurse
Copy-Item -LiteralPath (Join-Path $repo 'docs\evidence\native-rendering-20261028\README.md') -Destination (Join-Path $stage 'README-zh-CN.md')
$manifest = @(Get-ChildItem -LiteralPath $stage -File -Recurse | ForEach-Object {
    [pscustomobject]@{path=$_.FullName.Substring($stage.Length+1);sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
})
if (@($manifest | Where-Object path -match '(^|\\)(Data|observations|session-state|state)(\\|$)|native-session-active').Count) { throw 'A package must not contain profiles, observations or crash markers.' }
[ordered]@{kind='interop-prototype';productionIntegrationReady=$false;minimumWindowsBuild=19041;
    automatedValidatedWindowsBuild=$checks.osBuild;automatedChecksPassed=$checks.passed;sourceCommit=(& git -C $repo rev-parse HEAD).Trim();nativeSha256=$dllHash;
    unverified=@('Cross-system Win10 and Win11 acceptance gate','keyboard Win+D','cross-process click-through','mixed-DPI/multi-GPU','lock/sleep recovery','displayed FPS','production material quality','whole-product memory improvement');
    files=$manifest} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $stage 'prototype-manifest.json') -Encoding utf8
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($stage,$zip,[IO.Compression.CompressionLevel]::Optimal,$false)
$hash = Get-FileHash -LiteralPath $zip -Algorithm SHA256
[IO.File]::WriteAllText($zip+'.sha256', $hash.Hash.ToLowerInvariant()+'  '+[IO.Path]::GetFileName($zip)+[Environment]::NewLine)
$hash
