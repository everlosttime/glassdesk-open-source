[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][int]$RootProcessId,
    [Parameter(Mandatory=$true)][string]$PhaseFile,
    [Parameter(Mandatory=$true)][string]$OutputPath,
    [int]$MaximumSeconds = 2700,
    [switch]$CollectGpu
)
$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputPath)
$null = New-Item -ItemType Directory -Path (Split-Path -Parent $output) -Force
$samples = [Collections.Generic.List[object]]::new()
$known = [Collections.Generic.HashSet[int]]::new()
$null = $known.Add($RootProcessId)
$creationTimes = @{}
$previousCpu = @{}
$watch = [Diagnostics.Stopwatch]::StartNew()
$lastAt = 0.0
$version = $null
function Write-Samples {
    # The sampler is outside the product process; its CPU/memory are excluded.
    # Formatted GPU counters are observability, not displayed FPS measurements.
    [ordered]@{
        rootProcessId=$RootProcessId; fileVersion=$version; logicalProcessors=[Environment]::ProcessorCount
        measurement='Process tree including live/retiring capture helpers. CIM private working set; Process private commit/total working set; CPU time delta normalized by logical processor count. No forced GC or working set trimming.'
        intervalTargetSeconds=1; gpuRequested=[bool]$CollectGpu; samples=$samples.ToArray()
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $output -Encoding utf8
}
try {
    $root = Get-Process -Id $RootProcessId -ErrorAction Stop
    $productPath = $root.Path
    $version = (Get-Item -LiteralPath $productPath).VersionInfo.FileVersion
    $root.Dispose()
    while ($watch.Elapsed.TotalSeconds -lt $MaximumSeconds) {
        $tick = [Diagnostics.Stopwatch]::StartNew()
        $processes = @(Get-CimInstance Win32_Process | Select-Object ProcessId,ParentProcessId,Name,ExecutablePath,CommandLine,CreationDate)
        $live = $processes | Where-Object { $_.ProcessId -eq $RootProcessId }
        if (-not $live -or $live.ExecutablePath -ine $productPath) { break }
        if (-not $creationTimes.ContainsKey($RootProcessId)) { $creationTimes[$RootProcessId] = $live.CreationDate }
        if ($creationTimes[$RootProcessId] -ne $live.CreationDate) { throw "Product root PID was reused." }
        foreach ($item in $processes) {
            $itemId = [int]$item.ProcessId
            if ($known.Contains($itemId) -and $creationTimes.ContainsKey($itemId) -and $creationTimes[$itemId] -ne $item.CreationDate) {
                # Windows may reuse an exited helper's PID for an unrelated program.
                $null = $known.Remove($itemId); $creationTimes.Remove($itemId)
            }
        }
        # Discover descendants while the root lives; retained IDs let us see retiring
        # helpers even if their immediate parent has already exited.
        do {
            $added = $false
            foreach ($item in $processes) {
                if ($item.ExecutablePath -ieq $productPath -and $item.CommandLine -match '(?:^|\s)--glass-backdrop-worker\s' -and $known.Contains([int]$item.ParentProcessId) -and -not $known.Contains([int]$item.ProcessId)) {
                    $null = $known.Add([int]$item.ProcessId); $creationTimes[[int]$item.ProcessId] = $item.CreationDate; $added = $true
                }
            }
        } while ($added)
        $group = @($processes | Where-Object { $known.Contains([int]$_.ProcessId) -and $_.CommandLine -notlike '*--fullscreen-fixture*' })
        $privateWorking = @{}
        foreach ($counter in Get-CimInstance Win32_PerfFormattedData_PerfProc_Process) {
            if ($known.Contains([int]$counter.IDProcess)) { $privateWorking[[int]$counter.IDProcess] = [long]$counter.WorkingSetPrivate }
        }
        $at = $watch.Elapsed.TotalSeconds
        $interval = $at - $lastAt
        $phase = 'starting'
        if (Test-Path -LiteralPath $PhaseFile) {
            try {
                $phaseText = [IO.File]::ReadAllText($PhaseFile).Trim()
                $phase = if ($phaseText.StartsWith('{')) { ($phaseText | ConvertFrom-Json).phase } else { $phaseText }
            } catch { $phase = 'phase-write-in-progress' }
        }
        $members = [Collections.Generic.List[object]]::new()
        $cpuMilliseconds = 0.0
        foreach ($item in $group) {
            $member = $null
            try {
                $member = Get-Process -Id $item.ProcessId -ErrorAction Stop
                $cpu = $member.TotalProcessorTime.TotalMilliseconds
                if ($previousCpu.ContainsKey($member.Id)) { $cpuMilliseconds += [Math]::Max(0, $cpu - $previousCpu[$member.Id]) }
                $previousCpu[$member.Id] = $cpu
                $members.Add([pscustomobject][ordered]@{
                    processId=$member.Id; parentId=[int]$item.ParentProcessId; name=$item.Name
                    privateWorkingBytes=if ($privateWorking.ContainsKey($member.Id)) { $privateWorking[$member.Id] } else { $null }
                    privateCommitBytes=$member.PrivateMemorySize64; workingBytes=$member.WorkingSet64
                })
            } catch [Microsoft.PowerShell.Commands.ProcessCommandException] { }
            catch [System.InvalidOperationException] { }
            finally { if ($member) { $member.Dispose() } }
        }
        $gpu = $null
        if ($members.Count -eq 0) { break }
        if ($CollectGpu) {
            try {
                $engines = @(Get-CimInstance Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine -ErrorAction Stop |
                    Where-Object { $_.Name -match 'pid_(\d+)_' -and $known.Contains([int]$Matches[1]) } |
                    Select-Object Name,UtilizationPercentage)
                $gpu = $engines
            } catch { $gpu = $null }
        }
        $missingPrivate = @($members | Where-Object { $null -eq $_.privateWorkingBytes }).Count
        $samples.Add([pscustomobject][ordered]@{
            timestamp=[DateTimeOffset]::Now.ToString('O'); elapsedSeconds=$at; phase=$phase
            processCount=$members.Count; privateWorkingComplete=($missingPrivate -eq 0)
            privateWorkingBytes=if ($missingPrivate -eq 0) { ($members.privateWorkingBytes | Measure-Object -Sum).Sum } else { $null }
            privateCommitBytes=($members.privateCommitBytes | Measure-Object -Sum).Sum
            workingBytes=($members.workingBytes | Measure-Object -Sum).Sum
            cpuPercent=if ($lastAt -gt 0 -and $interval -gt 0) { 100*$cpuMilliseconds/($interval*1000)/[Environment]::ProcessorCount } else { $null }
            gpuEngines=$gpu; members=$members.ToArray()
        })
        $lastAt = $at
        if ($samples.Count % 10 -eq 0) { Write-Samples }
        $remaining = [Math]::Max(1, 1000 - [int]$tick.ElapsedMilliseconds)
        Start-Sleep -Milliseconds $remaining
    }
} finally { Write-Samples }
