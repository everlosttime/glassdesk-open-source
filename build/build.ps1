# Builds the GlassDesk solution and runs its tests.
#
# ASCII only on purpose: Windows PowerShell 5.1 reads a .ps1 without a BOM using the system
# ANSI code page, so non-ASCII text in this file would fail to parse on a Chinese-locale
# machine.
#
# The SDK is resolved in this order, so the script works both on this machine (where the SDK
# lives outside the project tree) and anywhere a suitable SDK is on PATH:
#   1. -DotnetPath
#   2. the GLASSDESK_DOTNET environment variable
#   3. D:\dotnet10\dotnet.exe
#   4. dotnet on PATH

[CmdletBinding()]
param(
    [string]$DotnetPath,
    [string]$Configuration = 'Release',
    [switch]$SkipTests,
    [switch]$SkipAcceptance,
    # Phase 3/4 can start or activate applications and create visible windows; default is off.
    [switch]$InteractiveAcceptance,
    [switch]$UiAcceptance,
    # Desktop acceptance injects REAL mouse and keyboard events through SendInput, so it is
    # only allowed on an isolated desktop: a dedicated test account or a virtual machine.
    # It refuses to send anything unless GLASSDESK_ISOLATED_DESKTOP=1 is set, and that
    # refusal is a blocked run (exit code 2), never a pass.
    [switch]$DesktopAcceptance,
    [int]$DesktopRepeat = 3,
    [string]$DesktopReport,
    [string]$EvidenceDirectory = ('docs/evidence/' + [char]0x9636 + [char]0x6BB5 + '7')
)

$ErrorActionPreference = 'Stop'

# The product writes UTF-8. When PowerShell captures its output through a pipe it decodes
# using the system ANSI code page, which turns Chinese text into mojibake. Decoding as UTF-8
# keeps the self-check readable on a non-English machine.
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$repoRoot = Split-Path -Parent $PSScriptRoot

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

function Get-TestProjects {
    param([switch]$IncludeWpf)

    $projects = @(
        'tests/GlassDesk.Core.Tests/GlassDesk.Core.Tests.csproj',
        'tests/GlassDesk.AppServices.Tests/GlassDesk.AppServices.Tests.csproj'
    )
    if ($IncludeWpf) { $projects += 'tests/GlassDesk.Dock.Tests/GlassDesk.Dock.Tests.csproj' }
    return $projects
}

function Test-DesktopAcceptanceAllowed {
    param([switch]$DesktopAcceptance, [string]$IsolationFlag)
    return (-not $DesktopAcceptance -or $IsolationFlag -eq '1')
}

function Test-InteractiveAcceptanceAllowed {
    param([switch]$InteractiveAcceptance, [string]$IsolationFlag)
    return ($InteractiveAcceptance -and $IsolationFlag -eq '1')
}

if (-not (Test-DesktopAcceptanceAllowed -DesktopAcceptance:$DesktopAcceptance -IsolationFlag:$env:GLASSDESK_ISOLATED_DESKTOP)) {
    Write-Host 'Blocked: desktop acceptance needs an isolated desktop.' -ForegroundColor Yellow
    Write-Host 'Run it inside the test virtual machine and set GLASSDESK_ISOLATED_DESKTOP=1 first.' -ForegroundColor Yellow
    exit 2
}

if ($InteractiveAcceptance -and -not (Test-InteractiveAcceptanceAllowed -InteractiveAcceptance:$InteractiveAcceptance -IsolationFlag:$env:GLASSDESK_ISOLATED_DESKTOP)) {
    Write-Host 'Blocked: Phase 3/4 acceptance needs an isolated desktop.' -ForegroundColor Yellow
    Write-Host 'Pass -InteractiveAcceptance inside the test virtual machine with GLASSDESK_ISOLATED_DESKTOP=1.' -ForegroundColor Yellow
    exit 2
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

function New-AcceptanceDataRoot {
    param([string]$Label)
    Join-Path ([System.IO.Path]::GetTempPath()) ("GlassDesk-build-" + $Label + "-" + [guid]::NewGuid().ToString('N'))
}

# global.json pins the SDK, but it can only reject a mismatch, not explain it. Report the
# version up front so a failure is easy to diagnose.
$sdks = & $dotnet --list-sdks
Write-Host "dotnet: $dotnet"
$sdks | ForEach-Object { Write-Host "  sdk $_" }

Push-Location $repoRoot
try {
    Assert-SafeEvidenceDirectory -Path $EvidenceDirectory -RepositoryRoot $repoRoot
    $evidencePath = [System.IO.Path]::GetFullPath($EvidenceDirectory)
    if (-not (Test-Path -LiteralPath $evidencePath)) { New-Item -ItemType Directory -Path $evidencePath | Out-Null }
    $runEvidenceDirectory = Join-Path $evidencePath ("acceptance-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $runEvidenceDirectory | Out-Null
    if (-not $DesktopReport) { $DesktopReport = Join-Path $runEvidenceDirectory 'phase6-desktop.json' }

    Write-Host ''
    Write-Host "== build ($Configuration) ==" -ForegroundColor Cyan

    & $dotnet build 'GlassDesk.slnx' -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }

    if (-not $SkipTests) {
        Write-Host ''
        Write-Host '== test ==' -ForegroundColor Cyan

        # Core and app-service tests do not create product windows. Dock tests create real
        # WPF windows, so include them only when the caller explicitly requests UI acceptance.
        $testProjects = Get-TestProjects -IncludeWpf:$UiAcceptance

        foreach ($project in $testProjects) {
            & $dotnet test $project -c $Configuration --nologo
            if ($LASTEXITCODE -ne 0) { throw "Tests failed in $project with exit code $LASTEXITCODE." }
        }
    }

    Write-Host ''
    Write-Host '== self-check ==' -ForegroundColor Cyan

    # The self-check is the automated form of the "starts and exits cleanly" requirement: it
    # reports the OS build, which glass capabilities are available, where data is stored, and
    # whether that data is healthy. Exit code 0 means every check passed.
    $selfTest = Join-Path $repoRoot "src/GlassDesk.App/bin/$Configuration/net10.0-windows/GlassDesk.exe"
    if (Test-Path $selfTest) {
        $selfTestExit = Invoke-WaitingProcess -FilePath $selfTest -WorkingDirectory $repoRoot -Arguments @(
            '--selftest', '--acceptance-data-root', (New-AcceptanceDataRoot 'selftest'),
            '--acceptance-report', (Join-Path $runEvidenceDirectory 'selftest.txt'))
        if ($selfTestExit -ne 0) { throw "Self-check reported failures (exit code $selfTestExit)." }

        # Phase 3's delivery is the shared application library plus the import and launch
        # flows. The acceptance run below is the automated form of that delivery's criteria:
        # it builds the Chinese/space/broken samples, imports all four kinds plus web,
        # kills a target and checks the entry survives and can be relocated, scans the real
        # Start Menu, and grows the library past 500 entries. It only touches apps.json and
        # the icon cache.
        #
        # Phase 4's acceptance accumulates per batch: window identity matching, the running
        # tracker and so on. Both runs execute in their own isolated temp directory and
        # never touch the user's real library.
        if (-not $SkipAcceptance) {
            # These accumulated phases launch/activate applications and create visible WPF
            # windows. Keep ordinary builds non-interactive; the opt-in is guarded above.
            if ($InteractiveAcceptance) {
                Write-Host ''
                Write-Host '== application library acceptance ==' -ForegroundColor Cyan

                $phase3ContactSheet = Join-Path $runEvidenceDirectory 'phase3-contact-sheet.png'
                $phase3Report = Join-Path $runEvidenceDirectory 'phase3-regression.log'
                $phase3Exit = Invoke-WaitingProcess -FilePath $selfTest -WorkingDirectory $repoRoot -Arguments @(
                    '--phase3-acceptance', $phase3ContactSheet, $phase3Report, (New-AcceptanceDataRoot 'phase3'))
                if ($phase3Exit -ne 0) { throw "Application library acceptance reported failures (exit code $phase3Exit)." }

                Write-Host ''
                Write-Host '== phase 4 acceptance ==' -ForegroundColor Cyan

                $phase4Exit = Invoke-WaitingProcess -FilePath $selfTest -WorkingDirectory $repoRoot -Arguments @(
                    '--phase4-acceptance', (Join-Path $runEvidenceDirectory 'phase4-regression.txt'), (New-AcceptanceDataRoot 'phase4'))
                if ($phase4Exit -ne 0) { throw "Phase 4 acceptance reported failures (exit code $phase4Exit)." }
            }

            Write-Host ''
            Write-Host '== phase 5 acceptance ==' -ForegroundColor Cyan
            $phase5Exit = Invoke-WaitingProcess -FilePath $selfTest -WorkingDirectory $repoRoot -Arguments @(
                '--phase5-acceptance', (Join-Path $runEvidenceDirectory 'phase5-command.log'), (New-AcceptanceDataRoot 'phase5'))
            if ($phase5Exit -ne 0) { throw "Phase 5 acceptance reported failures (exit code $phase5Exit)." }

            Write-Host ''
            Write-Host '== phase 6 acceptance ==' -ForegroundColor Cyan
            $phase6Exit = Invoke-WaitingProcess -FilePath $selfTest -WorkingDirectory $repoRoot -Arguments @(
                '--phase6-acceptance', (Join-Path $runEvidenceDirectory 'phase6-cli.json'), (New-AcceptanceDataRoot 'phase6'))
            if ($phase6Exit -ne 0) { throw "Phase 6 acceptance reported failures (exit code $phase6Exit)." }
        }
    }
    else {
        Write-Warning "Self-check binary not found at $selfTest; skipping."
    }

    Write-Host ''
    if ($UiAcceptance -or $DesktopAcceptance) {
        Write-Host '== UI integration acceptance ==' -ForegroundColor Cyan

        # Run the current phase's launcher acceptance first so an unrelated environment
        # blocker in the accumulated Dock regression cannot prevent its evidence from being
        # produced.
        & $dotnet run --project 'tests/GlassDesk.LauncherAcceptance' -c $Configuration -- --report (Join-Path $runEvidenceDirectory 'phase5-wpf.json')
        if ($LASTEXITCODE -ne 0) { throw "Launcher UI integration acceptance failed (exit code $LASTEXITCODE)." }

        & $dotnet run --project 'tests/GlassDesk.UiAcceptance' -c $Configuration -- --report (Join-Path $runEvidenceDirectory 'phase4-ui-regression.json')
        if ($LASTEXITCODE -ne 0) { throw "UI integration acceptance failed (exit code $LASTEXITCODE)." }

        & $dotnet run --project 'tests/GlassDesk.InteractionAcceptance' -c $Configuration -- --report (Join-Path $runEvidenceDirectory 'phase6-wpf.json')
        if ($LASTEXITCODE -ne 0) { throw "Phase 6 WPF acceptance failed (exit code $LASTEXITCODE)." }
    }

    if ($DesktopAcceptance) {
        Write-Host ''
        Write-Host '== desktop acceptance (real input) ==' -ForegroundColor Cyan

        & $dotnet run --project 'tests/GlassDesk.DesktopAcceptance' -c $Configuration -- `
            --i-am-on-isolated-desktop `
            --repeat $DesktopRepeat `
            --report $DesktopReport

        # 1 = the product failed a case; 2 = blocked or not run. Both must stay distinct,
        # so the code is passed through instead of being flattened into a generic failure.
        if ($LASTEXITCODE -ne 0) {
            Write-Host "Desktop acceptance exit code $LASTEXITCODE." -ForegroundColor Red
            exit $LASTEXITCODE
        }
    }

    Write-Host 'All steps succeeded.' -ForegroundColor Green
}
finally {
    Pop-Location
}
