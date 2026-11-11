param(
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [Parameter(Mandatory=$true)][string]$SeedDataDirectory,
    [string]$DotnetPath='D:\dotnet10\dotnet.exe',
    [string]$NativeDll=''
)
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$output=[IO.Path]::GetFullPath($OutputDirectory)
if (-not $output.StartsWith($workspace+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Package output must remain inside this checkout.' }
if (Test-Path -LiteralPath $output) { throw 'Use a fresh package directory.' }
$seed=[IO.Path]::GetFullPath($SeedDataDirectory)
$seedNames=@('settings.json','apps.json','dock-layout.json','launcher-layout.json')
$seedBefore=@{}
foreach ($name in $seedNames) { $seedBefore[$name]=(Get-FileHash -LiteralPath (Join-Path $seed $name)).Hash }
[IO.Directory]::CreateDirectory($output) | Out-Null
$payload=Join-Path $output 'GlassDesk-Launcher-Test'
& $DotnetPath publish (Join-Path $workspace 'src/GlassDesk.App/GlassDesk.App.csproj') -c Release -r win-x64 --self-contained true -o $payload '-p:InformationalVersion=1.5.3-launcher-test.20261030' *> (Join-Path $output 'publish.log')
if ($LASTEXITCODE -ne 0) { throw 'Portable publish failed; see publish.log.' }
if ($NativeDll) { Copy-Item -LiteralPath ([IO.Path]::GetFullPath($NativeDll)) -Destination (Join-Path $payload 'GlassDesk.Rendering.Native.dll') }
$data=Join-Path $payload 'Data'
[IO.Directory]::CreateDirectory($data) | Out-Null
foreach ($name in $seedNames) { Copy-Item -LiteralPath (Join-Path $seed $name) -Destination (Join-Path $data $name) }
if (Test-Path -LiteralPath (Join-Path $seed 'icons')) { Copy-Item -LiteralPath (Join-Path $seed 'icons') -Destination (Join-Path $data 'icons') -Recurse }
foreach ($name in $seedNames) {
    if ((Get-FileHash -LiteralPath (Join-Path $seed $name)).Hash -ne $seedBefore[$name]) { throw "Source profile changed during copy: $name" }
    if ((Get-FileHash -LiteralPath (Join-Path $data $name)).Hash -ne $seedBefore[$name]) { throw "Copied profile mismatch: $name" }
}
$base='@echo off'+"`r`n"+'setlocal'+"`r`n"+'cd /d "%~dp0"'+"`r`n"
foreach ($flag in @('PLACEMENT_REUSE','DISPLAY_CADENCE','VISUAL_SHADOW_OPACITY','SCENE_REUSE','DEFER_PAINT_SCENE_CHECKS','RETAIN_REVEAL_GRID')) {
    $base+='set "GLASSDESK_LAUNCHER_'+$flag+'=0"'+"`r`n"
}
$base+='set "GLASSDESK_ANIMATION_TRACE_DIR="'+"`r`n"
$base+='set "GLASSDESK_NATIVE_RENDER_DLL=%~dp0GlassDesk.Rendering.Native.dll"'+"`r`n"
$nativeFlag=if ($NativeDll) {'1'} else {'0'}
$launch='start "" "%~dp0GlassDesk.exe" --show-launcher'+"`r`n"
$normal=$base+'set "GLASSDESK_LAUNCHER_NATIVE_SHADOW='+$nativeFlag+'"'+"`r`n"+$launch
[IO.File]::WriteAllText((Join-Path $payload 'Start-Test.cmd'),$normal,[Text.Encoding]::ASCII)
$diagnostic=$base+'set "GLASSDESK_LAUNCHER_NATIVE_SHADOW='+$nativeFlag+'"'+"`r`n"+'set "GLASSDESK_ANIMATION_TRACE_DIR=%~dp0TestLogs\Animations"'+"`r`n"+$launch
[IO.File]::WriteAllText((Join-Path $payload 'Start-With-Diagnostics.cmd'),$diagnostic,[Text.Encoding]::ASCII)
[IO.File]::WriteAllText((Join-Path $payload 'Start-Original-Shadow.cmd'),$base+'set "GLASSDESK_LAUNCHER_NATIVE_SHADOW=0"'+"`r`n"+$launch,[Text.Encoding]::ASCII)
$readme=@'
GlassDesk 启动台便携测试候选（2026-10-30）

解压到可写目录，先从托盘退出原来的 GlassDesk，再双击 Start-Test.cmd。
安装版和便携版使用同一单实例协议，不能同时作为主实例运行。
无需安装 .NET、无需管理员权限、不会替换安装文件。本包内置独立 Data 副本；测试时的修改只保存在解压目录。
测试结束从托盘退出，再启动原安装版。不要把本包 Data 覆盖回安装版。

当前已修复首次准备时产品进程排除列表初始化顺序，并增加隐藏预热 DPI 与保留帧新鲜度保护。
Start-Test.cmd 启用启动台专用原生阴影候选，复用同一输入穿透窗口和阴影配方，跳过最终必然被裁掉的内部像素。
Start-Original-Shadow.cmd 使用原 WPF 阴影，供退出测试版后交错对照；直接运行 GlassDesk.exe 也沿用默认 WPF 路径。
两个入口共用本包的 Data。若显卡不支持原生阴影，程序自动退回 WPF 阴影。
是否达到屏幕满刷新率，需要本机实际显示证据确认；本包不宣称稳定 240 FPS。
保留磨砂、液态玻璃、阴影、原动画曲线和时长；高刷关闭选项沿用复制的配置。

请重点测试磨砂/液态的正常开关、快速反向、Esc/外部点击、久置后打开、睡眠恢复及切屏/DPI。
需要日志时退出本测试版，再用 Start-With-Diagnostics.cmd 启动。日志写入本包 TestLogs/Animations；这是回调与 CPU 计时，不是显示 FPS。

此包包含当前本机应用库、布局和图标的副本，供本机个人测试，请勿直接公开转发。
'@
[IO.File]::WriteAllText((Join-Path $payload '测试说明.txt'),$readme,[Text.UTF8Encoding]::new($true))
$files=@(Get-ChildItem -LiteralPath $payload -File -Recurse | ForEach-Object {
    [pscustomobject]@{path=[IO.Path]::GetRelativePath($payload,$_.FullName);bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
})
[pscustomobject]@{schemaVersion=1;kind='local portable test candidate';createdUtc=[DateTimeOffset]::UtcNow;sourceCommit=(& git -C $workspace rev-parse HEAD);uncommittedChanges=$true;selfContained=$true;rid='win-x64';installedDataEdited=$false;seedDataHashes=$seedBefore;files=$files} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'package-manifest.json') -Encoding utf8
Write-Output $payload
Write-Output 'Validate this payload before creating the final ZIP.'
