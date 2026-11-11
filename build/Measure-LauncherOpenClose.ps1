param(
    [ValidateSet('placement','cadence','shadow','scene','paint','grid','combined','native-shadow')][string]$Candidate='placement',
    [string]$OutputDirectory,
    [string]$DotnetPath='D:\dotnet10\dotnet.exe',
    [int]$Cycles=10,
    [int]$Batches=3,
    [string]$AcceptanceDirectory='',
    [string]$CaptureExecutable=''
)
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
if (-not $OutputDirectory) { $OutputDirectory=Join-Path $workspace ('artifacts\launcher-open-close-20261030\'+$Candidate+'-'+(Get-Date -Format 'yyyyMMdd-HHmmss')) }
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
if (-not $OutputDirectory.StartsWith($workspace+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Evidence must remain in this checkout.' }
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Use a fresh output directory.' }
$acceptance=Join-Path $workspace 'tests\GlassDesk.LauncherAcceptance\bin\Release\net10.0-windows10.0.26100.0\GlassDesk.LauncherAcceptance.dll'
$capture=Join-Path $workspace 'src\GlassDesk.App\bin\Release\net10.0-windows10.0.26100.0\GlassDesk.exe'
if ($AcceptanceDirectory) { $acceptance=Join-Path ([IO.Path]::GetFullPath($AcceptanceDirectory)) 'GlassDesk.LauncherAcceptance.dll' }
if ($CaptureExecutable) { $capture=[IO.Path]::GetFullPath($CaptureExecutable) }
$receipts=[Collections.Generic.List[object]]::new()
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$power=(& powercfg /getactivescheme 2>&1 | Out-String).Trim()
for ($batch=1; $batch -le $Batches; $batch++) {
    $arms=if ($batch % 2) { @('A','B') } else { @('B','A') }
    foreach ($material in @('FrostedGlass','AndroidLiquidGlass')) {
        foreach ($arm in $arms) {
            $run=Join-Path $OutputDirectory ('batch-'+$batch+'-'+$material+'-'+$arm)
            [IO.Directory]::CreateDirectory($run) | Out-Null
            $info=[Diagnostics.ProcessStartInfo]::new($DotnetPath)
            $info.WorkingDirectory=$workspace
            $info.UseShellExecute=$false; $info.CreateNoWindow=$true
            $info.RedirectStandardOutput=$true; $info.RedirectStandardError=$true
            $info.Environment['DOTNET_ROOT']=Split-Path $DotnetPath -Parent
            $info.Environment['DOTNET_ROOT_X64']=Split-Path $DotnetPath -Parent
            $info.Environment['GLASSDESK_LAUNCHER_PLACEMENT_REUSE']=if ($Candidate -eq 'cadence' -or ($Candidate -eq 'placement' -and $arm -eq 'B')) {'1'} else {'0'}
            $info.Environment['GLASSDESK_LAUNCHER_DISPLAY_CADENCE']=if ($Candidate -in @('cadence','combined') -and $arm -eq 'B') {'1'} else {'0'}
            $info.Environment['GLASSDESK_LAUNCHER_VISUAL_SHADOW_OPACITY']=if ($Candidate -in @('shadow','combined') -and $arm -eq 'B') {'1'} else {'0'}
            $info.Environment['GLASSDESK_LAUNCHER_SCENE_REUSE']=if ($Candidate -eq 'scene' -and $arm -eq 'B') {'1'} else {'0'}
            $info.Environment['GLASSDESK_LAUNCHER_DEFER_PAINT_SCENE_CHECKS']=if ($Candidate -in @('paint','combined') -and $arm -eq 'B') {'1'} else {'0'}
            $info.Environment['GLASSDESK_LAUNCHER_RETAIN_REVEAL_GRID']=if ($Candidate -in @('grid','combined') -and $arm -eq 'B') {'1'} else {'0'}
            $info.Environment['GLASSDESK_LAUNCHER_NATIVE_SHADOW']=if ($Candidate -eq 'native-shadow' -and $arm -eq 'B') {'1'} else {'0'}
            foreach ($arg in @($acceptance,'--launcher-open-close','--material',$material,'--cycles',"$Cycles",'--capture-exe',$capture,'--report',(Join-Path $run 'report.json'))) { $info.ArgumentList.Add($arg) }
            $started=[DateTimeOffset]::UtcNow
            $process=[Diagnostics.Process]::Start($info)
            $stdout=$process.StandardOutput.ReadToEndAsync(); $stderr=$process.StandardError.ReadToEndAsync()
            if (-not $process.WaitForExit(180000)) { $process.Kill($true); throw "Fixture timed out: $run" }
            [IO.File]::WriteAllText((Join-Path $run 'stdout.log'),$stdout.GetAwaiter().GetResult())
            [IO.File]::WriteAllText((Join-Path $run 'stderr.log'),$stderr.GetAwaiter().GetResult())
            $receipts.Add([pscustomobject]@{batch=$batch;material=$material;arm=$arm;pid=$process.Id;startedUtc=$started;endedUtc=[DateTimeOffset]::UtcNow;exitCode=$process.ExitCode;directory=$run})
            [pscustomobject]@{candidate=$Candidate;cycles=$Cycles;batches=$Batches;power=$power;runs=$receipts.ToArray()} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'manifest.json') -Encoding utf8
            Write-Output "$Candidate batch=$batch material=$material arm=$arm exit=$($process.ExitCode)"
            if ($process.ExitCode -ne 0) { throw "Fixture failed: $run" }
            $process.Dispose()
        }
    }
}
Write-Output "Evidence: $OutputDirectory"
