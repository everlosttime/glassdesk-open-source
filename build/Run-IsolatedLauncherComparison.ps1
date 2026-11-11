param(
    [Parameter(Mandatory=$true)][string]$InstalledExecutable,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [int]$Cycles=3,
    [int]$Batches=1,
    [string[]]$Candidates=@('grid','paint','shadow'),
    [switch]$RenderingControl,
    [switch]$DiagnosticAblations,
    [string]$NativeShadowDll='',
    [string]$AcceptanceDirectory='',
    [string]$CaptureExecutable='',
    [switch]$PortableSmoke,
    [switch]$ExpectNativeFallback,
    [switch]$PortableRegressions,
    [switch]$PortableEntrySmoke
)
$ErrorActionPreference='Stop'
$installed=[IO.Path]::GetFullPath($InstalledExecutable)
$output=[IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Use a fresh comparison directory.' }
$running=@(Get-CimInstance Win32_Process -Filter "Name='GlassDesk.exe'" | Where-Object { $_.ExecutablePath -eq $installed -and $_.CommandLine -notmatch '--glass-backdrop-worker' })
if ($running.Count -ne 1) { throw 'Expected exactly one explicitly named installed host.' }
$owner=$running[0]
$hash=(Get-FileHash -LiteralPath $installed).Hash
$data=Join-Path (Split-Path $installed -Parent) 'Data'
$before=@{}
foreach ($name in @('settings.json','apps.json','dock-layout.json','launcher-layout.json')) { $before[$name]=(Get-FileHash -LiteralPath (Join-Path $data $name)).Hash }
[IO.Directory]::CreateDirectory($output) | Out-Null
$receipt=[ordered]@{hostPid=$owner.ProcessId;executable=$installed;originalSha256=$hash;dataHashesBefore=$before;startedUtc=[DateTimeOffset]::UtcNow;restored=$false}
$exited=$false
try {
    & $installed --exit
    for ($i=0;$i -lt 120;$i++) {
        if (-not (Get-Process -Id $owner.ProcessId -ErrorAction SilentlyContinue)) { $exited=$true;break }
        Start-Sleep -Milliseconds 100
    }
    if (-not $exited) { throw 'Installed host did not exit; no forced host termination performed.' }
    $helpers=@(Get-CimInstance Win32_Process -Filter "Name='GlassDesk.exe'" | Where-Object {
        $_.ExecutablePath -eq $installed -and $_.CommandLine.Contains("--glass-backdrop-worker $($owner.ProcessId)")
    })
    foreach ($helper in $helpers) { Stop-Process -Id $helper.ProcessId }
    $receipt['retiredHelpers']=@($helpers | ForEach-Object ProcessId)
    if ($NativeShadowDll) { $env:GLASSDESK_NATIVE_RENDER_DLL=[IO.Path]::GetFullPath($NativeShadowDll) }
    if ($DiagnosticAblations) {
        $workspace=Split-Path $PSScriptRoot -Parent
        $env:DOTNET_ROOT='D:\dotnet10'
        $capture=Join-Path $workspace 'src/GlassDesk.App/bin/Release/net10.0-windows10.0.26100.0/GlassDesk.exe'
        foreach ($mode in @('none','shadow','backdrop','content','clips','freeze','freeze-no-scene','all')) {
            foreach ($material in @('FrostedGlass','AndroidLiquidGlass')) {
                $report=Join-Path $output ($mode+'-'+$material+'/report.json')
                & 'D:\dotnet10\dotnet.exe' (Join-Path $workspace 'tests/GlassDesk.LauncherAcceptance/bin/Release/net10.0-windows10.0.26100.0/GlassDesk.LauncherAcceptance.dll') --launcher-open-close --cycles $Cycles --ablation $mode --material $material --capture-exe $capture --report $report
                if ($LASTEXITCODE -ne 0) { throw "Diagnostic ablation failed: $mode $material" }
                $fixture=Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
                Get-CimInstance Win32_Process -Filter "Name='GlassDesk.exe'" | Where-Object {
                    $_.ExecutablePath -eq $capture -and $_.CommandLine.Contains("--glass-backdrop-worker $($fixture.processId)")
                } | ForEach-Object { Stop-Process -Id $_.ProcessId }
            }
        }
    }
    if ($RenderingControl) {
        $workspace=Split-Path $PSScriptRoot -Parent
        $env:DOTNET_ROOT='D:\dotnet10'
        & 'D:\dotnet10\dotnet.exe' (Join-Path $workspace 'tests/GlassDesk.LauncherAcceptance/bin/Release/net10.0-windows10.0.26100.0/GlassDesk.LauncherAcceptance.dll') --launcher-rendering-control --report (Join-Path $output 'rendering-control.json')
        if ($LASTEXITCODE -ne 0) { throw 'Bare WPF control failed.' }
    }
    foreach ($candidate in $Candidates) {
        & (Join-Path $PSScriptRoot 'Measure-LauncherOpenClose.ps1') -Candidate $candidate -Cycles $Cycles -Batches $Batches -OutputDirectory (Join-Path $output $candidate) -AcceptanceDirectory $AcceptanceDirectory -CaptureExecutable $CaptureExecutable
    }
    if ($PortableSmoke) {
        if (-not $AcceptanceDirectory) { throw 'Portable smoke requires an explicit staged acceptance directory.' }
        $smokeArgs=@('--full-host-launcher-smoke','--report',(Join-Path $output 'portable-smoke.json'))
        if ($ExpectNativeFallback) { $smokeArgs+='--expect-native-shadow-fallback' }
        $env:GLASSDESK_LAUNCHER_NATIVE_SHADOW='1'
        $env:DOTNET_ROOT='D:\dotnet10'
        & (Join-Path $AcceptanceDirectory 'GlassDesk.LauncherAcceptance.exe') @smokeArgs
        if ($LASTEXITCODE -ne 0) { throw 'Portable host smoke failed; see report.' }
    }
    if ($PortableRegressions) {
        if (-not $AcceptanceDirectory -or -not $CaptureExecutable) { throw 'Portable regressions need explicit staged binaries.' }
        $env:GLASSDESK_LAUNCHER_NATIVE_SHADOW='1'
        foreach ($mode in @('plain','high-refresh-off')) {
            $arguments=@('--launcher-open-close','--cycles','3','--material',$(if($mode -eq 'plain'){'PlainGlass'}else{'FrostedGlass'}),
                '--capture-exe',([IO.Path]::GetFullPath($CaptureExecutable)),'--report',(Join-Path $output ($mode+'/report.json')))
            if ($mode -eq 'high-refresh-off') { $arguments+='--high-refresh-off' }
            & (Join-Path $AcceptanceDirectory 'GlassDesk.LauncherAcceptance.exe') @arguments
            if ($LASTEXITCODE -ne 0) { throw "Portable regression failed: $mode" }
        }
    }
    if ($PortableEntrySmoke) {
        if (-not $AcceptanceDirectory) { throw 'Portable entry smoke requires an explicit staged directory.' }
        $exe=Join-Path ([IO.Path]::GetFullPath($AcceptanceDirectory)) 'GlassDesk.exe'
        $entryHost=$null
        try {
            Start-Process -FilePath (Join-Path $AcceptanceDirectory 'Start-Test.cmd') -WindowStyle Hidden
            Start-Sleep -Seconds 4
            $hosts=@(Get-CimInstance Win32_Process -Filter "Name='GlassDesk.exe'" | Where-Object { $_.ExecutablePath -eq $exe -and $_.CommandLine -notmatch '--glass-backdrop-worker' })
            if ($hosts.Count -ne 1) { throw 'Portable CMD did not start exactly one staged host.' }
            $entryHost=Get-Process -Id $hosts[0].ProcessId
            $entryHost.Refresh()
            $modules=@($entryHost.Modules | Where-Object ModuleName -eq 'GlassDesk.Rendering.Native.dll' | ForEach-Object FileName)
            # Process.MainWindowHandle excludes owned/non-taskbar windows. Inspect
            # this test process's visible top-level HWNDs without changing policy.
            Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class PortableEntryWindows {
    delegate bool EnumProc(IntPtr hwnd,IntPtr state);
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left,Top,Right,Bottom; }
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback,IntPtr state);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint process);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd,out Rect rectangle);
    public static long[] LauncherSized(uint process) {
        var result=new List<long>();
        EnumWindows((hwnd,_)=>{GetWindowThreadProcessId(hwnd,out var owner);
            if(owner==process && IsWindowVisible(hwnd) && GetWindowRect(hwnd,out var r)
                && r.Right-r.Left>=500 && r.Bottom-r.Top>=400)result.Add(hwnd.ToInt64());return true;},IntPtr.Zero);
        return result.ToArray();
    }
}
'@
            $windows=@([PortableEntryWindows]::LauncherSized($entryHost.Id))
            $valid=$windows.Count -gt 0 -and $modules.Count -eq 1 -and $modules[0] -eq (Join-Path ([IO.Path]::GetFullPath($AcceptanceDirectory)) 'GlassDesk.Rendering.Native.dll')
            [pscustomobject]@{pid=$entryHost.Id;executable=$exe;visibleLauncherSizedWindows=$windows;nativeModules=$modules;valid=$valid} |
                ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'portable-entry.json') -Encoding utf8
            if (-not $valid) { throw 'Portable CMD entry did not expose its window and local native module.' }
        }
        finally {
            if ($entryHost) {
                & $exe --exit
                if (-not $entryHost.WaitForExit(10000)) { throw 'Portable test host did not exit normally.' }
            }
        }
    }
}
finally {
    if ($exited) {
        if ((Get-FileHash -LiteralPath $installed).Hash -ne $hash) { throw 'Installed binary changed externally; refusing to start a different binary.' }
        $restored=Start-Process -FilePath $installed -ArgumentList '--autostart' -WindowStyle Hidden -PassThru
        $receipt['restored']=$true; $receipt['restoredPid']=$restored.Id
    }
    $after=@{}
    foreach ($name in $before.Keys) { $after[$name]=(Get-FileHash -LiteralPath (Join-Path $data $name)).Hash }
    $receipt['dataHashesAfter']=$after
    $receipt['coreDataUnchanged']=@($before.Keys | Where-Object { $before[$_] -ne $after[$_] }).Count -eq 0
    $receipt['endedUtc']=[DateTimeOffset]::UtcNow
    $receipt | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $output 'installation-receipt.json') -Encoding utf8
}
