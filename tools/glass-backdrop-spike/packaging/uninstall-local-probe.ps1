[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$stateFile = Join-Path $repoRoot 'artifacts\capture-compatible-backdrop\local-probe\local-install-state.json'
if (-not (Test-Path -LiteralPath $stateFile)) { throw 'Local installation record is missing; do not guess which certificate to remove.' }
$state = Get-Content -LiteralPath $stateFile -Raw -Encoding UTF8 | ConvertFrom-Json
if ($state.packageName -ne 'GlassDesk.BackdropSpike' -or -not $state.testOnly -or (-not $state.unsigned -and $state.certificateThumbprint -notmatch '^[A-Fa-f0-9]{40}$')) { throw 'Invalid local installation record.' }
Get-AppxPackage -Name 'GlassDesk.BackdropSpike' | Remove-AppxPackage
if ($state.unsigned) { Write-Output 'Removed the unsigned local probe identity. Source and evidence are preserved.'; return }
if ($state.trustStore -and $state.trustStore -ne 'Cert:\LocalMachine\TrustedPeople') { throw 'Unexpected trust store; preserve certificate.' }
$stores = @('Cert:\CurrentUser\TrustedPeople','Cert:\CurrentUser\My')
if ($state.trustStore) { $stores += $state.trustStore }
foreach ($store in $stores) {
    $certPath = Join-Path $store $state.certificateThumbprint
    if (Test-Path -LiteralPath $certPath) {
        $certificate = Get-Item -LiteralPath $certPath
        if ($certificate.Subject -ne 'CN=GlassDesk Backdrop Spike Local Test') { throw 'Certificate subject mismatch; preserve certificate.' }
        Remove-Item -LiteralPath $certPath
    }
}
Write-Output 'Removed the local probe identity and its exact test certificate. Source and evidence are preserved.'
