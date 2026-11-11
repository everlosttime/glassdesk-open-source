# Produces a local candidate with the existing installer identity and directory picker.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [string]$ProductVersion = '1.2.6',
    [string]$DotnetPath = 'D:\dotnet10\dotnet.exe',
    [string]$InnoCompilerPath = 'D:\Toolchains\InnoSetup-7.1.0-x64\Compiler\ISCC.exe',
    [switch]$Repackage
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'package-glass-input-fix.ps1') -OutputDirectory $OutputDirectory `
    -ProductVersion $ProductVersion -DotnetPath $DotnetPath -InnoCompilerPath $InnoCompilerPath -Repackage:$Repackage
if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw 'Packaging failed.' }
$reportPath = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) 'package-report.json'
$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
$report.baseline = 'installed 1.2.5'
$report.iteration = 'alt-tab-capture-carrier-candidate'
$source = (& git -C $repo rev-parse HEAD).Trim()
$report | Add-Member -NotePropertyName sourceCommit -NotePropertyValue $source
$report | Add-Member -NotePropertyName captureProtocol -NotePropertyValue 4
$report | Add-Member -NotePropertyName acceptanceStatus -NotePropertyValue 'candidate; see acceptance report and physical-device matrix'
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding utf8
