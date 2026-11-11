# Verifies the immutable program payload of the user-selected acceptance build.
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$PayloadDirectory)
$ErrorActionPreference = 'Stop'
$manifest = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '../packaging/capture-e3f9f152.json') | ConvertFrom-Json
$failures = @()
foreach ($file in $manifest.files) {
    $path = Join-Path $PayloadDirectory $file.path
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { $failures += "Missing: $($file.path)"; continue }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.sha256) { $failures += "Different: $($file.path)" }
}
if ($failures.Count -gt 0) { throw ($failures -join [Environment]::NewLine) }
Write-Output "Verified $($manifest.files.Count) unchanged program files from $($manifest.acceptanceId)."
