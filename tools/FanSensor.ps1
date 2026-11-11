param(
    [switch] $Once
)

$ErrorActionPreference = 'Stop'
$sensorDirectory = Join-Path $env:ProgramData 'GlassDesk\FanSensor'
$statusPath = Join-Path $sensorDirectory 'fan-status.json'
$temporaryPath = Join-Path $sensorDirectory 'fan-status.tmp.json'
New-Item -ItemType Directory -Path $sensorDirectory -Force | Out-Null

$deviceIds = @(
    [uint32]0x00110013, # CPU fan
    [uint32]0x00110014, # GPU fan
    [uint32]0x00110031  # Middle fan
)

function Write-FanSnapshot {
    param(
        $Rpm,
        [bool] $Supported,
        [string] $Source,
        [string] $ErrorMessage
    )

    [ordered]@{
        version = 1
        timestampUtc = [DateTimeOffset]::UtcNow.ToString('o')
        rpm = $Rpm
        supported = $Supported
        source = $Source
        error = $ErrorMessage
    } | ConvertTo-Json -Compress | Set-Content -LiteralPath $temporaryPath -Encoding UTF8
    Move-Item -LiteralPath $temporaryPath -Destination $statusPath -Force
}

function Read-AsusFanRpm {
    $instance = Get-CimInstance -Namespace 'root/wmi' -ClassName 'AsusAtkWmi_WMNB' -OperationTimeoutSec 3
    $bestRpm = $null
    $supported = $false

    foreach ($deviceId in $deviceIds) {
        $result = Invoke-CimMethod -InputObject $instance -MethodName 'DSTS' -Arguments @{
            Device_ID = $deviceId
        } -OperationTimeoutSec 3
        $status = [uint32]$result.device_status
        if ($status -eq 4294967294) { continue }

        $rawSpeed = [int]($status -band 0xffff)
        $isPresent = ($status -band [uint32]0x00010000) -ne 0
        if (-not $isPresent -and $rawSpeed -eq 0) { continue }

        $supported = $true
        $rpm = $rawSpeed * 100
        if ($rpm -ge 100 -and $rpm -le 30000) {
            if ($null -eq $bestRpm -or $rpm -gt $bestRpm) { $bestRpm = $rpm }
        }
    }

    return [pscustomobject]@{ Rpm = $bestRpm; Supported = $supported }
}

Write-FanSnapshot -Rpm $null -Supported $false -Source 'starting' -ErrorMessage $null

do {
    try {
        $reading = Read-AsusFanRpm
        Write-FanSnapshot -Rpm $reading.Rpm -Supported $reading.Supported -Source 'asus-wmi-dsts' -ErrorMessage $null
    }
    catch {
        Write-FanSnapshot -Rpm $null -Supported $false -Source 'asus-wmi-dsts' -ErrorMessage $_.Exception.Message
    }

    if (-not $Once) { Start-Sleep -Seconds 4 }
} while (-not $Once)
