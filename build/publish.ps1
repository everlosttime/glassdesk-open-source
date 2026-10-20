# Produces a self-contained Windows x64 build of GlassDesk.
#
# ASCII only on purpose: Windows PowerShell 5.1 reads a .ps1 without a BOM using the system
# ANSI code page, so non-ASCII text in this file would fail to parse on a Chinese-locale
# machine.
#
# Self-contained means the user needs no separate .NET installation, which is what the plan
# requires. The result is a portable folder; packaging it into an installer is phase 7 work.

[CmdletBinding()]
param(
    [string]$DotnetPath,
    [string]$Configuration = 'Release',
    [string]$OutputDirectory,
    [string]$ProductVersion,
    [string]$AssemblyVersion,
    [string]$FileVersion,
    [string]$AcceptanceDataRoot,
    [string]$EvidenceDirectory = ('docs/evidence/' + [char]0x9636 + [char]0x6BB5 + '7')
)

$ErrorActionPreference = 'Stop'

# The product writes UTF-8. When PowerShell captures its output through a pipe it decodes
# using the system ANSI code page, which turns Chinese text into mojibake. Decoding as UTF-8
# keeps the self-check readable on a non-English machine.
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$repoRoot = Split-Path -Parent $PSScriptRoot

$hasVersionOverride = -not [string]::IsNullOrWhiteSpace($ProductVersion) -or
    -not [string]::IsNullOrWhiteSpace($AssemblyVersion) -or
    -not [string]::IsNullOrWhiteSpace($FileVersion)
if ($hasVersionOverride) {
    if ([string]::IsNullOrWhiteSpace($ProductVersion) -or [string]::IsNullOrWhiteSpace($AssemblyVersion) -or [string]::IsNullOrWhiteSpace($FileVersion)) {
        throw 'Pass ProductVersion, AssemblyVersion, and FileVersion together.'
    }
    if ($ProductVersion -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$' -or
        $AssemblyVersion -notmatch '^\d+\.\d+\.\d+\.\d+$' -or $FileVersion -notmatch '^\d+\.\d+\.\d+\.\d+$') {
        throw 'Invalid product, assembly, or file version.'
    }
}

if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repoRoot ("artifacts/publish/phase7-candidate-" + [guid]::NewGuid().ToString('N'))
}

function Resolve-Dotnet {
    param([string]$Explicit)

    if ($Explicit) {
        if (-not (Test-Path $Explicit)) { throw "dotnet not found at '$Explicit'." }
        return $Explicit
    }

    if ($env:GLASSDESK_DOTNET -and (Test-Path $env:GLASSDESK_DOTNET)) {
        return $env:GLASSDESK_DOTNET
    }

    $known = 'D:\dotnet10\dotnet.exe'
    if (Test-Path $known) { return $known }

    $onPath = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    throw 'No .NET SDK found. Pass -DotnetPath, set GLASSDESK_DOTNET, or install the SDK.'
}

$dotnet = Resolve-Dotnet -Explicit $DotnetPath

function ConvertTo-ProcessArgument {
    param([string]$Value)

    $builder = New-Object System.Text.StringBuilder
    [void]$builder.Append([char]34)
    $backslashes = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq [char]92) { $backslashes++; continue }
        if ($character -eq [char]34) {
            [void]$builder.Append([char]92, (2 * $backslashes) + 1)
            [void]$builder.Append([char]34)
        }
        else {
            [void]$builder.Append([char]92, $backslashes)
            [void]$builder.Append($character)
        }
        $backslashes = 0
    }
    if ($backslashes -gt 0) { [void]$builder.Append([char]92, 2 * $backslashes) }
    [void]$builder.Append([char]34)
    return $builder.ToString()
}

function Invoke-WaitingProcess {
    param(
        [string]$FilePath,
        [string[]]$Arguments,
        [string]$WorkingDirectory = (Get-Location).Path
    )

    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $FilePath
    $startInfo.Arguments = (($Arguments | ForEach-Object { ConvertTo-ProcessArgument -Value $_ }) -join ' ')
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false

    $process = New-Object System.Diagnostics.Process
    try {
        $process.StartInfo = $startInfo
        if (-not $process.Start()) { throw "Could not start '$FilePath'." }
        $process.WaitForExit()
        return $process.ExitCode
    }
    finally {
        $process.Dispose()
    }
}

function Assert-SafePublishTarget {
    param([string]$Path, [string]$ProtectedDataRoot, [string]$RepositoryRoot)

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $driveRoot = [System.IO.Path]::GetPathRoot($fullPath)
    if ($fullPath.TrimEnd('\').Equals($driveRoot.TrimEnd('\'), [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Publish output cannot be a volume root: '$fullPath'."
    }
    $resolved = $fullPath.TrimEnd('\')
    $publishRoot = [System.IO.Path]::GetFullPath((Join-Path $RepositoryRoot 'artifacts/publish')).TrimEnd('\')
    if (-not $resolved.StartsWith(($publishRoot + '\'), [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Publish output must be a new directory inside '$publishRoot'."
    }
    $protected = [System.IO.Path]::GetFullPath($ProtectedDataRoot).TrimEnd('\')
    $overlaps = $resolved.Equals($protected, [System.StringComparison]::OrdinalIgnoreCase) -or
        $resolved.StartsWith(($protected + '\'), [System.StringComparison]::OrdinalIgnoreCase) -or
        $protected.StartsWith(($resolved + '\'), [System.StringComparison]::OrdinalIgnoreCase)
    if ($overlaps) { throw "Publish output overlaps the current user's GlassDesk data directory: '$resolved'." }
    $existingTarget = Get-Item -LiteralPath $resolved -Force -ErrorAction SilentlyContinue
    if ($null -ne $existingTarget) { throw "Publish output already exists; choose a fresh directory: '$resolved'." }

    $current = [System.IO.Directory]::GetParent($resolved)
    while ($null -ne $current) {
        if (Test-Path -LiteralPath $current.FullName) {
            $item = Get-Item -LiteralPath $current.FullName -Force
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Publish output has a reparse-point ancestor: '$($current.FullName)'."
            }
        }
        $current = $current.Parent
    }
}

function Assert-SafeEvidenceDirectory {
    param([string]$Path, [string]$RepositoryRoot)

    $phase7Relative = 'docs/evidence/' + [char]0x9636 + [char]0x6BB5 + '7'
    $phase7Root = [System.IO.Path]::GetFullPath((Join-Path $RepositoryRoot $phase7Relative)).TrimEnd('\')
    $resolved = [System.IO.Path]::GetFullPath($Path).TrimEnd('\')
    $prefix = $phase7Root + '\'
    if (-not $resolved.Equals($phase7Root, [System.StringComparison]::OrdinalIgnoreCase) -and
        -not $resolved.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "EvidenceDirectory must be inside '$phase7Root'."
    }

    $current = $resolved
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "EvidenceDirectory has a reparse-point ancestor: '$current'."
            }
        }
        $parent = [System.IO.Directory]::GetParent($current)
        if ($null -eq $parent) { break }
        $current = $parent.FullName
    }
}

function Assert-SafeAcceptanceDataRoot {
    param([string]$Path)
    $tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\')
    $resolved = [System.IO.Path]::GetFullPath($Path).TrimEnd('\')
    if (-not $resolved.StartsWith(($tempRoot + '\'), [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "AcceptanceDataRoot must be a new directory under '$tempRoot'."
    }
    if (Test-Path -LiteralPath $resolved) { throw "AcceptanceDataRoot already exists: '$resolved'." }
    $current = [System.IO.Directory]::GetParent($resolved)
    while ($null -ne $current) {
        if (Test-Path -LiteralPath $current.FullName) {
            $item = Get-Item -LiteralPath $current.FullName -Force
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "AcceptanceDataRoot has a reparse-point ancestor: '$($current.FullName)'."
            }
        }
        $current = $current.Parent
    }
}

function Get-VersionOverrideArguments {
    param(
        [string]$ProductVersion,
        [string]$AssemblyVersion,
        [string]$FileVersion
    )

    return @(
        ('-p:Version=' + $ProductVersion)
        ('-p:AssemblyVersion=' + $AssemblyVersion)
        ('-p:FileVersion=' + $FileVersion)
        '-p:IncludeSourceRevisionInInformationalVersion=false'
    )
}

Push-Location $repoRoot
try {
    $protectedDataRoot = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::ApplicationData)) 'GlassDesk'
    Assert-SafePublishTarget -Path $OutputDirectory -ProtectedDataRoot $protectedDataRoot -RepositoryRoot $repoRoot
    Assert-SafeEvidenceDirectory -Path $EvidenceDirectory -RepositoryRoot $repoRoot
    $evidencePath = [System.IO.Path]::GetFullPath($EvidenceDirectory)
    if (-not (Test-Path -LiteralPath $evidencePath)) { New-Item -ItemType Directory -Path $evidencePath | Out-Null }
    $runEvidenceDirectory = Join-Path $evidencePath ("acceptance-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $runEvidenceDirectory | Out-Null
    if (-not $AcceptanceDataRoot) {
        $AcceptanceDataRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("GlassDesk-publish-selftest-" + [guid]::NewGuid().ToString('N'))
    }
    Assert-SafeAcceptanceDataRoot -Path $AcceptanceDataRoot
    $acceptanceDataRoot = [System.IO.Path]::GetFullPath($AcceptanceDataRoot)
    Write-Host "acceptanceDataRoot: $acceptanceDataRoot"
    Write-Host "== publish self-contained win-x64 ($Configuration) ==" -ForegroundColor Cyan
    Write-Host "output: $OutputDirectory"

    # ReadyToRun trades a little size for noticeably faster cold start, which matters for a
    # utility the user launches at logon.
    $publishArguments = @(
        'publish', 'src/GlassDesk.App/GlassDesk.App.csproj',
        '-c', $Configuration,
        '-r', 'win-x64',
        '--self-contained', 'true',
        '-p:PublishReadyToRun=true',
        '-p:DebugType=none',
        '-o', $OutputDirectory,
        '--nologo'
    )
    if ($hasVersionOverride) {
        $publishArguments += Get-VersionOverrideArguments -ProductVersion $ProductVersion -AssemblyVersion $AssemblyVersion -FileVersion $FileVersion
    }
    & $dotnet @publishArguments

    if ($LASTEXITCODE -ne 0) { throw "Publish failed with exit code $LASTEXITCODE." }

    Copy-Item -LiteralPath (Join-Path $repoRoot 'docs/third-party/unicode-license-v3.txt') `
        -Destination (Join-Path $OutputDirectory 'UNICODE-LICENSE.txt') -Force

    $exe = Join-Path $OutputDirectory 'GlassDesk.exe'
    if (-not (Test-Path $exe)) { throw "Expected GlassDesk.exe was not produced." }

    $size = (Get-ChildItem $OutputDirectory -Recurse -File | Measure-Object -Property Length -Sum).Sum
    $fileCount = (Get-ChildItem $OutputDirectory -Recurse -File).Count

    Write-Host ''
    Write-Host ("files: {0}" -f $fileCount)
    Write-Host ("size:  {0:N1} MB" -f ($size / 1MB))
    Write-Host ''
    Write-Host "executable: $exe" -ForegroundColor Green

    # Verify the published build actually runs rather than assuming it does.
    Write-Host ''
    Write-Host '== verify published build ==' -ForegroundColor Cyan
    $selfTestExit = Invoke-WaitingProcess -FilePath $exe -WorkingDirectory $repoRoot -Arguments @(
        '--selftest', '--acceptance-data-root', $acceptanceDataRoot,
        '--acceptance-report', (Join-Path $runEvidenceDirectory 'selftest.txt'))
    if ($selfTestExit -ne 0) { throw "Published build failed its self-check (exit code $selfTestExit)." }

    Write-Host ''
    Write-Host 'Publish succeeded.' -ForegroundColor Green
}
finally {
    Pop-Location
}
