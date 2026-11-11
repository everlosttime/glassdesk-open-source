[CmdletBinding()]
param([string]$SdkBin, [string]$Dotnet = 'D:\dotnet10\dotnet.exe', [switch]$Unsigned, [switch]$PrepareOnly)
$ErrorActionPreference = 'Stop'
$probeRoot = Split-Path -Parent $PSScriptRoot
$repoRoot = (Resolve-Path (Join-Path $probeRoot '..\..')).Path
$outputRoot = Join-Path $repoRoot 'artifacts\capture-compatible-backdrop\local-probe'
$stage = Join-Path $outputRoot 'identity'
$runtime = Join-Path $outputRoot 'runtime'
if (-not $SdkBin) { $SdkBin = Join-Path $repoRoot 'artifacts\toolchain\windows-sdk-buildtools-10.0.26100.9169\bin\10.0.26100.0\x64' }
foreach ($tool in @('makeappx.exe','signtool.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $SdkBin $tool))) { throw "Official SDK tool missing: $tool" }
}
$existing = Get-AppxPackage -Name 'GlassDesk.BackdropSpike'
if ($existing) { throw 'The local probe identity is already registered. Preserve it; use the existing runtime or explicitly uninstall before reinstalling.' }
New-Item -ItemType Directory -Force -Path (Join-Path $stage 'Assets'),$runtime | Out-Null
& $Dotnet publish (Join-Path $probeRoot 'GlassBackdropSpike.csproj') -t:Rebuild -c Release -r win-x64 --self-contained true -o $runtime "-p:LocalProbeUnsigned=$($Unsigned.IsPresent.ToString().ToLowerInvariant())"
if ($LASTEXITCODE -ne 0) { throw 'Probe publish failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'AppxManifest.xml') -Destination (Join-Path $stage 'AppxManifest.xml')
if ($Unsigned) {
    $manifestFile = Join-Path $stage 'AppxManifest.xml'
    $manifestText = [IO.File]::ReadAllText($manifestFile).Replace('Publisher="CN=GlassDesk Backdrop Spike Local Test"', 'Publisher="CN=GlassDesk Backdrop Spike Local Test, OID.2.25.311729368913984317654407730594956997722=1"')
    [IO.File]::WriteAllText($manifestFile, $manifestText)
}
# A code-generated flat placeholder icon used only by the local identity package.
Add-Type -AssemblyName PresentationCore,WindowsBase
$iconPixels = New-Object byte[] (150 * 150 * 4)
for ($i = 0; $i -lt $iconPixels.Length; $i += 4) { $iconPixels[$i] = 90; $iconPixels[$i+1] = 190; $iconPixels[$i+2] = 70; $iconPixels[$i+3] = 255 }
$bitmap = [Windows.Media.Imaging.BitmapSource]::Create(150,150,96,96,[Windows.Media.PixelFormats]::Bgra32,$null,$iconPixels,600)
$encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
$encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
$iconStream = [IO.File]::Create((Join-Path $stage 'Assets\Logo.png'))
try { $encoder.Save($iconStream) } finally { $iconStream.Dispose() }
$package = Join-Path $outputRoot 'GlassDesk.BackdropSpike.msix'
& (Join-Path $SdkBin 'makeappx.exe') pack /o /d $stage /nv /p $package
if ($LASTEXITCODE -ne 0) { throw 'Identity packaging failed.' }
$stateFile = Join-Path $outputRoot 'local-install-state.json'
if ($Unsigned) {
    # Official Windows 11 test-only unsigned namespace. OS policy still decides whether installation is permitted.
    @{ packageName='GlassDesk.BackdropSpike'; runtime=$runtime; package=$package; testOnly=$true; unsigned=$true } | ConvertTo-Json | Set-Content -LiteralPath $stateFile -Encoding UTF8
    Add-AppxPackage -Path $package -ExternalLocation $runtime -AllowUnsigned
}
else {
    # Signed test installation requires elevation for machine certificate trust. Never change machine trust implicitly.
    $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $PrepareOnly -and -not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Local certificate trust requires an administrator. Use -PrepareOnly to build the reviewable signed probe without installing.' }
    $certificate = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=GlassDesk Backdrop Spike Local Test' -CertStoreLocation 'Cert:\CurrentUser\My' -NotAfter (Get-Date).AddDays(14)
    $publicCert = Join-Path $outputRoot 'LocalProbe.cer'
    Export-Certificate -Cert $certificate -FilePath $publicCert | Out-Null
    @{ packageName='GlassDesk.BackdropSpike'; certificateThumbprint=$certificate.Thumbprint; trustStore='Cert:\LocalMachine\TrustedPeople'; runtime=$runtime; package=$package; testOnly=$true; unsigned=$false } | ConvertTo-Json | Set-Content -LiteralPath $stateFile -Encoding UTF8
    & (Join-Path $SdkBin 'signtool.exe') sign /fd SHA256 /sha1 $certificate.Thumbprint /s My $package
    if ($LASTEXITCODE -ne 0) { throw 'Local test signing failed. See local-install-state.json for cleanup.' }
    if ($PrepareOnly) {
        # The prepared package and public certificate are sufficient for installation; remove the signing private key.
        Remove-Item -LiteralPath ('Cert:\CurrentUser\My\' + $certificate.Thumbprint)
        Write-Output "Signed local probe prepared: $package"
        return
    }
    Import-Certificate -FilePath $publicCert -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
    Add-AppxPackage -Path $package -ExternalLocation $runtime
}
& (Join-Path $runtime 'GlassBackdropSpike.exe') --identity (Join-Path $repoRoot 'docs\evidence\capture-compatible-backdrop-20261001\identity-green.json')
if ($LASTEXITCODE -ne 0) { throw 'Registered probe did not receive package identity.' }
Write-Output "Local probe runtime: $runtime"
