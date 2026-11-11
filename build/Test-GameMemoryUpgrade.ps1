[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$BaselineInstaller,
    [Parameter(Mandatory=$true)][string]$CandidateInstaller,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [string]$InstalledDirectory='D:\GlassDesk'
)
$ErrorActionPreference='Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'Use PowerShell 7 (pwsh).' }
$output=[IO.Path]::GetFullPath($OutputDirectory)
$live=[IO.Path]::GetFullPath($InstalledDirectory)
if (Test-Path -LiteralPath $output) { throw 'Use a fresh upgrade evidence directory.' }
if ($output.StartsWith($live+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Do not stage inside the live installation.' }
$stage=Join-Path $output 'InstalledCopy'
$null=New-Item -ItemType Directory -Path $stage -Force
$registration='Software\Microsoft\Windows\CurrentVersion\Uninstall\GlassDesk.Independent_is1'
$startup='Software\Microsoft\Windows\CurrentVersion\Run'
$registry=[Microsoft.Win32.Registry]::CurrentUser
function Snapshot-Key([string]$Name) {
    $key=$registry.OpenSubKey($Name)
    if ($null -eq $key) { return $null }
    try {
        if ($Name -eq $registration -and $key.SubKeyCount -ne 0) { throw "Unexpected child keys in $Name; refusing a lossy registry restoration." }
        @($key.GetValueNames() | ForEach-Object {
            [pscustomobject]@{name=$_;kind=$key.GetValueKind($_);value=$key.GetValue($_,$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)}
        })
    } finally { $key.Dispose() }
}
function Restore-Key([string]$Name,$Snapshot) {
    if ($null -eq $Snapshot) { $registry.DeleteSubKeyTree($Name,$false); return }
    $key=$registry.CreateSubKey($Name)
    try {
        foreach ($name in $key.GetValueNames()) { $key.DeleteValue($name,$false) }
        foreach ($entry in $Snapshot) { $key.SetValue($entry.name,$entry.value,$entry.kind) }
    } finally { $key.Dispose() }
}
function Manifest([string]$Directory) {
    @(Get-ChildItem -LiteralPath $Directory -File -Recurse | Sort-Object FullName | ForEach-Object {
        [pscustomobject]@{path=[IO.Path]::GetRelativePath($Directory,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
    })
}
function Install([string]$Installer,[string]$Log) {
    $file=[IO.Path]::GetFullPath($Installer)
    if (-not (Test-Path -LiteralPath $file)) { throw "Missing installer: $file" }
    # No GLASSDESKUPDATE parameter: the installer does not launch the copied app.
    $process=Start-Process -FilePath $file -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',
        '/MERGETASKS="!desktopicon"',('/DIR="'+$stage+'"'),('/LOG="'+$Log+'"')) -WindowStyle Hidden -PassThru -Wait
    if ($process.ExitCode -ne 0) { throw "Installer exit code $($process.ExitCode)" }
}
$beforeRegistry=Snapshot-Key $registration
$beforeStartup=Snapshot-Key $startup
$shortcuts=@(Join-Path ([Environment]::GetFolderPath('Programs')) 'GlassDesk.lnk';
    Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'GlassDesk.lnk')
$shortcutSnapshots=@($shortcuts | ForEach-Object { [pscustomobject]@{path=$_;bytes=if(Test-Path -LiteralPath $_){[IO.File]::ReadAllBytes($_)}else{$null}} })
$sourceProfile=Join-Path $live 'Data'
$beforeLive=Manifest $sourceProfile
$success=$false
$restored=$false
try {
    Install $BaselineInstaller (Join-Path $output 'baseline-install.log')
    Copy-Item -LiteralPath $sourceProfile -Destination $stage -Recurse
    $beforeCopy=Manifest (Join-Path $stage 'Data')
    Install $CandidateInstaller (Join-Path $output 'candidate-upgrade.log')
    $afterCopy=Manifest (Join-Path $stage 'Data')
    $copyPreserved=($beforeCopy | ConvertTo-Json -Compress) -eq ($afterCopy | ConvertTo-Json -Compress)
    $startupPreserved=($beforeStartup | ConvertTo-Json -Compress -Depth 5) -eq ((Snapshot-Key $startup) | ConvertTo-Json -Compress -Depth 5)
    $location=$registry.OpenSubKey($registration)
    try { $installedAt=$location.GetValue('InstallLocation') } finally { $location.Dispose() }
    $success=$copyPreserved -and $startupPreserved -and ([IO.Path]::GetFullPath($installedAt).TrimEnd('\') -eq $stage.TrimEnd('\'))
    if (-not $success) { throw 'Copied-profile upgrade preservation failed.' }
} finally {
    Restore-Key $registration $beforeRegistry
    foreach ($item in $shortcutSnapshots) {
        if ($null -eq $item.bytes) { if (Test-Path -LiteralPath $item.path) { Remove-Item -LiteralPath $item.path } }
        else { [IO.File]::WriteAllBytes($item.path,$item.bytes) }
    }
    $restored=($beforeRegistry | ConvertTo-Json -Compress -Depth 5) -eq ((Snapshot-Key $registration) | ConvertTo-Json -Compress -Depth 5)
    $livePreserved=($beforeLive | ConvertTo-Json -Compress) -eq ((Manifest $sourceProfile) | ConvertTo-Json -Compress)
    [ordered]@{timestamp=[DateTimeOffset]::Now;passed=($success -and $restored -and $livePreserved);
        copiedDataFiles=$beforeCopy.Count;copiedDataByteIdentical=$copyPreserved;startupRegistrationPreserved=$startupPreserved;
        liveRegistrationRestored=$restored;liveDataUnchanged=$livePreserved;liveInstallationReplaced=$false;
        stagedDirectory=$stage;candidateVersion=(Get-Item -LiteralPath (Join-Path $stage 'GlassDesk.exe')).VersionInfo.FileVersion;
        baselineInstallerSha256=(Get-FileHash -LiteralPath $BaselineInstaller).Hash;
        candidateInstallerSha256=(Get-FileHash -LiteralPath $CandidateInstaller).Hash} |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'upgrade-report.json') -Encoding utf8
}
if (-not ($success -and $restored -and $livePreserved)) { throw 'Upgrade verification or restoration failed.' }
