[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
Start-Transcript -Path (Join-Path $repoRoot 'docs\evidence\capture-compatible-backdrop-20261001\installation.log') -Force | Out-Null
try {
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Windows requires administrator approval to trust this exact local test certificate.' }
$outputRoot = Join-Path $repoRoot 'artifacts\capture-compatible-backdrop\local-probe'
$state = Get-Content -LiteralPath (Join-Path $outputRoot 'local-install-state.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($state.packageName -ne 'GlassDesk.BackdropSpike' -or -not $state.testOnly -or $state.unsigned -or $state.certificateThumbprint -notmatch '^[A-Fa-f0-9]{40}$') { throw 'Prepared installation record is invalid.' }
$certificateFile = Join-Path $outputRoot 'LocalProbe.cer'
$certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($certificateFile)
if ($certificate.Thumbprint -ne $state.certificateThumbprint -or $certificate.Subject -ne 'CN=GlassDesk Backdrop Spike Local Test') { throw 'Prepared certificate does not match the local probe.' }
$existing = Get-AppxPackage -Name 'GlassDesk.BackdropSpike'
if (-not $existing) {
    Import-Certificate -FilePath $certificateFile -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
    Add-AppxPackage -Path $state.package -ExternalLocation $state.runtime
}
$evidence = Join-Path $repoRoot 'docs\evidence\capture-compatible-backdrop-20261001'
& (Join-Path $state.runtime 'GlassBackdropSpike.exe') --identity (Join-Path $evidence 'identity-green.json')
if ($LASTEXITCODE -ne 0) { throw 'Installed identity check failed.' }
Write-Output 'Local test identity installed. Run the probe normally for the separate Windows borderless permission prompt.'
}
catch {
    $_ | Out-String | Write-Output
    throw
}
finally { Stop-Transcript | Out-Null }
