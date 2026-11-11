param(
    [string] $OutputDirectory = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..\..')) 'docs\evidence\liquid-glass-refraction-acceptance-20260929\final-frames-verified'),
    [string] $DotnetPath = (Join-Path $env:USERPROFILE '.dotnet\dotnet.exe')
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$outputPath = [System.IO.Path]::GetFullPath($OutputDirectory)

if (-not (Test-Path -LiteralPath $DotnetPath -PathType Leaf)) {
    throw "dotnet executable not found: $DotnetPath"
}
if (Test-Path -LiteralPath $outputPath -PathType Container) {
    if (Get-ChildItem -LiteralPath $outputPath -Force | Select-Object -First 1) {
        throw "Refusing to overwrite a non-empty output directory: $outputPath"
    }
} else {
    New-Item -ItemType Directory -Path $outputPath | Out-Null
}

$env:DOTNET_ROOT = Split-Path -Parent $DotnetPath
Push-Location $repoRoot
$logPrefix = Split-Path -Leaf $outputPath
$logParent = Split-Path -Parent $outputPath
$logPath = Join-Path $logParent ($logPrefix + '-matrix-stdout.log')
$errorPath = Join-Path $logParent ($logPrefix + '-matrix-stderr.log')
try {
    & $DotnetPath build 'tools\glass-capture-probe\GlassCaptureProbe.csproj' -c Release
    if ($LASTEXITCODE -ne 0) {
        throw "Debug final-frame exporter build failed with exit code $LASTEXITCODE"
    }

    $exePath = Join-Path $repoRoot 'tools\glass-capture-probe\bin\Release\net10.0-windows\GlassCaptureProbe.exe'
    $arguments = '--export-final-matrix "' + $outputPath.Replace('"', '\"') + '"'
    $process = Start-Process -FilePath $exePath -ArgumentList $arguments -PassThru -WindowStyle Hidden -RedirectStandardOutput $logPath -RedirectStandardError $errorPath
    $gpuSamples = [System.Collections.Generic.List[object]]::new()
    $gpuErrors = [System.Collections.Generic.List[string]]::new()
    $counterPath = '\GPU Engine(*)\Utilization Percentage'

    while (-not $process.HasExited) {
        try {
            $sample = Get-Counter -Counter $counterPath -SampleInterval 1 -MaxSamples 1 -ErrorAction Stop
            $matching = @($sample.CounterSamples | Where-Object {
                $_.Path -match "pid_$($process.Id)_" -and $_.Path -match '_engtype_3D'
            })
            $valid = @($matching | Where-Object {
                $_.Status -eq 0 -and [double]::IsFinite([double]$_.CookedValue)
            })
            if ($valid.Count -eq 0) {
                $gpuErrors.Add('No valid GPU Engine 3D counter sample for the exporter process.')
                $process.Refresh()
                continue
            }
            $gpuSamples.Add([pscustomobject]@{
                utc = [DateTimeOffset]::UtcNow.ToString('o')
                processId = $process.Id
                gpu3dPercent = [Math]::Round((($valid | Measure-Object -Property CookedValue -Sum).Sum), 4)
                gpuEngineInstances = $valid.Count
            })
        } catch {
            $gpuErrors.Add($_.Exception.Message)
            Start-Sleep -Milliseconds 500
        }
        $process.Refresh()
    }
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) {
        Get-Content -LiteralPath $errorPath -ErrorAction SilentlyContinue
        Get-Content -LiteralPath $logPath -ErrorAction SilentlyContinue
        throw "Debug final-frame exporter failed with exit code $($process.ExitCode)"
    }
    if ($gpuSamples.Count -gt 0) {
        $gpuSamples | Export-Csv -LiteralPath (Join-Path $outputPath 'gpu-samples.csv') -NoTypeInformation -Encoding utf8
        $gpuValues = @($gpuSamples | ForEach-Object { $_.gpu3dPercent })
        [pscustomobject]@{
            processId = $process.Id
            sampleCount = $gpuSamples.Count
            averageGpu3dPercent = [Math]::Round(($gpuValues | Measure-Object -Average).Average, 4)
            peakGpu3dPercent = [Math]::Round(($gpuValues | Measure-Object -Maximum).Maximum, 4)
            invalidOrMissingSampleCount = $gpuErrors.Count
            errors = @($gpuErrors)
        } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $outputPath 'gpu-summary.json') -Encoding utf8
    }
} finally {
    Pop-Location
}

$manifestPath = Join-Path $outputPath 'final-frame-matrix.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Final-frame manifest was not written: $manifestPath"
}
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$frames = @(Get-ChildItem -LiteralPath $outputPath -Filter '*.png' -File | Where-Object { $_.Name -notlike '*-contact.png' })
$sourceDirectory = Join-Path $outputPath 'source-inputs'
$sourceFrames = if (Test-Path -LiteralPath $sourceDirectory -PathType Container) {
    @(Get-ChildItem -LiteralPath $sourceDirectory -Filter '*.png' -File)
} else { @() }
if ($frames.Count -ne 36) {
    throw "Expected 36 one-shot final frames, found $($frames.Count)"
}
if (@($manifest.frames).Count -ne 36) {
    throw "Expected 36 frame records, found $(@($manifest.frames).Count)"
}
if (@($manifest.compositionSamples).Count -ne 3) {
    throw "Expected 3 live composition samples, found $(@($manifest.compositionSamples).Count)"
}
if ($sourceFrames.Count -ne 36 -or @($manifest.sourceFrames).Count -ne 36) {
    throw "Expected 36 one-shot Debug background inputs, found $($sourceFrames.Count)"
}
if (
    $manifest.liveDpi -ne 144 -or
    $manifest.debugViewActiveDuringFinalExports -ne 'None' -or
    $manifest.debugViewActiveDuringCompositionTiming -ne 'None' -or
    $manifest.diagnosticImageGeneratorCallCount -ne 0
) {
    throw "Unexpected source DPI or Debug view state in matrix manifest"
}
if ($manifest.inputMethod -notmatch 'one-shot Debug pattern rasterization and injection at the LiquidGlassOptics.Render input boundary') {
    throw "The matrix did not use the documented one-shot Debug input path"
}
foreach ($frame in $manifest.frames) {
    $imagePath = Join-Path $outputPath $frame.file
    if (-not (Test-Path -LiteralPath $imagePath -PathType Leaf)) {
        throw "Frame record refers to a missing image: $($frame.file)"
    }
    if ((Get-Item -LiteralPath $imagePath).Length -lt 256) {
        throw "Final frame is unexpectedly small: $($frame.file)"
    }
    if (
        $frame.liveSourceBeforeInjection -ne 'LiveDesktop' -or
        $frame.inputMethod -ne 'DebugPatternInjectedAtProductionLiquidGlassOptics.Render'
    ) {
        throw "Frame was not exported from the production surface after a live sample: $($frame.file)"
    }
}
foreach ($source in $manifest.sourceFrames) {
    $imagePath = Join-Path $sourceDirectory $source.file
    if (-not (Test-Path -LiteralPath $imagePath -PathType Leaf)) {
        throw "Source record refers to a missing sampled-background image: $($source.file)"
    }
}
$invalidSceneData = @($manifest.sourceFrames | Where-Object {
    ($_.background -eq 'bright-low-contrast' -and ($_.meanLuma -lt 180 -or $_.lumaStdDev -gt 45)) -or
    ($_.background -eq 'dark' -and $_.meanLuma -gt 120) -or
    ($_.background -eq 'high-contrast-text-window' -and
        ($_.darkPixelFraction -lt 0.01 -or $_.brightPixelFraction -lt 0.2 -or $_.minimumLuma -gt 80 -or $_.maximumLuma -lt 220)) -or
    ($_.background -eq 'straight-lines-grid' -and
        ($_.meanLuma -lt 130 -or $_.meanLuma -gt 235 -or $_.lumaStdDev -lt 20 -or $_.minimumLuma -gt 80))
})
if ($invalidSceneData.Count -gt 0) {
    $invalidSceneData | Select-Object Background,DockWidthDip,MeanLuma,LumaStdDev,MinimumLuma,MaximumLuma,DarkPixelFraction,BrightPixelFraction | Format-Table -AutoSize
    throw "One or more requested background patterns did not reach the production sampler."
}

Write-Output "PASS: 36 final production WPF frames; 36 Debug input signatures; 3 live composition samples."
