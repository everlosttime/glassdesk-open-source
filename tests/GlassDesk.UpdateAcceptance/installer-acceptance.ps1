# Exercise the production Inno script with a dedicated AppId and a controlled probe,
# never the user's installed GlassDesk, shortcuts, registration, or data.
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$root = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $root) { throw 'Acceptance output must be new.' }
$null = New-Item -ItemType Directory -Path $root
$dotnet = 'D:\dotnet10\dotnet.exe'
$compiler = 'D:\Toolchains\InnoSetup-7.1.0-x64\Compiler\ISCC.exe'
$identity = 'GD.Update.' + [guid]::NewGuid().ToString('N')
$liveKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\GlassDesk.Independent_is1'
$before = if (Test-Path -LiteralPath $liveKey) { (Get-ItemProperty -LiteralPath $liveKey | Select-Object InstallLocation,DisplayVersion,UninstallString | ConvertTo-Json -Compress) } else { 'absent' }
$stub = Join-Path $root 'probe'
$null = New-Item -ItemType Directory -Path $stub
@'
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><AssemblyName>GlassDesk</AssemblyName><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>
'@ | Set-Content -LiteralPath (Join-Path $stub 'Probe.csproj') -Encoding utf8
@'
using System.Reflection;
using System.Text.Json;
if (args.SequenceEqual(new[] { "--autostart" }))
    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "restart-marker.json"), JsonSerializer.Serialize(new { args, version = Assembly.GetExecutingAssembly().GetName().Version!.ToString() }));
else Environment.Exit(2);
'@ | Set-Content -LiteralPath (Join-Path $stub 'Program.cs') -Encoding utf8
$script = Get-Content -Raw -LiteralPath (Join-Path $repo 'packaging/GlassDesk-Capture.iss')
$script = $script.Replace('AppIdValue != "GlassDesk.Independent"', 'AppIdValue != "' + $identity + '"')
$script = $script.Replace('SetupIconFile=..\src\GlassDesk.App\Assets\GlassDesk.ico', 'SetupIconFile=' + (Join-Path $repo 'src/GlassDesk.App/Assets/GlassDesk.ico'))
$script = $script.Replace('Name: "{autoprograms}\GlassDesk"', 'Name: "{autoprograms}\' + $identity + '"')
$script = $script.Replace('Name: "{autodesktop}\GlassDesk"', 'Name: "{autodesktop}\' + $identity + '"')
$iss = Join-Path $root 'test.iss'
$script | Set-Content -LiteralPath $iss -Encoding utf8
$installed = Join-Path $root 'Installed App'
$priorStage = $env:GLASSDESK_PACKAGE_STAGE
$priorDotnet = $env:DOTNET_ROOT
$priorDotnetX64 = $env:DOTNET_ROOT_X64
$checks = @()
$passed = $false
try {
    $env:DOTNET_ROOT = 'D:\dotnet10'
    $env:DOTNET_ROOT_X64 = 'D:\dotnet10'
    foreach ($version in @('0.2.8','0.2.9')) {
        $stage = Join-Path $root "stage-$version"
        & $dotnet publish (Join-Path $stub 'Probe.csproj') -c Release -r win-x64 --self-contained false "-p:Version=$version" "-p:FileVersion=$version.0" "-p:AssemblyVersion=$version.0" -o $stage *> (Join-Path $root "publish-$version.log")
        if ($LASTEXITCODE -ne 0) { throw 'Probe publish failed.' }
        Copy-Item -LiteralPath (Join-Path $repo 'src/GlassDesk.App/Assets/GlassDesk.ico') -Destination (Join-Path $stage 'GlassDesk.ico')
        $null = New-Item -ItemType Directory -Path (Join-Path $stage 'Data')
        'Must not overwrite user data' | Set-Content -LiteralPath (Join-Path $stage 'Data/settings.json')
        $env:GLASSDESK_PACKAGE_STAGE = $stage
        & $compiler "/DAppIdValue=$identity" "/DProductVersion=$version" "/DFileVersion=$version.0" "/DPackageBaseName=Test-$version" "/O$root" $iss *> (Join-Path $root "compile-$version.log")
        if ($LASTEXITCODE -ne 0) { throw 'Probe installer compile failed.' }
    }
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = Join-Path $root 'Test-0.2.8-Setup.exe'
    $info.Arguments = '/SP- /VERYSILENT /NORESTART /DIR="' + $installed + '" /LOG="' + (Join-Path $root 'install-old.log') + '"'
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $process = [Diagnostics.Process]::Start($info)
    if (-not $process.WaitForExit(60000)) { throw 'Old installer timed out.' }
    if ($process.ExitCode -ne 0) { throw "Old installer failed: $($process.ExitCode)" }
    $process.Dispose()
    if (@(Get-ChildItem -LiteralPath (Join-Path $installed 'Data') -File -Recurse -ErrorAction SilentlyContinue).Count -ne 0) { throw 'Installer unexpectedly shipped user Data.' }
    if (Test-Path -LiteralPath (Join-Path $installed 'restart-marker.json')) { throw 'Normal installation unexpectedly invoked update restart.' }
    $checks += 'Initial isolated install succeeds, excludes Data, and does not invoke update restart'
    $data = Join-Path $installed 'Data'
    $null = New-Item -ItemType Directory -Path $data -Force
    '{"marker":"existing user settings"}' | Set-Content -LiteralPath (Join-Path $data 'settings.json')
    [IO.File]::WriteAllBytes((Join-Path $data 'custom-icon.png'), [byte[]]@(0,1,2,42,255))
    $dataBefore = @{}
    Get-ChildItem -LiteralPath $data -File | ForEach-Object { $dataBefore[$_.Name] = (Get-FileHash -LiteralPath $_.FullName).Hash }
    $oldHash = (Get-FileHash -LiteralPath (Join-Path $installed 'GlassDesk.dll')).Hash
    $info.FileName = Join-Path $root 'Test-0.2.9-Setup.exe'
    $info.Arguments = '/SP- /SILENT /NORESTART /GLASSDESKUPDATE=1 /DIR="' + $installed + '" /LOG="' + (Join-Path $root 'install-update.log') + '"'
    $process = [Diagnostics.Process]::Start($info)
    if (-not $process.WaitForExit(60000)) { throw 'Update installer timed out.' }
    if ($process.ExitCode -ne 0) { throw "Update installer failed: $($process.ExitCode)" }
    $process.Dispose()
    if ((Get-FileHash -LiteralPath (Join-Path $installed 'GlassDesk.dll')).Hash -eq $oldHash) { throw 'Update did not replace application binary.' }
    foreach ($name in $dataBefore.Keys) {
        if ((Get-FileHash -LiteralPath (Join-Path $data $name)).Hash -ne $dataBefore[$name]) { throw "Update modified user file: $name" }
    }
    $checks += 'Same-directory update replaces the binary and preserves existing settings and custom icon bytes'
    $shortcutPath = Join-Path ([Environment]::GetFolderPath('Programs')) ($identity + '.lnk')
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $expectedIcon = Join-Path $installed 'GlassDesk-0.2.9.ico'
    if ($shortcut.IconLocation -ne ($expectedIcon + ',0')) { throw "Shortcut still uses a cached/default icon: $($shortcut.IconLocation)" }
    if ($shortcut.TargetPath -ne (Join-Path $installed 'GlassDesk.exe')) { throw 'Shortcut target changed.' }
    if ((Get-FileHash -LiteralPath $expectedIcon).Hash -ne (Get-FileHash -LiteralPath (Join-Path $repo 'src/GlassDesk.App/Assets/GlassDesk.ico')).Hash) { throw 'Installed shortcut icon differs from approved artwork.' }
    $checks += 'Upgraded shortcut references the versioned approved icon and keeps the executable target'
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    $marker = Join-Path $installed 'restart-marker.json'
    while (-not (Test-Path -LiteralPath $marker) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
    $restart = Get-Content -Raw -LiteralPath $marker | ConvertFrom-Json
    if ($restart.version -ne '0.2.9.0' -or ($restart.args -join ',') -ne '--autostart') { throw 'Restart did not invoke the updated binary with the intended argument.' }
    $checks += 'Successful installer restarts the new 0.2.9 binary with --autostart'
    $passed = $true
} finally {
    $env:GLASSDESK_PACKAGE_STAGE = $priorStage
    $env:DOTNET_ROOT = $priorDotnet
    $env:DOTNET_ROOT_X64 = $priorDotnetX64
    $uninstaller = Join-Path $installed 'unins000.exe'
    if (Test-Path -LiteralPath $uninstaller) {
        $cleanup = New-Object Diagnostics.ProcessStartInfo
        $cleanup.FileName = $uninstaller
        $cleanup.Arguments = '/VERYSILENT /NORESTART /SUPPRESSMSGBOXES'
        $cleanup.UseShellExecute = $false
        $cleanup.CreateNoWindow = $true
        $owned = [Diagnostics.Process]::Start($cleanup)
        if (-not $owned.WaitForExit(60000)) { throw 'Controlled uninstaller timed out.' }
        $owned.Dispose()
    }
    $after = if (Test-Path -LiteralPath $liveKey) { (Get-ItemProperty -LiteralPath $liveKey | Select-Object InstallLocation,DisplayVersion,UninstallString | ConvertTo-Json -Compress) } else { 'absent' }
    $untouched = $before -eq $after
    $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\' + $identity + '_is1'
    $cleaned = -not (Test-Path -LiteralPath $key)
    [ordered]@{passed=($passed -and $untouched -and $cleaned); checks=$checks; liveRegistrationUnchanged=$untouched; testRegistrationRemoved=$cleaned; identity=$identity} |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $root 'installer-report.json') -Encoding utf8
    if (-not $untouched -or -not $cleaned) { throw 'Acceptance cleanup or live registration isolation failed.' }
}
Write-Output 'PASS: isolated Inno install, update, data retention, restart, and cleanup'
