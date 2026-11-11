param([string]$OutputPath = 'Reports/environment.json')
$ErrorActionPreference='Stop'
$windows=Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
$gpu=@(Get-CimInstance Win32_VideoController | Select-Object Name,DriverVersion,AdapterRAM)
$cpu=@(Get-CimInstance Win32_Processor | Select-Object Name)
$system=Get-CimInstance Win32_ComputerSystem
$dwm=Join-Path $env:windir 'System32/dwmapi.dll'
$report=[ordered]@{
    timestamp=(Get-Date).ToString('o'); displayVersion=$windows.DisplayVersion
    build=$windows.CurrentBuildNumber; ubr=$windows.UBR; edition=$windows.EditionID
    architecture=$system.SystemType; gpu=$gpu; cpu=$cpu
    hypervisorPresent=$system.HypervisorPresent
    dwmVersion=[Diagnostics.FileVersionInfo]::GetVersionInfo($dwm).FileVersion
    note='Adapter inventory alone does not establish the active rendering adapter or displayed FPS. No pixels are collected.'
}
$absolute=[IO.Path]::GetFullPath($OutputPath)
$null=New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($absolute)) -Force
$report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $absolute -Encoding utf8
Write-Output $absolute
