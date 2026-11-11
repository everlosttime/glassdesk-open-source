[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$AcceptanceDirectory,
    [Parameter(Mandatory=$true)][string]$OutputPath
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($AcceptanceDirectory)
$consoleHosted = -not (Test-Path -LiteralPath (Join-Path $root 'product-measurement-run.json'))
function Distribution($Values) {
    $sorted = @($Values | Where-Object { $null -ne $_ } | Sort-Object)
    if ($sorted.Count -eq 0) { return $null }
    [ordered]@{count=$sorted.Count;mean=($sorted | Measure-Object -Average).Average;
        p95=$sorted[[Math]::Max(0,[Math]::Ceiling(.95*$sorted.Count)-1)];minimum=$sorted[0];maximum=$sorted[-1]}
}
$runs = foreach ($kind in @('baseline','candidate')) {
    $samplesPath = Join-Path $root "$kind/process-group-samples.json"
    if (-not (Test-Path -LiteralPath $samplesPath)) { continue }
    $raw = Get-Content -LiteralPath $samplesPath -Raw | ConvertFrom-Json
    $phases = foreach ($phase in @('idle','open','closed','sleeping','rapid-interaction','long-run','long-run-no-capture')) {
        $rows = if ($phase -eq 'long-run-no-capture') {
            @($raw.samples | Where-Object {
                $_.phase -eq 'long-run' -and @($_.members | Where-Object {
                    $_.processId -ne $raw.rootProcessId -and $_.name -in @('GlassDesk.exe','GlassDesk.LauncherAcceptance.exe')
                }).Count -eq 0
            })
        } else { @($raw.samples | Where-Object phase -eq $phase) }
        if ($rows.Count -eq 0) { continue }
        $complete = @($rows | Where-Object privateWorkingComplete)
        [ordered]@{
            phase=$phase;sampleCount=$rows.Count;completePrivateSamples=$complete.Count
            firstTimestamp=$rows[0].timestamp;lastTimestamp=$rows[-1].timestamp
            privateWorkingMB=Distribution @($complete | ForEach-Object { $_.privateWorkingBytes/1MB })
            privateCommitMB=Distribution @($rows | ForEach-Object { $_.privateCommitBytes/1MB })
            totalWorkingMB=Distribution @($rows | ForEach-Object { $_.workingBytes/1MB })
            cpuPercent=Distribution @($rows | ForEach-Object { $_.cpuPercent })
            processCount=Distribution @($rows | ForEach-Object { $_.processCount })
            gpuEnginePercent=Distribution @($rows | ForEach-Object { @($_.gpuEngines | ForEach-Object UtilizationPercentage) })
        }
    }
    $functional = Join-Path $root "$kind/full-host-performance.json"
    [ordered]@{kind=$kind;measurement=$raw.measurement;intervalTargetSeconds=$raw.intervalTargetSeconds;
        includesConsoleHosts=$consoleHosted;phases=@($phases)
        functional=if (Test-Path -LiteralPath $functional) {
            $result=Get-Content -LiteralPath $functional -Raw | ConvertFrom-Json
            [ordered]@{passed=$result.passed;failed=$result.failed;quick=$result.quick;phaseSeconds=$result.phaseSeconds;
                longRunSeconds=$result.longRunSeconds;helperExitMs=$result.helperExitMs;shaderFailures=$result.shaderFailures}
        } else { $null }
    }
}
$candidate = $runs | Where-Object kind -eq 'candidate'
$idle = $candidate.phases | Where-Object phase -eq 'idle'
$open = $candidate.phases | Where-Object phase -eq 'open'
$closed = $candidate.phases | Where-Object phase -eq 'closed'
$longIdle = $candidate.phases | Where-Object phase -eq 'long-run-no-capture'
$sleep = $candidate.phases | Where-Object phase -eq 'sleeping'
[ordered]@{
    generatedAt=[DateTimeOffset]::Now.ToString('O');acceptanceDirectory=$root;runs=@($runs)
    thresholds=[ordered]@{idlePrivateWorkingMeanMB=200;openPrivateWorkingMeanMB=250;sleepCpuMeanPercent=.2}
    targetChecks=[ordered]@{
        idle=if ($idle) {$idle.privateWorkingMB.mean -lt 200} else {$null}
        open=if ($open) {$open.privateWorkingMB.mean -le 250} else {$null}
        closed=if ($closed) {$closed.privateWorkingMB.mean -lt 200} else {$null}
        longRunNoCapture=if ($longIdle) {$longIdle.privateWorkingMB.mean -lt 200} else {$null}
        sleepCpu=if ($sleep) {$sleep.cpuPercent.mean -le .2} else {$null}
    }
    limitations=@('Sample maxima are observed at about 1 Hz and do not establish an instantaneous animation peak.',
        $(if ($consoleHosted) {'The actual production AppHost is hosted by a console acceptance driver; its console hosts remain included in the process group.'}
          else {'Actual GlassDesk.exe and its capture children; no console acceptance driver.'}),
        'GPU engine counters are activity observations, not displayed FPS or proof of game frame-time impact.',
        'Recording phases are excluded from steady-state and long-run metrics. No forced GC or working-set trimming.')
} | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $OutputPath -Encoding utf8
