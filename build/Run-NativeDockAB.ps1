[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$ProductDirectory,
    [Parameter(Mandatory=$true)][string]$ProfileDirectory,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [ValidateRange(5,600)][int]$Seconds = 30,
    [ValidateRange(5,120)][int]$WarmupSeconds = 20,
    [string[]]$Backends = @('legacy','native-static'),
    [switch]$AllowLegacyFallback
)
$ErrorActionPreference='Stop'
$product=[IO.Path]::GetFullPath($ProductDirectory).TrimEnd('\')
$profile=[IO.Path]::GetFullPath($ProfileDirectory).TrimEnd('\')
$output=[IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
if (!(Test-Path -LiteralPath (Join-Path $product 'GlassDesk.exe'))) { throw 'Publish the complete product first.' }
if (!(Test-Path -LiteralPath (Join-Path $profile 'settings.json'))) { throw 'A frozen profile is required.' }
if (Test-Path -LiteralPath $output) { throw 'Use a fresh output directory.' }
if ($output.StartsWith($profile+'\',[StringComparison]::OrdinalIgnoreCase) -or
    $output.StartsWith($product+'\',[StringComparison]::OrdinalIgnoreCase) -or
    $profile.StartsWith($output+'\',[StringComparison]::OrdinalIgnoreCase) -or
    $product.StartsWith($output+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Input and output directories must not overlap.' }
if (@(Get-CimInstance Win32_Process -Filter "Name='GlassDesk.exe'").Count) { throw 'Close the existing product cleanly before a controlled run.' }
$null=New-Item -ItemType Directory -Path $output
$app=Join-Path $output 'app'
$null=New-Item -ItemType Directory -Path $app
Get-ChildItem -LiteralPath $product | Where-Object { $_.Name -ne 'Data' } | Copy-Item -Destination $app -Recurse
[IO.File]::WriteAllText((Join-Path $app '.native-static-test-root'),$app)
$config=Get-Content -LiteralPath (Join-Path $profile 'settings.json') -Raw | ConvertFrom-Json
if ($config.appearance.useSystemBackdrop -or $config.appearance.pinBarsToDesktop) { throw 'This stage requires an explicit static wallpaper profile with desktop-only mode off.' }
$manifest=@(Get-ChildItem -LiteralPath $profile -Recurse -File | ForEach-Object {
    [pscustomobject]@{relative=$_.FullName.Substring($profile.Length+1);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
})
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'profile-manifest.json')
& powercfg /getactivescheme | Set-Content -LiteralPath (Join-Path $output 'power-before.txt')
$runs=[Collections.Generic.List[object]]::new()
$index=0
foreach ($backend in $Backends) {
    if ($backend -notin @('legacy','native-static')) { throw 'Unknown backend.' }
    $run=Join-Path $output (('{0:D2}-' -f $index)+$backend)
    $null=New-Item -ItemType Directory -Path $run
    Copy-Item -LiteralPath $profile -Destination (Join-Path $app 'Data') -Recurse
    $start=[Diagnostics.ProcessStartInfo]::new((Join-Path $app 'GlassDesk.exe'))
    $start.UseShellExecute=$false; $start.WindowStyle='Hidden'
    $start.Environment['GLASSDESK_DOCK_RENDERER']=$backend
    $start.Environment['GLASSDESK_RENDER_TEST_DIRECTORY']=$run
    $start.Environment.Remove('GLASSDESK_NATIVE_RENDER_DLL') | Out-Null
    $process=[Diagnostics.Process]::Start($start)
    try {
        Start-Sleep -Seconds $WarmupSeconds
        if ($process.HasExited) { throw 'Product exited before measurement.' }
        & (Join-Path $PSScriptRoot 'Measure-NativeRenderingBaseline.ps1') -RootProcessId $process.Id -OutputDirectory (Join-Path $run 'memory') -Seconds $Seconds
        [IO.File]::WriteAllText((Join-Path $run 'report.request'),'report')
        $deadline=[Diagnostics.Stopwatch]::StartNew()
        while (!(Test-Path -LiteralPath (Join-Path $run 'dock.png')) -and $deadline.Elapsed.TotalSeconds -lt 15) { Start-Sleep -Milliseconds 200 }
        if (!(Test-Path -LiteralPath (Join-Path $run 'product.json'))) { throw 'Full product diagnostic report unavailable.' }
        $report=Get-Content -LiteralPath (Join-Path $run 'product.json') -Raw | ConvertFrom-Json
        if ($report.launcherVisible -or !$report.performance.LauncherSleeping) { throw 'Idle scene changed: Launcher became active.' }
        if ($backend -eq 'legacy' -and $config.appearance.glassMaterial -ne 'PlainGlass' -and $report.dock.source -ne 'Wallpaper') { throw 'Legacy did not present actual wallpaper; comparison is invalid.' }
        if ($backend -eq 'native-static' -and !$report.dock.nativeStatic.ready -and !$AllowLegacyFallback) { throw ('Native material did not activate: '+$report.dock.nativeStatic.state) }
        $runs.Add([pscustomobject]@{backend=$backend;processId=$process.Id;directory=$run;nativeLoaded=$report.nativeLoaded})
    } finally {
        [IO.File]::WriteAllText((Join-Path $run 'shutdown.request'),'shutdown')
        if (!$process.WaitForExit(15000)) { throw 'Product did not shut down cleanly; preserve evidence and inspect it.' }
        $process.Dispose()
        $data=[IO.Path]::GetFullPath((Join-Path $app 'Data'))
        if (!$data.StartsWith($output+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Data retirement path escaped the output workspace.' }
        Move-Item -LiteralPath $data -Destination (Join-Path $run 'data-after')
    }
    $index++
}
foreach ($file in $manifest) {
    if ((Get-FileHash -LiteralPath (Join-Path $profile $file.relative)).Hash -ne $file.sha256) { throw 'Frozen profile changed.' }
}
& powercfg /getactivescheme | Set-Content -LiteralPath (Join-Path $output 'power-after.txt')
[ordered]@{runs=$runs.ToArray();warmupSeconds=$WarmupSeconds;measurementSeconds=$Seconds;
    sameExecutableSha256=(Get-FileHash -LiteralPath (Join-Path $app 'GlassDesk.dll')).Hash;
    sourceProfileUnchanged=$true;diagnosticScreenshotsTakenAfterMemorySamples=$true;
    displayedFpsMeasured=$false;phase1GatePassed=$false} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'ab.json')
