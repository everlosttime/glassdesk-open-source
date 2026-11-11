$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Administrator privileges are required.'
}

$sourcePath = Join-Path $PSScriptRoot 'FanSensor.ps1'
$installDirectory = Join-Path $env:LOCALAPPDATA 'GlassDesk\FanSensor'
$installedScript = Join-Path $installDirectory 'FanSensor.ps1'
$statusPath = Join-Path $env:ProgramData 'GlassDesk\FanSensor\fan-status.json'
New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null
Copy-Item -LiteralPath $sourcePath -Destination $installedScript -Force

$powerShellPath = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$actionArguments = "-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$installedScript`""
$action = New-ScheduledTaskAction -Execute $powerShellPath -Argument $actionArguments
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $identity.Name
$taskPrincipal = New-ScheduledTaskPrincipal -UserId $identity.Name -LogonType Interactive -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -Hidden `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -RestartCount 3 `
    -RestartInterval ([TimeSpan]::FromMinutes(1))

$task = New-ScheduledTask -Action $action -Trigger $trigger -Principal $taskPrincipal -Settings $settings
Register-ScheduledTask -TaskName 'GlassDesk Fan Sensor' -InputObject $task -Force | Out-Null
Start-ScheduledTask -TaskName 'GlassDesk Fan Sensor'

$deadline = [DateTimeOffset]::UtcNow.AddSeconds(12)
do {
    Start-Sleep -Milliseconds 300
    if (Test-Path -LiteralPath $statusPath) {
        $status = Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json
        if ($status.version -eq 1 -and $status.timestampUtc) {
            Write-Host "GlassDesk Fan Sensor installed. Current reading: $($status.rpm) RPM"
            exit 0
        }
    }
} while ([DateTimeOffset]::UtcNow -lt $deadline)

throw 'The scheduled task was created, but no sensor snapshot was produced.'
