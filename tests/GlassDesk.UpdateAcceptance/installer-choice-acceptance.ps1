# Black-box wizard tests with a unique AppId, shortcuts and directories.
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$root = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $root) { throw 'Use a fresh acceptance output directory.' }
$null = New-Item -ItemType Directory -Path $root
$identity = 'GD.Choice.' + [guid]::NewGuid().ToString('N')
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\' + $identity + '_is1'
$liveKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\GlassDesk.Independent_is1'
function LiveRegistration {
    if (Test-Path -LiteralPath $liveKey) {
        Get-ItemProperty -LiteralPath $liveKey | Select-Object InstallLocation,DisplayVersion,UninstallString | ConvertTo-Json -Compress
    } else { 'absent' }
}
$before = LiveRegistration
$compiler = 'D:\Toolchains\InnoSetup-7.1.0-x64\Compiler\ISCC.exe'
$old = Join-Path $root '原安装位置'
$custom = Join-Path $root '自定义位置'
$default = Join-Path $root 'Default App'
$stage = Join-Path $root 'stage'
$null = New-Item -ItemType Directory -Path (Join-Path $stage 'Data')
'Never ship personal data' | Set-Content -LiteralPath (Join-Path $stage 'Data/settings.json')
Copy-Item -LiteralPath (Join-Path $repo 'src/GlassDesk.App/Assets/GlassDesk.ico') -Destination (Join-Path $stage 'GlassDesk.ico')
$script = (Get-Content -Raw -LiteralPath (Join-Path $repo 'packaging/GlassDesk-Capture.iss')).
    Replace('AppIdValue != "GlassDesk.Independent"', 'AppIdValue != "' + $identity + '"').
    Replace('AppName=GlassDesk', 'AppName=' + $identity).
    Replace('AppVerName=GlassDesk ', 'AppVerName=' + $identity + ' ').
    Replace('DefaultDirName={localappdata}\Programs\GlassDesk', 'DefaultDirName=' + $default).
    Replace('SetupIconFile=..\src\GlassDesk.App\Assets\GlassDesk.ico', 'SetupIconFile=' + (Join-Path $repo 'src/GlassDesk.App/Assets/GlassDesk.ico')).
    Replace('Name: "{autoprograms}\GlassDesk"', 'Name: "{autoprograms}\' + $identity + '"').
    Replace('Name: "{autodesktop}\GlassDesk"', 'Name: "{autodesktop}\' + $identity + '"')
$iss = Join-Path $root 'choice.iss'
$script | Set-Content -LiteralPath $iss -Encoding utf8
$upgradeInclude = Join-Path $repo 'packaging/InstallerUpgrade.iss'
if (Test-Path -LiteralPath $upgradeInclude) {
    # Keep even the custom option's initial suggestion inside the test sandbox.
    (Get-Content -Raw -LiteralPath $upgradeInclude).Replace(
        "'{localappdata}\Programs\GlassDesk'", ("'" + $default + "'")) |
        Set-Content -LiteralPath (Join-Path $root 'InstallerUpgrade.iss') -Encoding utf8
}
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class InstallerWizardDriver {
    public delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr p);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr h, EnumProc callback, IntPtr p);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder text, int count);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    public struct Rect { public int Left,Top,Right,Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out Rect rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll", CharSet=CharSet.Unicode, EntryPoint="SendMessageW")] public static extern IntPtr SetTextMessage(IntPtr h, uint msg, IntPtr w, string text);
    [DllImport("user32.dll", CharSet=CharSet.Unicode, EntryPoint="SendMessageW")] public static extern IntPtr GetTextMessage(IntPtr h, uint msg, IntPtr w, StringBuilder text);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    public static string Text(IntPtr h) { var b = new StringBuilder(2048); GetTextMessage(h,0xD,(IntPtr)b.Capacity,b); return b.ToString(); }
    public static string Class(IntPtr h) { var b = new StringBuilder(256); GetClassName(h,b,b.Capacity); return b.ToString(); }
    public static IntPtr Window(string title) {
        IntPtr result = IntPtr.Zero;
        EnumWindows((h,p) => { if (IsWindowVisible(h) && Class(h)=="TWizardForm" && Text(h).Contains(title)) result=h; return true; }, IntPtr.Zero);
        return result;
    }
    public static IntPtr[] Controls(IntPtr window) {
        var result = new List<IntPtr>();
        EnumChildWindows(window,(h,p) => { if(IsWindowVisible(h)) result.Add(h); return true; },IntPtr.Zero);
        return result.ToArray();
    }
    public static void Click(IntPtr control) { PostMessage(control,0xF5,IntPtr.Zero,IntPtr.Zero); }
    public static bool Checked(IntPtr control) { return SendMessage(control,0xF0,IntPtr.Zero,IntPtr.Zero).ToInt64()==1; }
}
'@
function Controls { @([InstallerWizardDriver]::Controls($script:window)) }
function FindControl([string]$Prefix, [switch]$Button) {
    @(Controls | Where-Object {
        [InstallerWizardDriver]::Text($_).Replace('&','').StartsWith($Prefix) -and
        (-not $Button -or [InstallerWizardDriver]::Class($_).EndsWith('Button'))
    }) | Select-Object -First 1
}
function WaitControl([string]$Prefix, [switch]$Button) {
    $until = [DateTime]::UtcNow.AddSeconds(12)
    do {
        $control = FindControl $Prefix -Button:$Button
        if ($control) { return $control }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $until)
    $visible = @(Controls | ForEach-Object { [InstallerWizardDriver]::Class($_) + ': ' + [InstallerWizardDriver]::Text($_) }) -join "`n"
    throw "Expected wizard control '$Prefix'. Visible controls:`n$visible"
}
function Click([string]$Prefix) {
    [InstallerWizardDriver]::Click((WaitControl $Prefix -Button))
    Start-Sleep -Milliseconds 250
}
function StartWizard([string]$Version, [string]$Directory = '') {
    $info = [Diagnostics.ProcessStartInfo]::new((Join-Path $root "Test-$Version-Setup.exe"))
    $info.UseShellExecute = $false
    foreach ($a in @('/SP-','/NORESTART',('/LOG=' + (Join-Path $root ('wizard-' + [guid]::NewGuid().ToString('N') + '.log'))))) { $info.ArgumentList.Add($a) }
    if ($Directory) { $info.ArgumentList.Add('/DIR=' + $Directory) }
    $script:process = [Diagnostics.Process]::Start($info)
    $until = [DateTime]::UtcNow.AddSeconds(12)
    do {
        $script:window = [InstallerWizardDriver]::Window($identity)
        if ($script:window -ne [IntPtr]::Zero) { return }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $until)
    throw 'Installer wizard did not open.'
}
function CompleteInstall {
    for ($i=0; $i -lt 6; $i++) {
        $install = @(Controls | Where-Object {
            [InstallerWizardDriver]::Class($_) -eq 'TNewButton' -and [InstallerWizardDriver]::Text($_).StartsWith('安装')
        }) | Select-Object -First 1
        if ($install) { [InstallerWizardDriver]::Click($install); break }
        Click '下一步'
    }
    Click '完成'
    if (-not $script:process.WaitForExit(15000) -or $script:process.ExitCode -ne 0) { throw 'Interactive installation failed.' }
    $script:process.Dispose()
    $script:process = $null
}
function DirectoryEdit {
    @(Controls | Where-Object { [InstallerWizardDriver]::Class($_) -match 'Edit$' }) | Select-Object -First 1
}
function SetDirectory([string]$Directory) {
    $edit = DirectoryEdit
    if (-not $edit) { throw 'Destination edit is missing.' }
    [InstallerWizardDriver]::SetTextMessage($edit, 0xC, [IntPtr]::Zero, $Directory) | Out-Null
    if ([InstallerWizardDriver]::Text($edit) -ne $Directory) { throw 'Failed to enter the test destination.' }
}
function CloseTestWizard {
    $script:process.Kill($true)
    $script:process.WaitForExit()
    $script:process.Dispose()
    $script:process = $null
}
function SaveWizardPreview {
    $rect = [InstallerWizardDriver+Rect]::new()
    [InstallerWizardDriver]::GetWindowRect($script:window, [ref]$rect) | Out-Null
    $bitmap = [Drawing.Bitmap]::new(($rect.Right - $rect.Left), ($rect.Bottom - $rect.Top))
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $hdc = $graphics.GetHdc()
    try {
        if (-not [InstallerWizardDriver]::PrintWindow($script:window, $hdc, 2)) { throw 'Could not capture the test wizard.' }
    } finally { $graphics.ReleaseHdc($hdc); $graphics.Dispose() }
    try { $bitmap.Save((Join-Path $root 'upgrade-choice.png'), [Drawing.Imaging.ImageFormat]::Png) } finally { $bitmap.Dispose() }
}
$priorStage = $env:GLASSDESK_PACKAGE_STAGE
$checks = @()
$passed = $false
try {
    $env:GLASSDESK_PACKAGE_STAGE = $stage
    foreach ($version in @('0.0.1','0.0.2')) {
        # A controlled file, never executed by a normal manual installation.
        "Binary marker $version" | Set-Content -LiteralPath (Join-Path $stage 'GlassDesk.exe')
        & $compiler "/I$(Join-Path $repo 'packaging')" "/DAppIdValue=$identity" "/DProductVersion=$version" "/DFileVersion=$version.0" "/DPackageBaseName=Test-$version" "/O$root" $iss *> (Join-Path $root "compile-$version.log")
        if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed: compile-$version.log" }
    }
    StartWizard '0.0.1' $old
    $null = WaitControl '选择目标位置'
    if (FindControl '覆盖升级') { throw 'First install unexpectedly shows an upgrade choice.' }
    CompleteInstall
    if (Test-Path -LiteralPath (Join-Path $old 'Data/settings.json')) { throw 'Installer shipped personal data.' }
    $checks += 'First install keeps the ordinary destination page and excludes Data'
    $null = New-Item -ItemType Directory -Path (Join-Path $old 'Data') -Force
    '{"userSetting":true}' | Set-Content -LiteralPath (Join-Path $old 'Data/settings.json')
    [IO.File]::WriteAllBytes((Join-Path $old 'Data/custom-icon.png'), [byte[]]@(1,2,3,255))
    $dataHashes = @{}
    Get-ChildItem -LiteralPath (Join-Path $old 'Data') -File | ForEach-Object { $dataHashes[$_.FullName] = (Get-FileHash -LiteralPath $_.FullName).Hash }
    StartWizard '0.0.2'
    $upgrade = WaitControl '覆盖升级'
    if (-not [InstallerWizardDriver]::Checked($upgrade)) { throw 'Overwrite must be the default choice.' }
    if (-not (Controls | Where-Object { [InstallerWizardDriver]::Text($_).Contains($old) })) { throw 'Existing directory was not shown.' }
    SaveWizardPreview
    Click '下一步'
    if (DirectoryEdit) { throw 'Overwrite unexpectedly asks the user to locate the old directory.' }
    Click '上一步'
    Click '自定义安装位置'
    Click '下一步'
    $null = WaitControl '选择目标位置'
    SetDirectory $custom
    Click '上一步'
    Click '覆盖升级'
    Click '下一步'
    CompleteInstall
    if ((Get-Content -Raw -LiteralPath (Join-Path $old 'GlassDesk.exe')).Trim() -ne 'Binary marker 0.0.2') { throw 'Overwrite did not use the original directory after returning from custom selection.' }
    foreach ($path in $dataHashes.Keys) { if ((Get-FileHash -LiteralPath $path).Hash -ne $dataHashes[$path]) { throw "Overwrite changed user data: $path" } }
    $checks += 'Upgrade shows the old path, defaults to overwrite, skips browsing, survives Back/custom/Back and preserves data'
    $originalBinaryHash = (Get-FileHash -LiteralPath (Join-Path $old 'GlassDesk.exe')).Hash
    StartWizard '0.0.2'
    Click '自定义安装位置'
    Click '下一步'
    $null = WaitControl '选择目标位置'
    SetDirectory $custom
    CompleteInstall
    if (-not (Test-Path -LiteralPath (Join-Path $custom 'GlassDesk.exe'))) { throw 'Custom destination was not used.' }
    if ([IO.Path]::TrimEndingDirectorySeparator((Get-ItemProperty -LiteralPath $key).InstallLocation) -ne $custom) { throw 'Registration does not point to the custom directory.' }
    if ((Get-FileHash -LiteralPath (Join-Path $old 'GlassDesk.exe')).Hash -ne $originalBinaryHash) { throw 'Custom installation changed the old binary.' }
    foreach ($path in $dataHashes.Keys) { if ((Get-FileHash -LiteralPath $path).Hash -ne $dataHashes[$path]) { throw 'Custom installation changed old user data.' } }
    $checks += 'Custom install uses the chosen directory, updates registration and leaves the old files and data intact'
    Remove-ItemProperty -LiteralPath $key -Name InstallLocation
    StartWizard '0.0.2'
    $null = WaitControl '覆盖升级'
    if (-not (Controls | Where-Object { [InstallerWizardDriver]::Text($_).Contains($custom) })) { throw 'Legacy Inno path was not detected.' }
    CloseTestWizard
    $checks += 'Earlier installations with only the built-in Inno path are detected'
    Set-ItemProperty -LiteralPath $key -Name 'Inno Setup: App Path' -Value (Join-Path $root 'Missing App')
    StartWizard '0.0.2'
    $null = WaitControl '选择目标位置'
    if (FindControl '覆盖升级') { throw 'A stale registration was mistaken for an installed app.' }
    CloseTestWizard
    $checks += 'Stale registration without GlassDesk.exe keeps the first-install flow'
    $passed = $true
} finally {
    $env:GLASSDESK_PACKAGE_STAGE = $priorStage
    if ($script:process -and -not $script:process.HasExited) {
        # Only terminate the owned test installer and its children.
        taskkill /PID $script:process.Id /T /F | Out-Null
    }
    foreach ($directory in @($old,$custom)) {
        $uninstaller = Join-Path $directory 'unins000.exe'
        if (Test-Path -LiteralPath $uninstaller) {
            $info = [Diagnostics.ProcessStartInfo]::new($uninstaller)
            $info.UseShellExecute = $false
            foreach ($a in @('/VERYSILENT','/NORESTART','/SUPPRESSMSGBOXES')) { $info.ArgumentList.Add($a) }
            $p = [Diagnostics.Process]::Start($info)
            if (-not $p.WaitForExit(30000)) { throw 'Test uninstaller timed out.' }
            $p.Dispose()
        }
    }
    $unchanged = $before -eq (LiveRegistration)
    $cleaned = -not (Test-Path -LiteralPath $key)
    [ordered]@{passed=($passed -and $unchanged -and $cleaned); checks=$checks; liveRegistrationUnchanged=$unchanged; testRegistrationRemoved=$cleaned; identity=$identity} |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $root 'installer-choice-report.json') -Encoding utf8
    if (-not $unchanged -or -not $cleaned) { throw 'Test isolation or cleanup failed.' }
}
Write-Output 'PASS: interactive first install, overwrite upgrade, custom directory, Back navigation and data preservation'
