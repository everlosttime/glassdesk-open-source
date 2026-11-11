[CmdletBinding()]
param([switch]$NoDialog)
$ErrorActionPreference = 'Stop'
function Copy-BackdropIncidents {
    param([string]$SourceDirectory, [string]$DestinationDirectory)
    $copied = 0
    $totalBytes = 0L
    if (-not (Test-Path -LiteralPath $SourceDirectory -PathType Container)) { return $copied }
    $allowedRoot = @('timestamp', 'processId', 'applicationVersion', 'qpcFrequency', 'incidentAtTicks',
        'preWindowSeconds', 'postWindowSeconds', 'screenPixelsIncluded', 'windowTitlesIncluded', 'note', 'events')
    $numericFields = @('AtTicks', 'SourceTicks', 'EndTicks', 'BarrierTicks', 'Generation', 'QueueFrames',
        'WindowHandle', 'WindowProcess', 'ErrorCode', 'SurfaceId', 'SceneHash', 'Width', 'Height',
        'NearBlackFraction', 'TransparentFraction', 'MeanChannel', 'WorkerProcess')
    $textFields = @('Stage', 'Reason', 'WindowClass', 'Surface', 'MaterialSource')
    $booleanFields = @('Visible', 'Active', 'Sleeping')
    $allowedEvents = $numericFields + $textFields + $booleanFields + @('Detail')
    $files = Get-ChildItem -LiteralPath $SourceDirectory -File -Filter 'dock-backdrop-*.json' |
        Where-Object { $_.Length -le 2MB -and -not ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) } |
        Sort-Object LastWriteTimeUtc -Descending
    foreach ($file in $files) {
        if ($copied -ge 10) { break }
        try {
            $incident = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
            if ($incident -isnot [pscustomobject] -or $incident.screenPixelsIncluded -isnot [bool] -or
                $incident.screenPixelsIncluded -or $incident.windowTitlesIncluded -isnot [bool] -or
                $incident.windowTitlesIncluded -or @($incident.PSObject.Properties.Name | Where-Object { $_ -notin $allowedRoot }).Count -gt 0 -or
                $null -eq $incident.events -or @($incident.events).Count -gt 1024) { continue }
            $events = @()
            foreach ($event in $incident.events) {
                if ($event -isnot [pscustomobject] -or
                    @($event.PSObject.Properties.Name | Where-Object { $_ -notin $allowedEvents }).Count -gt 0) { throw 'Invalid incident event schema.' }
                $metadata = [ordered]@{}
                foreach ($field in $textFields) {
                    $value = $event.$field
                    if ($null -eq $value) { continue }
                    if ($value -isnot [string] -or $value.Length -gt 128 -or $value -match '[\x00-\x1f\\/:]') { throw 'Invalid incident text metadata.' }
                    $metadata[$field] = $value
                }
                foreach ($field in $numericFields) {
                    $value = $event.$field
                    if ($null -eq $value) { continue }
                    if ($value -isnot [ValueType] -or $value -is [bool] -or $value -is [datetime]) { throw 'Invalid incident numeric metadata.' }
                    $metadata[$field] = $value
                }
                foreach ($field in $booleanFields) {
                    $value = $event.$field
                    if ($null -eq $value) { continue }
                    if ($value -isnot [bool]) { throw 'Invalid incident boolean metadata.' }
                    $metadata[$field] = $value
                }
                if (-not $metadata.Contains('Stage') -or -not $metadata.Contains('Reason')) { throw 'Missing incident event identity.' }
                $events += [pscustomobject]$metadata
            }
            $metadata = [ordered]@{ schemaVersion=1; screenPixelsIncluded=$false; windowTitlesIncluded=$false; events=$events }
            foreach ($field in @('processId', 'qpcFrequency', 'incidentAtTicks', 'preWindowSeconds', 'postWindowSeconds')) {
                $value = $incident.$field
                if ($null -eq $value) { continue }
                if ($value -isnot [ValueType] -or $value -is [bool] -or $value -is [datetime]) { throw 'Invalid incident numeric metadata.' }
                $metadata[$field] = $value
            }
            if ($null -ne $incident.timestamp) { $metadata.timestamp = ([DateTimeOffset]::Parse($incident.timestamp)).ToString('o') }
            if ($null -ne $incident.applicationVersion) {
                if ($incident.applicationVersion -isnot [string] -or $incident.applicationVersion -notmatch '^[a-zA-Z0-9.+-]{1,128}$') { throw 'Invalid incident version.' }
                $metadata.applicationVersion = $incident.applicationVersion
            }
            $json = $metadata | ConvertTo-Json -Depth 6
            $bytes = [Text.Encoding]::UTF8.GetByteCount($json)
            if ($bytes -gt 2MB -or $totalBytes + $bytes -gt 20MB) { continue }
            $null = New-Item -ItemType Directory -Path $DestinationDirectory -Force
            [IO.File]::WriteAllText((Join-Path $DestinationDirectory ('incident-{0:D2}.json' -f $copied)), $json, [Text.UTF8Encoding]::new($false))
            $totalBytes += $bytes
            $copied++
        } catch { continue }
    }
    return $copied
}
function Get-GlassDeskProcessMetadata {
    param([string]$PackageDirectory)
    $processes = @()
    $available = $true
    try {
        $running = @(Get-CimInstance Win32_Process -Filter "Name = 'GlassDesk.exe'" -OperationTimeoutSec 3)
        foreach ($process in $running | Select-Object -First 64) {
            $version = $null
            $belongsToThisFolder = $null
            if (-not [string]::IsNullOrEmpty($process.ExecutablePath)) {
                $belongsToThisFolder = [string]::Equals([IO.Path]::GetDirectoryName($process.ExecutablePath),
                    [IO.Path]::GetFullPath($PackageDirectory).TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)
                try { $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($process.ExecutablePath).ProductVersion } catch { }
                if ($version -notmatch '^[a-zA-Z0-9.+-]{1,128}$') { $version = $null }
            }
            $worker = -not [string]::IsNullOrEmpty($process.CommandLine) -and $process.CommandLine -match '(?:^|\s)--glass-backdrop-worker(?:\s|$)'
            $processes += [pscustomobject]@{ processId=[int]$process.ProcessId; worker=$worker; version=$version; belongsToThisFolder=$belongsToThisFolder }
        }
    } catch { $available = $false }
    return [pscustomobject]@{ available=$available; mainCount=@($processes | Where-Object { -not $_.worker }).Count;
        workerCount=@($processes | Where-Object { $_.worker }).Count; processes=$processes }
}
if ($MyInvocation.InvocationName -eq '.') { return }
$probe = Join-Path $PSScriptRoot 'GlassCaptureProbe.exe'
if (-not (Test-Path -LiteralPath $probe -PathType Leaf)) { throw 'Run this collector from the extracted test package.' }
$name = 'desktop-background-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 6)
$directory = Join-Path $PSScriptRoot ('Reports\' + $name)
$null = New-Item -ItemType Directory -Path $directory
$report = Join-Path $directory 'desktop-background.json'
$exitCode = $null
$timedOut = $false
$activeProcesses = Get-GlassDeskProcessMetadata -PackageDirectory $PSScriptRoot
Start-Sleep -Seconds 3
$process = Start-Process -FilePath $probe -ArgumentList @('--desktop-background-report', ('"' + $report + '"')) -WindowStyle Hidden -PassThru
try {
    if (-not $process.WaitForExit(30000)) {
        $timedOut = $true
        $process.Kill()
        $process.WaitForExit()
    } else { $exitCode = $process.ExitCode }
} finally { $process.Dispose() }
$incidentCount = Copy-BackdropIncidents -SourceDirectory (Join-Path $PSScriptRoot 'Data\logs\dock-backdrop-incidents') `
    -DestinationDirectory (Join-Path $directory 'backdrop-incidents')
[ordered]@{
    schemaVersion=2; exitCode=$exitCode; timedOut=$timedOut
    screenPixelsIncluded=$false; windowTitlesIncluded=$false; wallpaperPathsIncluded=$false
    userDataIncluded=$false; diagnosticIncidentMetadataIncluded=($incidentCount -gt 0); diagnosticIncidentCount=$incidentCount
    activeGlassDesk=$activeProcesses; applicationVersion=(Get-Item -LiteralPath (Join-Path $PSScriptRoot 'GlassDesk.exe')).VersionInfo.ProductVersion
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $directory 'collection.json') -Encoding UTF8
$zip = Join-Path $PSScriptRoot ('Reports\' + $name + '.zip')
Compress-Archive -LiteralPath $directory -DestinationPath $zip -CompressionLevel Optimal
Write-Output $zip
if (-not $NoDialog) {
    Add-Type -AssemblyName System.Windows.Forms
    [Windows.Forms.MessageBox]::Show(('Report saved. Send this ZIP to the developer:' + [Environment]::NewLine + $zip),
        'GlassDesk desktop background diagnostics') | Out-Null
}
