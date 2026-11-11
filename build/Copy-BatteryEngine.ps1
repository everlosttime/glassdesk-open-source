# SPDX-License-Identifier: AGPL-3.0-only
# Offline, separately licensed battery-reader component. No installer or upstream build script is run.
# Keep ASCII for Windows PowerShell 5.1.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [string]$LockFile = (Join-Path $PSScriptRoot 'battery-engine.lock.json')
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$vendor = Join-Path $repo 'third_party/myfinder-device-battery'
$source = Join-Path $repo 'components-src/battery-engine'
$lock = Get-Content -LiteralPath $LockFile -Raw -Encoding UTF8 | ConvertFrom-Json
if ($lock.schemaVersion -ne 1 -or $lock.upstreamCommit -cne 'bbcb779be4633ecf9183f53409de118dc9cb1fd6') {
    throw 'Unsupported battery engine lock.'
}

function Resolve-ComponentPath([string]$Root, [string]$Relative) {
    if ([string]::IsNullOrWhiteSpace($Relative) -or $Relative.Contains('\') -or $Relative.Contains(':') -or
        $Relative.StartsWith('/') -or $Relative -match '(^|/)\.\.?(/|$)' -or $Relative.Contains('//')) {
        throw "Unsafe battery engine path: '$Relative'."
    }
    foreach ($segment in $Relative.Split('/')) {
        if ($segment.EndsWith('.') -or $segment.EndsWith(' ') -or $segment -match '[<>"|?*\x00-\x1f]' -or
            ($segment -split '\.')[0] -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$') {
            throw "Unsafe battery engine path: '$Relative'."
        }
    }
    return Join-Path $Root $Relative
}

function Get-ComponentFiles([string]$Root) {
    $rootItem = Get-Item -LiteralPath $Root -Force
    if (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Battery engine reparse point: '$Root'." }
    foreach ($item in Get-ChildItem -LiteralPath $Root -Force) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Battery engine reparse point: '$($item.FullName)'." }
        if ($item.PSIsContainer) { Get-ComponentFiles $item.FullName }
        else { $item }
    }
}

function Assert-ComponentFiles([string]$Root, [object[]]$Records, [string]$Context) {
    $expected = @{}
    foreach ($record in $Records) {
        $path = Resolve-ComponentPath $Root ([string]$record.relativePath)
        $key = ([string]$record.relativePath).ToLowerInvariant()
        if ($expected.ContainsKey($key) -or $record.sha256 -notmatch '^[a-f0-9]{64}$' -or [long]$record.length -lt 0) {
            throw "Invalid battery engine $Context manifest record."
        }
        $expected[$key] = $true
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
            (Get-Item -LiteralPath $path).Length -ne [long]$record.length -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $record.sha256) {
            throw "Battery engine $Context checksum mismatch: '$($record.relativePath)'."
        }
    }
    foreach ($item in Get-ComponentFiles $Root) {
        $relative = $item.FullName.Substring($Root.TrimEnd('\').Length+1).Replace('\','/').ToLowerInvariant()
        if (-not $expected.ContainsKey($relative)) { throw "Unlisted battery engine $Context file: '$relative'." }
    }
}

function Expand-CheckedArchive([string]$Archive, [string]$Destination) {
    $zip = [IO.Compression.ZipFile]::OpenRead($Archive)
    try {
        $seen = @{}
        foreach ($entry in $zip.Entries) {
            $relative = $entry.FullName.Replace('\','/')
            if ($relative.EndsWith('/')) { continue }
            $path = Resolve-ComponentPath $Destination $relative
            if ($seen.ContainsKey($relative.ToLowerInvariant()) -or (Test-Path -LiteralPath $path)) {
                throw "Duplicate battery runtime archive path: '$relative'."
            }
            $seen[$relative.ToLowerInvariant()] = $true
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $path, $false)
        }
    }
    finally { $zip.Dispose() }
}

function Write-BridgeSourceArchive([string]$Destination) {
    $archive = [IO.Compression.ZipFile]::Open($Destination, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($record in ($lock.componentFiles | Sort-Object relativePath -CaseSensitive)) {
            # NoCompression makes the source archive stable across Framework/Core zlib versions.
            $entry = $archive.CreateEntry([string]$record.relativePath, [IO.Compression.CompressionLevel]::NoCompression)
            $entry.LastWriteTime = [DateTimeOffset]::new(2026,11,6,0,0,0,[TimeSpan]::Zero)
            $input = [IO.File]::OpenRead((Resolve-ComponentPath $source ([string]$record.relativePath)))
            $output = $entry.Open()
            try { $input.CopyTo($output) }
            finally { $output.Dispose(); $input.Dispose() }
        }
        $entry = $archive.CreateEntry('build/Copy-BatteryEngine.ps1', [IO.Compression.CompressionLevel]::NoCompression)
        $entry.LastWriteTime = [DateTimeOffset]::new(2026,11,6,0,0,0,[TimeSpan]::Zero)
        $input = [IO.File]::OpenRead($PSCommandPath)
        $output = $entry.Open()
        try { $input.CopyTo($output) }
        finally { $output.Dispose(); $input.Dispose() }
    }
    finally { $archive.Dispose() }
}

# Validate every input, including complete corresponding source and all notices, before execution.
if ($lock.copyScriptSha256 -notmatch '^[a-f0-9]{64}$' -or
    (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash -ine $lock.copyScriptSha256) {
    throw 'Battery engine packaging script checksum mismatch; review and refresh the lock.'
}
Assert-ComponentFiles $vendor $lock.vendorFiles 'vendor'
Assert-ComponentFiles $source $lock.componentFiles 'source'
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$target = Join-Path $outputRoot 'components/battery-engine'
$ancestor = $target
while ($ancestor) {
    if (Test-Path -LiteralPath $ancestor) {
        if (((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Battery engine output has a reparse point: '$ancestor'."
        }
    }
    $parent = [IO.Directory]::GetParent($ancestor)
    if ($null -eq $parent) { break }
    $ancestor = $parent.FullName
}
if (Test-Path -LiteralPath $target) { throw "Battery engine target must be fresh: '$target'." }
$stageRoot = Join-Path ([IO.Path]::GetTempPath()) ('GlassDesk-battery-stage-' + [guid]::NewGuid().ToString('N'))
$stage = Join-Path $stageRoot 'battery-engine'
$runtime = Join-Path $stage 'runtime'
$correspondingSource = Join-Path $stage 'corresponding-source'
New-Item -ItemType Directory -Force -Path $runtime,$correspondingSource | Out-Null
try {
Add-Type -AssemblyName System.IO.Compression.FileSystem
Expand-CheckedArchive (Join-Path $vendor 'archives/python-3.14.8-embed-amd64.zip') $runtime
Expand-CheckedArchive (Join-Path $vendor 'archives/hidapi-0.15.0-cp314-cp314-win_amd64.whl') $runtime
Copy-Item -LiteralPath (Join-Path $vendor 'python314._pth') -Destination (Join-Path $runtime 'python314._pth') -Force
Copy-Item -LiteralPath (Join-Path $vendor 'engine') -Destination $stage -Recurse
foreach ($name in @('LICENSE','THIRD_PARTY_NOTICES.md','NOTICE.txt','dependency-materials.json')) {
    Copy-Item -LiteralPath (Join-Path $vendor $name) -Destination $stage
}
Copy-Item -LiteralPath (Join-Path $vendor 'dependencies.lock.json') -Destination (Join-Path $stage 'upstream-dependencies.lock.json')
foreach ($name in @('myfinder-device-battery-bbcb779be4633ecf9183f53409de118dc9cb1fd6.zip',
    'Python-3.14.8.tar.xz','Python-3.14.8.tar.xz.sigstore','hidapi-0.15.0.tar.gz')) {
    Copy-Item -LiteralPath (Join-Path $vendor ('archives/' + $name)) -Destination $correspondingSource
}
foreach ($name in @('glassdesk_bridge.py','native_profiles.py','coverage.json')) {
    Copy-Item -LiteralPath (Join-Path $source $name) -Destination $stage
}
Write-BridgeSourceArchive (Join-Path $correspondingSource 'glassdesk-battery-engine.zip')
Assert-ComponentFiles $stage $lock.payloadFiles 'payload'
$python = Join-Path $runtime 'python.exe'
$signature = Get-AuthenticodeSignature -LiteralPath $python
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Python Software Foundation') {
    throw 'Official portable Python publisher signature is not valid.'
}
& $python -I -B -c 'import hid,ctypes,json,sys; assert sys.flags.isolated == 1; assert sys.flags.no_site == 1; assert hid.version_str() == "0.15.0"; print("PASS: isolated offline CPython/HIDAPI imports.")'
if ($LASTEXITCODE -ne 0) { throw 'Portable battery runtime dependency smoke failed.' }
$smokeOutput = & $python -I -B (Join-Path $stage 'glassdesk_bridge.py') --smoke
if ($LASTEXITCODE -ne 0) { throw 'Portable battery engine smoke failed.' }
$smoke = $smokeOutput | ConvertFrom-Json
if ($smoke.schema -ne 1 -or $smoke.upstreamCommit -cne $lock.upstreamCommit -or $smoke.coverageComplete -ne $true) {
    throw 'Portable battery engine coverage or commit smoke failed.'
}
Write-Output $smokeOutput
Copy-Item -LiteralPath $LockFile -Destination (Join-Path $stage 'component-manifest.json')
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
Move-Item -LiteralPath $stage -Destination $target
Write-Output ('PASS: complete pinned offline battery engine copied to ' + $target)
}
finally {
    $safeCache = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
    $resolvedStage = [IO.Path]::GetFullPath($stageRoot)
    if (-not $resolvedStage.StartsWith(($safeCache + '\GlassDesk-battery-stage-'), [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolvedStage) -notmatch '^GlassDesk-battery-stage-[0-9a-f]{32}$') {
        throw 'Refusing to remove a battery engine stage outside its private cache.'
    }
    if (Test-Path -LiteralPath $resolvedStage) { Remove-Item -LiteralPath $resolvedStage -Recurse -Force }
}
