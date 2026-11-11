[CmdletBinding()]
param(
    [string]$ToolchainRoot = 'D:\Toolchains\GlassDesk-Native',
    [string]$DotnetPath = 'D:\dotnet10\dotnet.exe'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'Build-NativeRendering.ps1') -ToolchainRoot $ToolchainRoot -BuildProbe -DotnetPath $DotnetPath
if (!$?) { throw 'Native core or probe build failed.' }
$oldDll = $env:GLASSDESK_NATIVE_RENDER_DLL
try {
    $env:GLASSDESK_NATIVE_RENDER_DLL = Join-Path $repo 'artifacts\native-rendering\GlassDesk.Rendering.Native.dll'
    $results = Join-Path $repo ('artifacts\native-rendering\checks-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))
    & $DotnetPath test (Join-Path $repo 'tests\GlassDesk.NativeRendering.Tests\GlassDesk.NativeRendering.Tests.csproj') -c Release --results-directory $results --logger 'trx;LogFileName=native-rendering.trx'
    if ($LASTEXITCODE -ne 0) { throw 'Native rendering checks failed. Read the TRX and per-process probe.json.' }
    [xml]$trx = Get-Content -LiteralPath (Join-Path $results 'native-rendering.trx') -Raw
    $payload = @(foreach ($name in @('GlassDesk.Rendering.Native.dll','GlassDesk.Rendering.dll','GlassDesk.NativeRenderingProbe.dll','GlassDesk.UI.dll','GlassDesk.Platform.dll','GlassDesk.Core.dll')) {
        [pscustomobject]@{name=$name;sha256=(Get-FileHash -LiteralPath (Join-Path $repo ('artifacts\native-rendering\probe\'+$name)) -Algorithm SHA256).Hash}
    })
    [ordered]@{completedUtc=[DateTimeOffset]::UtcNow.ToString('O');osBuild=[Environment]::OSVersion.Version.Build;
        passed=[int]$trx.TestRun.ResultSummary.Counters.passed;failed=[int]$trx.TestRun.ResultSummary.Counters.failed;
        # xUnit's TRX adapter can report notExecuted=0 while individual
        # UnitTestResult rows are NotExecuted. Preserve the actual skipped rows.
        notExecuted=@($trx.TestRun.Results.UnitTestResult | Where-Object outcome -eq 'NotExecuted').Count;
        notExecutedTests=@($trx.TestRun.Results.UnitTestResult | Where-Object outcome -eq 'NotExecuted' | ForEach-Object testName);
        payload=$payload;productionIntegrationReady=$false} | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath (Join-Path $repo 'artifacts\native-rendering\checks-receipt.json') -Encoding utf8
} finally { $env:GLASSDESK_NATIVE_RENDER_DLL = $oldDll }
