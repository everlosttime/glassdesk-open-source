# Copy only the pinned runtime resources, never the upstream executable or user state.
[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$OutputDirectory)

$ErrorActionPreference = 'Stop'
$sourceRoot = Join-Path (Split-Path -Parent $PSScriptRoot) 'contrib/Naiwa'
$manifestPath = Join-Path $sourceRoot 'assets-manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$sourceAssets = Join-Path $sourceRoot 'assets-v2.5.1'
if ($manifest.version -ne '2.5.1' -or @($manifest.files).Count -ne 18) { throw 'Unexpected Naiwa asset manifest.' }
foreach ($file in $manifest.files) {
    if ([IO.Path]::GetFileName($file.name) -cne $file.name -or $file.name -notmatch '\.(nwp|mp3|wav)$') {
        throw 'Unsafe Naiwa asset name.'
    }
    $path = Join-Path $sourceAssets $file.name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
        (Get-Item -LiteralPath $path).Length -ne $file.length -or
        (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.sha256) {
        throw ('Missing or modified pinned Naiwa asset: ' + $file.name)
    }
}

$destination = Join-Path $OutputDirectory 'assets-v2.5.1'
New-Item -ItemType Directory -Path $destination -Force | Out-Null
foreach ($file in $manifest.files) {
    Copy-Item -LiteralPath (Join-Path $sourceAssets $file.name) -Destination (Join-Path $destination $file.name) -Force
}
Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $OutputDirectory 'assets-manifest.json') -Force
Copy-Item -LiteralPath (Join-Path $sourceRoot 'NOTICE.txt') -Destination $OutputDirectory -Force
