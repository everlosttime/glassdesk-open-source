[CmdletBinding()]
param(
    [string]$ToolchainRoot = 'D:\Toolchains\GlassDesk-Native',
    [string]$OutputDirectory = '',
    [switch]$BuildProbe,
    [string]$DotnetPath = 'D:\dotnet10\dotnet.exe',
    [string]$LensSourcePath = ''
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repo 'artifacts\native-rendering' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
$version = '14.44.35207'
$compiler = Join-Path $ToolchainRoot "Microsoft.VC.14.44.17.14.Tools.HostX64.TargetX64.base\Contents\VC\Tools\MSVC\$version\bin\Hostx64\x64\cl.exe"
$headers = Join-Path $ToolchainRoot "Microsoft.VC.14.44.17.14.CRT.Headers.base\Contents\VC\Tools\MSVC\$version\include"
$crt = Join-Path $ToolchainRoot "Microsoft.VC.14.44.17.14.CRT.x64.Desktop.base\Contents\VC\Tools\MSVC\$version\lib\x64"
$crtNames = Join-Path $ToolchainRoot "Microsoft.VC.14.44.17.14.CRT.x64.Store.base\Contents\VC\Tools\MSVC\$version\lib\x64"
$sdk = Join-Path $ToolchainRoot 'microsoft.windows.sdk.cpp\c\Include\10.0.26100.0'
$libs = Join-Path $ToolchainRoot 'microsoft.windows.sdk.cpp.x64\c'
if (!(Test-Path -LiteralPath $compiler)) { throw "Install the pinned native toolchain first: $compiler" }
$signature = Get-AuthenticodeSignature -LiteralPath $compiler
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'CN=Microsoft Corporation,') { throw 'Compiler signature verification failed.' }
$null = New-Item -ItemType Directory -Force -Path $output
# Compile the existing production optical function for the native presenter too.
# Keep one source of truth; only the shader-model sampler/entry syntax differs.
if (!$LensSourcePath) { $LensSourcePath = Join-Path $repo 'src\GlassDesk.UI\Effects\TransitionGlass.hlsl' }
$lensSource = [IO.File]::ReadAllText([IO.Path]::GetFullPath($LensSourcePath))
$entry = 'float4 main(float2 uv : TEXCOORD) : COLOR'
$entryAt = $lensSource.IndexOf('float4 ReadBackdrop(',[StringComparison]::Ordinal)
if ($entryAt -lt 0) { $entryAt = $lensSource.IndexOf($entry,[StringComparison]::Ordinal) }
if ($entryAt -lt 0) { throw 'Production lens entry signature changed; review the native adapter.' }
$lensBody = $lensSource.Substring($entryAt).Replace($entry,'float4 Lens(float2 uv)').Replace('tex2D(Backdrop,','Source.Sample(LinearClamp,')
$lensBody = $lensBody.Replace('tex2Dlod(Backdrop, float4(saturate(uv), 0, 0))','Source.SampleLevel(LinearClamp, saturate(uv), 0)')
[IO.File]::WriteAllText((Join-Path $output 'WpfLensBody.h'), '// Generated from production TransitionGlass.hlsl. Kyant 2025 / Apache-2.0 adaptation.'+[Environment]::NewLine+'constexpr char GD_WpfLensBody[] = R"hlsl('+$lensBody+')hlsl";')
$oldInclude = $env:INCLUDE; $oldLib = $env:LIB
try {
    $env:INCLUDE = @($headers,"$sdk\ucrt","$sdk\shared","$sdk\um","$sdk\winrt") -join ';'
    $env:LIB = @($crt,$crtNames,"$libs\um\x64","$libs\ucrt\x64") -join ';'
    $common = @('/nologo','/std:c++20','/O2','/MT','/EHsc','/W4','/WX','/fp:strict','/permissive-',
        '/DUNICODE','/D_UNICODE','/DWIN32_LEAN_AND_MEAN','/DNOMINMAX','/D_WIN32_WINNT=0x0A00','/DGD_RENDER_EXPORTS')
    Push-Location $output
    try {
        & $compiler @common "/I$output" /LD "$repo\native\GlassDesk.Rendering.Native\Engine.cpp" "$repo\native\GlassDesk.Rendering.Native\DirectPresenter.cpp" /Fe:GlassDesk.Rendering.Native.dll /link /Brepro /INCREMENTAL:NO /DYNAMICBASE /NXCOMPAT d3d9.lib d3d11.lib dxgi.lib d3dcompiler.lib dcomp.lib d2d1.lib dwrite.lib user32.lib
        if ($LASTEXITCODE -ne 0) { throw 'Native rendering compilation failed.' }
    } finally { Pop-Location }
    $dll = Join-Path $output 'GlassDesk.Rendering.Native.dll'
    [ordered]@{ abi=1; minimumWindowsBuild=19041; architecture='x64'; compilerVersion=$version;
        compilerSigner=$signature.SignerCertificate.Subject; dll=(Get-FileHash -LiteralPath $dll -Algorithm SHA256);
        configuration='Release /O2 /MT /W4 /WX'; productionDefaultEnabled=$false } |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'build-receipt.json') -Encoding utf8
    Get-FileHash -LiteralPath $dll -Algorithm SHA256
    if ($BuildProbe) {
        & $DotnetPath publish (Join-Path $repo 'tools\GlassDesk.NativeRenderingProbe\GlassDesk.NativeRenderingProbe.csproj') -c Release -r win-x64 --self-contained true -o (Join-Path $output 'probe') "-p:NativeRenderingDll=$dll"
        if ($LASTEXITCODE -ne 0) { throw 'Native rendering probe publish failed.' }
    }
} finally { $env:INCLUDE=$oldInclude; $env:LIB=$oldLib }
