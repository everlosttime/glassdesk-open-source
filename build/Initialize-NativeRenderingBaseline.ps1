# Read-only with respect to the installed product and its Data. No app is launched.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [string]$InstalledDirectory = 'D:\GlassDesk',
    [string]$OriginalCheckout = '',
    [string]$BaselineCommit = '2d4bd40095c04d8a3c5d268a3052018d7de5ae18',
    [switch]$AllowRunningProfile
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$output = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
$installed = [IO.Path]::GetFullPath($InstalledDirectory).TrimEnd('\')
$source = Join-Path $installed 'Data'
if ($output.Equals($installed,[StringComparison]::OrdinalIgnoreCase) -or
    $output.StartsWith($installed+'\',[StringComparison]::OrdinalIgnoreCase) -or
    $installed.StartsWith($output+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Snapshot output overlaps the installation.' }
if (Test-Path -LiteralPath $output) { throw 'Use a new baseline output directory; existing evidence is never overwritten.' }
if (!(Test-Path -LiteralPath (Join-Path $source 'settings.json'))) { throw 'A saved Data/settings.json is required.' }
$running = @(Get-Process -Name GlassDesk -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Join-Path $installed 'GlassDesk.exe') })
if ($running.Count -and !$AllowRunningProfile) { throw 'Close the installed product normally, or use -AllowRunningProfile with before/after hash verification.' }
$commit = (& git -C $repo rev-parse $BaselineCommit).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Baseline commit is not available.' }
function Profile-Manifest([string]$Root) {
    $prefix = [IO.Path]::GetFullPath($Root).TrimEnd('\')+'\'
    @(Get-ChildItem -LiteralPath $Root -File -Recurse | ForEach-Object {
        $relative = $_.FullName.Substring($prefix.Length)
        if ($relative -notmatch '^(logs|crash-dumps)\\' -and $_.Name -notlike '*.lease') {
            [pscustomobject][ordered]@{path=$relative;bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
        }
    } | Sort-Object path)
}
$before = @(Profile-Manifest $source)
$profile = Join-Path $output 'profile'
$null = New-Item -ItemType Directory -Path $profile -Force
foreach ($entry in $before) {
    $destination = Join-Path $profile $entry.path
    $null = New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force
    Copy-Item -LiteralPath (Join-Path $source $entry.path) -Destination $destination
}
$copied = @(Profile-Manifest $profile)
$after = @(Profile-Manifest $source)
$unchanged = ($before | ConvertTo-Json -Depth 4 -Compress) -eq ($after | ConvertTo-Json -Depth 4 -Compress)
$verifiedCopy = ($before | ConvertTo-Json -Depth 4 -Compress) -eq ($copied | ConvertTo-Json -Depth 4 -Compress)
$before | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'profile-manifest.json') -Encoding utf8
$payload = @(Get-ChildItem -LiteralPath $installed -File | Where-Object { $_.Extension -in '.dll','.exe','.json' } |
    ForEach-Object { [pscustomobject]@{name=$_.Name;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash} })
$original = @()
if ($OriginalCheckout) {
    $originalRoot = [IO.Path]::GetFullPath($OriginalCheckout)
    $paths = @(& git -C $originalRoot diff --name-only) + @(& git -C $originalRoot ls-files --others --exclude-standard)
    $original = @($paths | Sort-Object -Unique | ForEach-Object {
        $file = Join-Path $originalRoot $_
        if (Test-Path -LiteralPath $file -PathType Leaf) { [pscustomobject]@{path=$_;sha256=(Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash} }
    })
}
$run = Get-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue
$startup = [ordered]@{runValue=$run.GlassDesk;shortcuts=@();tasks=@();taskQueryUnavailable=$null}
$startupDirectory = [Environment]::GetFolderPath('Startup')
if (Test-Path -LiteralPath $startupDirectory) {
    $startup.shortcuts = @(Get-ChildItem -LiteralPath $startupDirectory -Filter '*GlassDesk*' -File | ForEach-Object {
        [pscustomobject]@{name=$_.Name;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
    })
}
try {
    $startup.tasks = @(Get-ScheduledTask -ErrorAction Stop | Where-Object TaskName -like '*GlassDesk*' | ForEach-Object {
        [pscustomobject]@{name=$_.TaskName;path=$_.TaskPath;xml=(Export-ScheduledTask -TaskName $_.TaskName -TaskPath $_.TaskPath)}
    })
} catch { $startup.taskQueryUnavailable=$_.Exception.Message }
$os = Get-CimInstance Win32_OperatingSystem | Select-Object Caption,Version,BuildNumber
$gpu = @(Get-CimInstance Win32_VideoController | Select-Object Name,DriverVersion,CurrentHorizontalResolution,CurrentVerticalResolution,CurrentRefreshRate)
$power = (& powercfg.exe /getactivescheme | Out-String).Trim()
[ordered]@{
    sourceCommit=$commit; worktreeHead=(& git -C $repo rev-parse HEAD).Trim(); recordedUtc=[DateTimeOffset]::UtcNow.ToString('O')
    installedDirectory=$installed; sourceProfileUnchanged=$unchanged; snapshotHashesVerified=$verifiedCopy; profileFiles=$before.Count
    profileWasRunning=($running.Count -gt 0); runningSnapshotExplicitlyAllowed=[bool]$AllowRunningProfile
    profileExclusions=@('logs','crash-dumps','*.lease'); installedPayload=$payload; originalUncommittedFiles=$original
    os=$os; gpu=$gpu; powerScheme=$power; startup=$startup
    performanceMeasured=$false; presentedFpsMeasured=$false; originalDataRestored=$false
    recovery='No installed file was replaced. Measurements must use copies of profile/, never the installed Data.'
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'baseline.json') -Encoding utf8
if (!$unchanged -or !$verifiedCopy) { throw 'Profile changed during snapshot or copy hashes differ. Do not use this baseline.' }
Write-Output "Baseline frozen: $($before.Count) profile files verified; $commit"
