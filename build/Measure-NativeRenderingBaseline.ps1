# Observes a running product/probe without changing its visibility or settings.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][int]$RootProcessId,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [ValidateRange(5,600)][int]$Seconds = 30
)
$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Use a fresh measurement directory.' }
$root = Get-CimInstance Win32_Process -Filter "ProcessId=$RootProcessId"
if (!$root) { throw 'Root process is not running.' }
$null = New-Item -ItemType Directory -Path $output
$phase = Join-Path $output 'phase.txt'
[IO.File]::WriteAllText($phase, 'observational-baseline')
$sampler = Start-Job -ScriptBlock {
    param($script,$rootId,$phaseFile,$target,$seconds)
    & $script -RootProcessId $rootId -PhaseFile $phaseFile -OutputPath $target -MaximumSeconds $seconds -CollectGpu
} -ArgumentList (Join-Path $PSScriptRoot 'Measure-GlassDeskProcessGroup.ps1'),$RootProcessId,$phase,(Join-Path $output 'process-group.json'),$Seconds
$samples = [Collections.Generic.List[object]]::new()
$known = @{}; $known[$RootProcessId] = $root.CreationDate
$watch = [Diagnostics.Stopwatch]::StartNew()
$unavailable = $null
try {
    while ($watch.Elapsed.TotalSeconds -lt $Seconds) {
        $processes = @(Get-CimInstance Win32_Process | Select-Object ProcessId,ParentProcessId,CreationDate)
        $liveRoot = $processes | Where-Object { $_.ProcessId -eq $RootProcessId -and $_.CreationDate -eq $root.CreationDate }
        if (!$liveRoot) { break }
        foreach ($process in $processes) {
            $memberId = [int]$process.ProcessId
            if ($known.ContainsKey($memberId) -and $known[$memberId] -ne $process.CreationDate) { $known.Remove($memberId) }
        }
        do {
            $added = $false
            foreach ($process in $processes) {
                if ($known.ContainsKey([int]$process.ParentProcessId) -and !$known.ContainsKey([int]$process.ProcessId)) {
                    $known[[int]$process.ProcessId] = $process.CreationDate; $added = $true
                }
            }
        } while ($added)
        try {
            $gpu = @(Get-CimInstance Win32_PerfFormattedData_GPUPerformanceCounters_GPUProcessMemory -ErrorAction Stop | Where-Object {
                $_.Name -match 'pid_(\d+)_' -and $known.ContainsKey([int]$Matches[1])
            } | Select-Object Name,DedicatedUsage,SharedUsage,TotalCommitted)
            $samples.Add([pscustomobject]@{atUtc=[DateTimeOffset]::UtcNow.ToString('O');elapsedSeconds=$watch.Elapsed.TotalSeconds;
                dedicatedBytes=($gpu | Measure-Object DedicatedUsage -Sum).Sum;
                sharedBytes=($gpu | Measure-Object SharedUsage -Sum).Sum;
                committedBytes=($gpu | Measure-Object TotalCommitted -Sum).Sum;members=$gpu})
        } catch { $unavailable=$_.Exception.Message; break }
        Start-Sleep -Milliseconds 1000
    }
    $null = Wait-Job $sampler
    Receive-Job $sampler
} finally {
    if ($sampler.State -eq 'Running') { Stop-Job $sampler }
    Remove-Job $sampler
    [ordered]@{rootProcessId=$RootProcessId;source='Win32_PerfFormattedData_GPUPerformanceCounters_GPUProcessMemory';
        unavailable=$unavailable; samples=$samples.ToArray();presentedFpsMeasured=$false;
        limitation='Observation only; no controlled gestures or fixed scene. GPU counters are sampled, may miss short-lived helpers, and must not be added to CPU private commit as a total-memory measure.'} |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'gpu-memory.json') -Encoding utf8
}
