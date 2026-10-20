# Real old/new product payloads, using an isolated Inno identity and directory.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$OldPayload,
    [Parameter(Mandatory=$true)][string]$NewPayload,
    [Parameter(Mandatory=$true)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$root = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $root) { throw 'Use a new acceptance directory.' }
$null = New-Item -ItemType Directory -Path $root
$compiler = 'D:\Toolchains\InnoSetup-7.1.0-x64\Compiler\ISCC.exe'
$identity = 'GD.Payload.' + [guid]::NewGuid().ToString('N')
$liveKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\GlassDesk.Independent_is1'
function LiveRegistration {
    Get-ItemProperty -LiteralPath $liveKey | Select-Object InstallLocation,DisplayVersion,UninstallString | ConvertTo-Json -Compress
}
$before = LiveRegistration
$dataBefore = @{}
$liveDirectory = (Get-ItemProperty -LiteralPath $liveKey).InstallLocation
Get-ChildItem -LiteralPath (Join-Path $liveDirectory 'Data') -File -Recurse | ForEach-Object { $dataBefore[$_.FullName] = (Get-FileHash -LiteralPath $_.FullName).Hash }
$iss = (Get-Content -LiteralPath (Join-Path $repo 'packaging/GlassDesk-Capture.iss') -Raw).
    Replace('AppIdValue != "GlassDesk.Independent"', 'AppIdValue != "' + $identity + '"').
    Replace('SetupIconFile=..\src\GlassDesk.App\Assets\GlassDesk.ico', 'SetupIconFile=' + (Join-Path $repo 'src/GlassDesk.App/Assets/GlassDesk.ico')).
    Replace('Name: "{autoprograms}\GlassDesk"', 'Name: "{autoprograms}\' + $identity + '"').
    Replace('Name: "{autodesktop}\GlassDesk"', 'Name: "{autodesktop}\' + $identity + '"')
$issPath = Join-Path $root 'isolated.iss'
$iss | Set-Content -LiteralPath $issPath -Encoding utf8
$installed = Join-Path $root 'Installed App'
$prior = $env:GLASSDESK_PACKAGE_STAGE
$checks = @()
$passed = $false
$oldVersion = ([version][Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $OldPayload 'GlassDesk.dll')).FileVersion).ToString(3)
$newVersion = ([version][Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $NewPayload 'GlassDesk.dll')).FileVersion).ToString(3)
function RunInstaller([string]$File, [string]$Log) {
    $info = [Diagnostics.ProcessStartInfo]::new($File)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    foreach ($a in @('/SP-','/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/DIR=' + $installed),('/LOG=' + $Log))) { $info.ArgumentList.Add($a) }
    $p = [Diagnostics.Process]::Start($info)
    if (-not $p.WaitForExit(60000)) { throw 'Isolated installer timed out.' }
    try { if ($p.ExitCode -ne 0) { throw "Isolated installer failed: $($p.ExitCode)" } } finally { $p.Dispose() }
}
try {
    foreach ($entry in @(@{version=$oldVersion; payload=$OldPayload}, @{version=$newVersion; payload=$NewPayload})) {
        $env:GLASSDESK_PACKAGE_STAGE = (Resolve-Path -LiteralPath $entry.payload).Path
        & $compiler "/DAppIdValue=$identity" "/DProductVersion=$($entry.version)" "/DFileVersion=$($entry.version).0" "/DPackageBaseName=Test-$($entry.version)" "/O$root" $issPath *> (Join-Path $root "compile-$($entry.version).log")
        if ($LASTEXITCODE -ne 0) { throw 'Isolated payload compilation failed.' }
    }
    RunInstaller (Join-Path $root "Test-$oldVersion-Setup.exe") (Join-Path $root 'install-old.log')
    if ([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $installed 'GlassDesk.dll')).FileVersion -ne "$oldVersion.0") { throw 'Old product was not installed.' }
    $checks += "Actual $oldVersion payload installs in the isolated directory"
    $data = Join-Path $installed 'Data'
    $null = New-Item -ItemType Directory -Path (Join-Path $data 'icons') -Force
    foreach ($name in @('settings.json','launcher-layout.json','library.json')) { '{"existingUserMarker":"keep"}' | Set-Content -LiteralPath (Join-Path $data $name) -Encoding utf8 }
    [IO.File]::WriteAllBytes((Join-Path $data 'icons/custom.png'), [byte[]]@(137,80,78,71,13,10,26,10,42))
    $ownedData = @{}
    Get-ChildItem -LiteralPath $data -File -Recurse | ForEach-Object { $ownedData[$_.FullName] = (Get-FileHash -LiteralPath $_.FullName).Hash }
    RunInstaller (Join-Path $root "Test-$newVersion-Setup.exe") (Join-Path $root 'install-update.log')
    foreach ($file in Get-ChildItem -LiteralPath $NewPayload -File -Recurse) {
        $relative = [IO.Path]::GetRelativePath((Resolve-Path -LiteralPath $NewPayload).Path, $file.FullName)
        if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath (Join-Path $installed $relative)).Hash) { throw "Installed bytes differ: $relative" }
    }
    foreach ($name in $ownedData.Keys) { if ((Get-FileHash -LiteralPath $name).Hash -ne $ownedData[$name]) { throw 'Upgrade modified existing data.' } }
    $checks += "$newVersion installed product files exactly match the supplied candidate payload"
    $checks += 'Same-directory upgrade preserves settings, launcher layout, library and icon-cache bytes'
    $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\' + $identity + '_is1'
    if ((Get-ItemProperty -LiteralPath $key).DisplayVersion -ne $newVersion) { throw 'Version registration did not advance.' }
    $checks += "Isolated install registration advances to $newVersion"
    $passed = $true
} finally {
    $env:GLASSDESK_PACKAGE_STAGE = $prior
    $uninstaller = Join-Path $installed 'unins000.exe'
    if (Test-Path -LiteralPath $uninstaller) {
        $cleanup = [Diagnostics.ProcessStartInfo]::new($uninstaller)
        $cleanup.UseShellExecute = $false
        $cleanup.CreateNoWindow = $true
        foreach ($a in @('/VERYSILENT','/NORESTART','/SUPPRESSMSGBOXES')) { $cleanup.ArgumentList.Add($a) }
        $p = [Diagnostics.Process]::Start($cleanup)
        try { if (-not $p.WaitForExit(60000) -or $p.ExitCode -ne 0) { throw 'Isolated uninstall failed.' } } finally { $p.Dispose() }
    }
    $unchanged = $before -eq (LiveRegistration)
    foreach ($name in $dataBefore.Keys) { if ((Get-FileHash -LiteralPath $name).Hash -ne $dataBefore[$name]) { $unchanged = $false } }
    $cleaned = -not (Test-Path -LiteralPath ('HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\' + $identity + '_is1'))
    [ordered]@{passed=($passed -and $unchanged -and $cleaned); checks=$checks; liveInstallationAndDataUnchanged=$unchanged; testRegistrationRemoved=$cleaned; fullProductRestartTested=$false; note='Full payload install/upgrade uses isolated registration. Update-mode restart is covered separately by the controlled production-script probe.'} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $root 'installer-report.json') -Encoding utf8
    if (-not $unchanged -or -not $cleaned) { throw 'User isolation or test cleanup failed.' }
}
Write-Output "PASS: real $oldVersion to $newVersion payload upgrade, data retention and isolation"
