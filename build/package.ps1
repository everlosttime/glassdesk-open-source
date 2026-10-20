# Builds a local, unsigned GlassDesk ZIP and Inno Setup release from a fresh publish.
# Keep this script ASCII so Windows PowerShell 5.1 can read it on any locale.

[CmdletBinding()]
param(
    [string]$DotnetPath,
    [Parameter(Mandatory = $true)]
    [string]$InnoCompilerPath,
    [string]$InnoInstallerPath,
    [string]$ReleaseManifest,
    [string]$OutputDirectory,
    [string]$EvidenceDirectory = ('docs/evidence/' + [char]0x9636 + [char]0x6BB5 + '7')
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ReleaseManifest)) {
    $ReleaseManifest = Join-Path $repoRoot 'packaging/release.json'
}
$startedAtUtc = [DateTime]::UtcNow
$runId = [guid]::NewGuid().ToString('N')
$runEvidenceDirectory = $null
$candidateDirectory = $null
$publishDirectory = $null
$acceptanceDataRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("GlassDesk-package-selftest-" + $runId)
$runStatus = 'Failed'
$runError = $null
$runExitCode = 1
$artifactRecords = @()

function Resolve-Dotnet {
    param([string]$Explicit)
    if ($Explicit) {
        if (-not (Test-Path -LiteralPath $Explicit -PathType Leaf)) { throw "dotnet not found at '$Explicit'." }
        return [System.IO.Path]::GetFullPath($Explicit)
    }
    if ($env:GLASSDESK_DOTNET -and (Test-Path -LiteralPath $env:GLASSDESK_DOTNET -PathType Leaf)) {
        return [System.IO.Path]::GetFullPath($env:GLASSDESK_DOTNET)
    }
    $known = 'D:\dotnet10\dotnet.exe'
    if (Test-Path -LiteralPath $known -PathType Leaf) { return $known }
    $onPath = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    throw 'No .NET SDK found. Pass -DotnetPath, set GLASSDESK_DOTNET, or install the SDK.'
}

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

function Invoke-CapturedProcess {
    param([string]$FilePath, [string[]]$Arguments, [string]$WorkingDirectory, [string]$LogPath)
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $FilePath
    $startInfo.Arguments = (($Arguments | ForEach-Object { ConvertTo-ProcessArgument -Value $_ }) -join ' ')
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $process = New-Object System.Diagnostics.Process
    try {
        $process.StartInfo = $startInfo
        if (-not $process.Start()) { throw "Could not start '$FilePath'." }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
        if ($LogPath) {
            [System.IO.File]::WriteAllText($LogPath, $output, (New-Object System.Text.UTF8Encoding($false)))
        }
        return [pscustomobject]@{ ExitCode = $process.ExitCode; Output = $output }
    }
    finally { $process.Dispose() }
}

function Get-Sha256HexFromFile {
    param([string]$Path)
    $stream = [System.IO.File]::OpenRead($Path)
    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    try { return ([System.BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-', '').ToLowerInvariant() }
    finally { $algorithm.Dispose(); $stream.Dispose() }
}

function Get-InnoCompileArguments {
    param(
        [string]$AppId,
        [string]$ProductVersion,
        [string]$FileVersion,
        [string]$PackageBaseName,
        [string]$OutputDirectory,
        [string]$ScriptPath
    )

    return @(
        ('/DAppIdValue=' + $AppId)
        ('/DProductVersion=' + $ProductVersion)
        ('/DFileVersion=' + $FileVersion)
        ('/DPackageBaseName=' + $PackageBaseName)
        ('/O' + $OutputDirectory)
        $ScriptPath
    )
}

function Write-JsonFile {
    param([string]$Path, [object]$Value)
    $json = ConvertTo-Json -InputObject $Value -Depth 20
    [System.IO.File]::WriteAllText($Path, $json + [Environment]::NewLine, (New-Object System.Text.UTF8Encoding($false)))
}

function Test-PathOverlap {
    param([string]$First, [string]$Second)
    $a = [System.IO.Path]::GetFullPath($First).TrimEnd('\')
    $b = [System.IO.Path]::GetFullPath($Second).TrimEnd('\')
    return $a.Equals($b, [System.StringComparison]::OrdinalIgnoreCase) -or
        $a.StartsWith(($b + '\'), [System.StringComparison]::OrdinalIgnoreCase) -or
        $b.StartsWith(($a + '\'), [System.StringComparison]::OrdinalIgnoreCase)
}

function Assert-SafeOutputTarget {
    param([string]$Path, [string]$AllowedRoot, [string]$ProtectedDataRoot)
    $resolved = [System.IO.Path]::GetFullPath($Path).TrimEnd('\')
    $driveRoot = [System.IO.Path]::GetPathRoot($resolved)
    if ($resolved.Equals($driveRoot.TrimEnd('\'), [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Output target cannot be a volume root: '$resolved'."
    }
    $root = [System.IO.Path]::GetFullPath($AllowedRoot).TrimEnd('\')
    if (-not $resolved.StartsWith(($root + '\'), [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Output target must be a new child of '$root'."
    }
    if (Test-PathOverlap -First $resolved -Second $ProtectedDataRoot) {
        throw "Output target overlaps GlassDesk user data: '$resolved'."
    }
    if (Test-Path -LiteralPath $resolved) { throw "Output target already exists: '$resolved'." }
    $current = [System.IO.Directory]::GetParent($resolved)
    while ($null -ne $current) {
        if (Test-Path -LiteralPath $current.FullName) {
            $item = Get-Item -LiteralPath $current.FullName -Force
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Output target has a reparse-point ancestor: '$($current.FullName)'."
            }
        }
        $current = $current.Parent
    }
}

function Assert-SafeEvidenceDirectory {
    param([string]$Path)
    $allowedRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot ('docs/evidence/' + [char]0x9636 + [char]0x6BB5 + '7'))).TrimEnd('\')
    $resolved = [System.IO.Path]::GetFullPath($Path).TrimEnd('\')
    if (-not $resolved.Equals($allowedRoot, [System.StringComparison]::OrdinalIgnoreCase) -and
        -not $resolved.StartsWith(($allowedRoot + '\'), [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "EvidenceDirectory must be inside '$allowedRoot'."
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

function Assert-PayloadFilePath {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path) -or $Path.Contains(':') -or $Path.Contains('\') -or $Path.StartsWith('/')) {
        throw "Unsafe payload path: '$Path'."
    }
    foreach ($segment in $Path.Split('/')) {
        if ([string]::IsNullOrWhiteSpace($segment) -or $segment -eq '.' -or $segment -eq '..' -or
            $segment.EndsWith('.') -or $segment.EndsWith(' ') -or $segment -match '[<>"|?*\x00-\x1f]') {
            throw "Unsafe payload path: '$Path'."
        }
    }
    if ($Path -match '(?i)(\.pdb$|\.cs$|\.csproj$|\.slnx?$|\.props$|\.targets$|\.ps1$|\.psm1$|\.psd1$|\.nupkg$|project\.assets\.json$|\.Tests?\.dll$|\.TestAdapter\.dll$|^testhost|^Microsoft\.TestPlatform|^Microsoft\.CodeCoverage|^Microsoft\.CodeAnalysis|^Microsoft\.Build|^NuGet\.|^dotnet\.exe$|^csc\.dll$|^vbc\.dll$|^MSBuild\.dll$)') {
        throw "Forbidden payload file: '$Path'."
    }
    foreach ($segment in $Path.Split('/')) {
        if ($segment -match '^(?i)(\.git|obj|tests?|test-results|sdk|ref|referenceassemblies|src|packages|node_modules)$') {
            throw "Forbidden payload path: '$Path'."
        }
    }
}

function Assert-NoReparsePayload {
    param([string]$Root)
    $pending = New-Object 'System.Collections.Generic.Stack[string]'
    $pending.Push($Root)
    while ($pending.Count -gt 0) {
        $current = $pending.Pop()
        foreach ($child in Get-ChildItem -LiteralPath $current -Force) {
            if (($child.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Publish output contains a reparse point: '$($child.FullName)'."
            }
            if ($child.PSIsContainer) { $pending.Push($child.FullName) }
        }
    }
}

function Get-PayloadRecords {
    param([string]$Root)
    Assert-NoReparsePayload -Root $Root
    $prefix = [System.IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $records = @()
    foreach ($file in Get-ChildItem -LiteralPath $Root -Recurse -File -Force) {
        $relative = $file.FullName.Substring($prefix.Length).Replace('\', '/')
        Assert-PayloadFilePath -Path $relative
        $records += [pscustomobject]@{
            relativePath = $relative
            length = [long]$file.Length
            sha256 = Get-Sha256HexFromFile -Path $file.FullName
        }
    }
    if ($records.Count -eq 0) { throw 'Publish output is empty.' }
    return @($records | Sort-Object { $_.relativePath.ToLowerInvariant() }, relativePath)
}

function Assert-ReleaseManifest {
    param([object]$Release)
    if ($Release.schemaVersion -ne 1 -or $Release.productName -ne 'GlassDesk' -or $Release.status -ne 'local-release') {
        throw 'Release manifest must describe a local GlassDesk release.'
    }
    if ($Release.productVersion -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$' -or
        $Release.assemblyVersion -notmatch '^\d+\.\d+\.\d+\.\d+$' -or $Release.fileVersion -notmatch '^\d+\.\d+\.\d+\.\d+$') {
        throw 'Release manifest contains an invalid product, assembly, or file version.'
    }
    if ($Release.rid -ne 'win-x64' -or $Release.packageBaseName -ne ('GlassDesk-' + $Release.productVersion + '-win-x64')) {
        throw 'Release manifest package name or RID is inconsistent.'
    }
    if ($Release.appId -ne 'GlassDesk.Independent' -or $Release.dotnetSdkVersion -ne '10.0.401' -or
        $Release.runtimeVersion -ne '10.0.12' -or $Release.innoVersion -ne '7.1.0' -or
        $Release.innoSetupInstallerFile -ne 'innosetup-7.1.0-x64.exe' -or
        ([string]$Release.innoSetupInstallerSha256 -notmatch '^[0-9a-fA-F]{64}$')) {
        throw 'Release manifest toolchain or stable AppId is inconsistent with the approved candidate.'
    }
}

function Assert-RequiredLicenseSources {
    param([string]$DotnetRoot, [string]$Stage)
    $versioned = Join-Path $repoRoot 'docs/third-party/dotnet-10.0.12'
    $thirdPartySummary = Join-Path $repoRoot ('docs/' + [char]0x7B2C + [char]0x4E09 + [char]0x65B9 + [char]0x58F0 + [char]0x660E + '.md')
    $copyMap = @(
        [pscustomobject]@{ Source = (Join-Path $DotnetRoot 'LICENSE.txt'); Expected = (Join-Path $versioned 'LICENSE.txt'); Target = (Join-Path $Stage 'LICENSE.txt') },
        [pscustomobject]@{ Source = (Join-Path $DotnetRoot 'ThirdPartyNotices.txt'); Expected = (Join-Path $versioned 'ThirdPartyNotices.txt'); Target = (Join-Path $Stage 'ThirdPartyNotices.txt') },
        [pscustomobject]@{ Source = (Join-Path $repoRoot 'docs/third-party/windows-sdk-license-en-us.txt'); Expected = $null; Target = (Join-Path $Stage 'WINDOWS-SDK-LICENSE.txt') },
        [pscustomobject]@{ Source = (Join-Path $repoRoot 'docs/third-party/inno-setup-license.txt'); Expected = $null; Target = (Join-Path $Stage 'INNO-SETUP-LICENSE.txt') },
        [pscustomobject]@{ Source = $thirdPartySummary; Expected = $null; Target = (Join-Path $Stage 'THIRD-PARTY-NOTICES.md') },
        [pscustomobject]@{ Source = (Join-Path $repoRoot 'packaging/RELEASE-NOTES.md'); Expected = $null; Target = (Join-Path $Stage 'RELEASE-NOTES.md') }
    )
    foreach ($row in $copyMap) {
        $source = [string]$row.Source
        $expectedCopy = $row.Expected
        $target = [string]$row.Target
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Required license or notice source is missing: '$source'." }
        if ($expectedCopy) {
            if (-not (Test-Path -LiteralPath $expectedCopy -PathType Leaf) -or
                (Get-Sha256HexFromFile -Path $source) -ne (Get-Sha256HexFromFile -Path $expectedCopy)) {
                throw "Installed .NET notice does not match the pinned runtime asset: '$source'."
            }
        }
        if (Test-Path -LiteralPath $target) { throw "Publish output unexpectedly already contains '$([System.IO.Path]::GetFileName($target))'." }
        Copy-Item -LiteralPath $source -Destination $target
    }
    $unicodeTarget = Join-Path $Stage 'UNICODE-LICENSE.txt'
    if (-not (Test-Path -LiteralPath $unicodeTarget -PathType Leaf) -or
        (Get-Sha256HexFromFile -Path $unicodeTarget) -ne (Get-Sha256HexFromFile -Path (Join-Path $repoRoot 'docs/third-party/unicode-license-v3.txt'))) {
        throw 'The publish output Unicode license does not match the pinned source.'
    }
}

try {
    if (-not (Test-Path -LiteralPath $ReleaseManifest -PathType Leaf)) { throw "Release manifest not found: '$ReleaseManifest'." }
    $releasePath = [System.IO.Path]::GetFullPath($ReleaseManifest)
    $allowedReleaseRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'packaging')).TrimEnd('\') + '\'
    if (-not $releasePath.StartsWith($allowedReleaseRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'ReleaseManifest must be inside the repository packaging directory.'
    }
    $release = Get-Content -LiteralPath $releasePath -Raw | ConvertFrom-Json
    Assert-ReleaseManifest -Release $release

    $locatorSource = Get-Content -LiteralPath (Join-Path $repoRoot 'src/GlassDesk.Platform/Installation/InstallationLocator.cs') -Raw
    if ($locatorSource -notmatch ('AppId\s*=\s*"' + [regex]::Escape([string]$release.appId) + '"')) {
        throw 'Release AppId does not match the application installation locator.'
    }

    $dotnet = Resolve-Dotnet -Explicit $DotnetPath
    $dotnetDirectory = Split-Path -Parent $dotnet
    $compiler = [System.IO.Path]::GetFullPath($InnoCompilerPath)
    if (-not [System.IO.Path]::GetFileName($compiler).Equals('ISCC.exe', [System.StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw 'InnoCompilerPath must point to an installed ISCC.exe.' }

    $toolDirectory = Split-Path -Parent $compiler
    $toolRoot = Split-Path -Parent $toolDirectory
    if (-not $InnoInstallerPath) { $InnoInstallerPath = Join-Path $toolRoot $release.innoSetupInstallerFile }
    $innoInstaller = [System.IO.Path]::GetFullPath($InnoInstallerPath)
    $innoSignaturePath = $innoInstaller + '.issig'
    if (-not (Test-Path -LiteralPath $innoInstaller -PathType Leaf) -or -not (Test-Path -LiteralPath $innoSignaturePath -PathType Leaf)) {
        throw 'The pinned Inno Setup installer and its official .issig file are required beside the installed compiler.'
    }
    $installerHash = Get-Sha256HexFromFile -Path $innoInstaller
    if ($installerHash -ne ([string]$release.innoSetupInstallerSha256).ToLowerInvariant()) { throw 'Inno Setup installer SHA-256 does not match release.json.' }
    $authenticode = Get-AuthenticodeSignature -LiteralPath $innoInstaller
    $publisher = $authenticode.SignerCertificate.GetNameInfo([System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false)
    if ($authenticode.Status -ne 'Valid' -or $publisher -ne 'Pyrsys B.V.') { throw 'Inno Setup installer Authenticode publisher verification failed.' }
    $signatureText = Get-Content -LiteralPath $innoSignaturePath -Raw
    if ($signatureText -notmatch ('(?m)^file-name\s+"' + [regex]::Escape([string]$release.innoSetupInstallerFile) + '"\r?$') -or
        $signatureText -notmatch ('(?m)^file-size\s+' + (Get-Item -LiteralPath $innoInstaller).Length + '\r?$') -or
        $signatureText -notmatch ('(?m)^file-hash\s+' + [regex]::Escape($installerHash) + '\r?$')) {
        throw 'Inno Setup .issig metadata does not match the downloaded installer.'
    }
    $compilerSignature = Get-AuthenticodeSignature -LiteralPath $compiler
    $compilerPublisher = $compilerSignature.SignerCertificate.GetNameInfo([System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false)
    if ($compilerSignature.Status -ne 'Valid' -or $compilerPublisher -ne 'Pyrsys B.V.') { throw 'Installed Inno compiler Authenticode verification failed.' }
    $releaseHistory = Join-Path $toolDirectory 'whatsnew.htm'
    if (-not (Test-Path -LiteralPath $releaseHistory -PathType Leaf) -or
        (Get-Content -LiteralPath $releaseHistory -Raw) -notmatch '7\.1\.0\s*</span>\s*<span class="date">\(2026-08-12\)') {
        throw 'Installed Inno Setup compiler does not match the pinned 7.1.0 release.'
    }
    $pinnedInnoLicense = Join-Path $repoRoot 'docs/third-party/inno-setup-license.txt'
    $installedInnoLicense = Join-Path $toolDirectory 'license.txt'
    if (-not (Test-Path -LiteralPath $installedInnoLicense -PathType Leaf) -or
        (Get-Sha256HexFromFile -Path $installedInnoLicense) -ne (Get-Sha256HexFromFile -Path $pinnedInnoLicense)) {
        throw 'Installed Inno Setup license does not match the pinned license asset.'
    }

    $protectedDataRoot = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::ApplicationData)) 'GlassDesk'
    if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repoRoot ("artifacts/package/" + $release.packageBaseName + '-' + $runId) }
    $candidateDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
    $packageRoot = Join-Path $repoRoot 'artifacts/package'
    Assert-SafeOutputTarget -Path $candidateDirectory -AllowedRoot $packageRoot -ProtectedDataRoot $protectedDataRoot
    $publishDirectory = Join-Path $repoRoot ("artifacts/publish/phase7-package-" + $runId)
    Assert-SafeOutputTarget -Path $publishDirectory -AllowedRoot (Join-Path $repoRoot 'artifacts/publish') -ProtectedDataRoot $protectedDataRoot
    Assert-SafeEvidenceDirectory -Path $EvidenceDirectory
    $evidenceRoot = [System.IO.Path]::GetFullPath($EvidenceDirectory)
    if (-not (Test-Path -LiteralPath $evidenceRoot)) { New-Item -ItemType Directory -Path $evidenceRoot | Out-Null }
    $runEvidenceDirectory = Join-Path $evidenceRoot ("batch-d-package-" + $runId)
    New-Item -ItemType Directory -Path $runEvidenceDirectory | Out-Null

    $sdkResult = Invoke-CapturedProcess -FilePath $dotnet -Arguments @('--version') -WorkingDirectory $repoRoot -LogPath (Join-Path $runEvidenceDirectory 'dotnet-version.log')
    if ($sdkResult.ExitCode -ne 0 -or $sdkResult.Output.Trim() -ne $release.dotnetSdkVersion) { throw 'The selected .NET SDK version does not match release.json.' }
    $runtimeResult = Invoke-CapturedProcess -FilePath $dotnet -Arguments @('--list-runtimes') -WorkingDirectory $repoRoot -LogPath (Join-Path $runEvidenceDirectory 'dotnet-runtimes.log')
    if ($runtimeResult.ExitCode -ne 0 -or $runtimeResult.Output -notmatch ('Microsoft\.NETCore\.App\s+' + [regex]::Escape([string]$release.runtimeVersion)) -or
        $runtimeResult.Output -notmatch ('Microsoft\.WindowsDesktop\.App\s+' + [regex]::Escape([string]$release.runtimeVersion))) {
        throw 'The selected SDK installation does not contain the pinned .NET and Windows Desktop runtime version.'
    }
    $gitHead = (& git -C $repoRoot rev-parse HEAD 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $gitHead -notmatch '^[0-9a-fA-F]{40}$') { throw 'Could not record repository HEAD.' }
    $gitStatus = (& git -C $repoRoot status --porcelain=v1 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0) { throw 'Could not record working-tree status.' }
    $dirty = -not [string]::IsNullOrWhiteSpace($gitStatus)

    $gh = Get-Command gh -ErrorAction SilentlyContinue
    $attestationStatus = 'NotRun'
    if ($gh) {
        $attestationResult = Invoke-CapturedProcess -FilePath $gh.Source -Arguments @('release', 'verify-asset', $innoInstaller, '--repo', 'jrsoftware/issrc') -WorkingDirectory $repoRoot -LogPath (Join-Path $runEvidenceDirectory 'inno-release-attestation.log')
        if ($attestationResult.ExitCode -ne 0) { throw 'GitHub release attestation verification failed for the Inno Setup installer.' }
        $attestationStatus = 'Verified'
    }

    $artifactParent = Split-Path -Parent $candidateDirectory
    if (-not (Test-Path -LiteralPath $artifactParent)) { New-Item -ItemType Directory -Path $artifactParent -Force | Out-Null }
    $stageParent = Split-Path -Parent $publishDirectory
    if (-not (Test-Path -LiteralPath $stageParent)) { New-Item -ItemType Directory -Path $stageParent -Force | Out-Null }
    $stage = $publishDirectory
    # Windows PowerShell 5.1 can transcode non-ASCII native arguments; dotnet receives this ASCII relative path from the repository working directory.
    $publishOutputRelative = 'artifacts/publish/phase7-package-' + $runId
    $publishLog = Join-Path $runEvidenceDirectory 'publish.log'
    $publishScript = Join-Path $PSScriptRoot 'publish.ps1'
    $powerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $publishOutput = @()
    try {
        $publishOutput = & $publishScript `
            -DotnetPath $dotnet `
            -OutputDirectory $publishOutputRelative `
            -EvidenceDirectory $runEvidenceDirectory `
            -ProductVersion ([string]$release.productVersion) `
            -AssemblyVersion ([string]$release.assemblyVersion) `
            -FileVersion ([string]$release.fileVersion) `
            -AcceptanceDataRoot $acceptanceDataRoot *>&1
    }
    finally {
        $publishText = ($publishOutput | Out-String)
        [System.IO.File]::WriteAllText($publishLog, $publishText, (New-Object System.Text.UTF8Encoding($false)))
    }
    $publishExitCode = $LASTEXITCODE
    if ($publishExitCode -ne 0) { throw "Self-contained publish failed with exit code $publishExitCode." }

    $publishedExe = Join-Path $stage 'GlassDesk.exe'
    if (-not (Test-Path -LiteralPath $publishedExe -PathType Leaf)) { throw 'Publish output is missing GlassDesk.exe.' }
    $exeVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($publishedExe)
    if ($exeVersion.FileVersion -ne $release.fileVersion -or $exeVersion.ProductVersion -ne $release.productVersion) {
        throw "Published executable version mismatch: file='$($exeVersion.FileVersion)' product='$($exeVersion.ProductVersion)'."
    }
    Assert-RequiredLicenseSources -DotnetRoot $dotnetDirectory -Stage $stage
    $payloadRecords = Get-PayloadRecords -Root $stage

    if (-not (Test-Path -LiteralPath $candidateDirectory)) { New-Item -ItemType Directory -Path $candidateDirectory | Out-Null }
    $zipPath = Join-Path $candidateDirectory ($release.packageBaseName + '.zip')
    $setupPath = Join-Path $candidateDirectory ($release.packageBaseName + '-Setup.exe')
    $payloadManifestPath = Join-Path $candidateDirectory 'payload-manifest.json'
    $toolchainPath = Join-Path $candidateDirectory 'toolchain.json'
    $buildReportPath = Join-Path $candidateDirectory 'build-report.json'
    $installerLicenseCopy = Join-Path $candidateDirectory 'INNO-SETUP-LICENSE.txt'
    $checksumsPath = Join-Path $candidateDirectory 'SHA256SUMS.json'

    $payloadManifest = [ordered]@{
        schemaVersion = 1
        productName = $release.productName
        productVersion = $release.productVersion
        assemblyVersion = $release.assemblyVersion
        fileVersion = $release.fileVersion
        appId = $release.appId
        rid = $release.rid
        files = $payloadRecords
    }
    Write-JsonFile -Path $payloadManifestPath -Value $payloadManifest
    Copy-Item -LiteralPath $pinnedInnoLicense -Destination $installerLicenseCopy

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)

    $previousStage = $env:GLASSDESK_PACKAGE_STAGE
    $env:GLASSDESK_PACKAGE_STAGE = $stage
    try {
        $isccArguments = Get-InnoCompileArguments `
            -AppId ([string]$release.appId) `
            -ProductVersion ([string]$release.productVersion) `
            -FileVersion ([string]$release.fileVersion) `
            -PackageBaseName ([string]$release.packageBaseName) `
            -OutputDirectory $candidateDirectory `
            -ScriptPath (Join-Path $repoRoot 'packaging/GlassDesk.iss')
        $innoResult = Invoke-CapturedProcess -FilePath $compiler -Arguments $isccArguments -WorkingDirectory $repoRoot -LogPath (Join-Path $runEvidenceDirectory 'inno-compile.log')
    }
    finally {
        if ($null -eq $previousStage) { Remove-Item Env:\GLASSDESK_PACKAGE_STAGE -ErrorAction SilentlyContinue }
        else { $env:GLASSDESK_PACKAGE_STAGE = $previousStage }
    }
    if ($innoResult.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $setupPath -PathType Leaf)) {
        throw "Inno Setup compilation failed with exit code $($innoResult.ExitCode)."
    }
    $setupVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($setupPath)
    if ($setupVersion.FileVersionRaw.ToString() -ne [string]$release.fileVersion -or
        $setupVersion.ProductVersionRaw.ToString() -ne [string]$release.fileVersion -or
        $setupVersion.FileVersion.TrimEnd() -ne [string]$release.fileVersion -or
        $setupVersion.ProductVersion.TrimEnd() -ne [string]$release.productVersion) {
        throw "Setup executable version mismatch: file='$($setupVersion.FileVersion)' product='$($setupVersion.ProductVersion)'."
    }

    $toolchain = [ordered]@{
        schemaVersion = 1
        dotnetPath = $dotnet
        sdkVersion = [string]$release.dotnetSdkVersion
        runtimeVersion = [string]$release.runtimeVersion
        dotnetExeSha256 = Get-Sha256HexFromFile -Path $dotnet
        innoCompilerPath = $compiler
        innoVersion = [string]$release.innoVersion
        innoPublisher = $publisher
        innoInstallerPath = $innoInstaller
        innoInstallerSignatureStatus = $authenticode.Status.ToString()
        innoInstallerAttestationStatus = $attestationStatus
        innoInstallerSha256 = $installerHash
        innoCompilerSignatureStatus = $compilerSignature.Status.ToString()
        innoCompilerSha256 = Get-Sha256HexFromFile -Path $compiler
        innoLicenseSha256 = Get-Sha256HexFromFile -Path $pinnedInnoLicense
        innoAttestationLog = $(if ($attestationStatus -eq 'Verified') { 'inno-release-attestation.log' } else { $null })
    }
    Write-JsonFile -Path $toolchainPath -Value $toolchain

    $buildReport = [ordered]@{
        schemaVersion = 1
        status = 'Built'
        productName = $release.productName
        productVersion = $release.productVersion
        assemblyVersion = $release.assemblyVersion
        fileVersion = $release.fileVersion
        appId = $release.appId
        rid = $release.rid
        commit = $gitHead
        dirty = $dirty
        startedAtUtc = $startedAtUtc.ToString('o')
        finishedAtUtc = [DateTime]::UtcNow.ToString('o')
        operatingSystem = [System.Environment]::OSVersion.VersionString
        powershellVersion = $PSVersionTable.PSVersion.ToString()
        dotnetSdkVersion = [string]$release.dotnetSdkVersion
        runtimeVersion = [string]$release.runtimeVersion
        innoVersion = [string]$release.innoVersion
        driver = 'build/package.ps1 -> build/publish.ps1 -> dotnet publish -> ISCC.exe'
        dataRoot = $acceptanceDataRoot
        stagingDirectory = $stage
        packageDirectory = $candidateDirectory
        artifacts = @(
            [pscustomobject]@{ relativePath = [System.IO.Path]::GetFileName($zipPath); length = (Get-Item -LiteralPath $zipPath).Length; sha256 = Get-Sha256HexFromFile -Path $zipPath },
            [pscustomobject]@{ relativePath = [System.IO.Path]::GetFileName($setupPath); length = (Get-Item -LiteralPath $setupPath).Length; sha256 = Get-Sha256HexFromFile -Path $setupPath }
        )
        counts = [pscustomobject]@{ Passed = 1; Failed = 0; Blocked = 0; NotRun = 0; Skipped = 0 }
        exitCode = 0
    }
    Write-JsonFile -Path $buildReportPath -Value $buildReport

    $deliveryFiles = @()
    foreach ($file in Get-ChildItem -LiteralPath $candidateDirectory -File -Force) {
        if ($file.Name -eq 'SHA256SUMS.json') { continue }
        $deliveryFiles += [pscustomobject]@{
            relativePath = $file.Name
            length = [long]$file.Length
            sha256 = Get-Sha256HexFromFile -Path $file.FullName
        }
    }
    Write-JsonFile -Path $checksumsPath -Value ([ordered]@{ schemaVersion = 1; files = @($deliveryFiles | Sort-Object { $_.relativePath.ToLowerInvariant() }, relativePath) })

    $verifierLog = Join-Path $runEvidenceDirectory 'verify-package.log'
    $packageDirectoryRelative = $candidateDirectory.Substring($repoRoot.TrimEnd('\').Length + 1).Replace('\', '/')
    $verifyResult = Invoke-CapturedProcess -FilePath $powerShell -Arguments @(
        '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', 'build/verify-package.ps1',
        '-PackageDirectory', $packageDirectoryRelative, '-ReleaseManifest', 'packaging/release.json'
    ) -WorkingDirectory $repoRoot -LogPath $verifierLog
    if ($verifyResult.ExitCode -ne 0) { throw 'Static package verification failed; see verify-package.log.' }

    $artifactRecords = @()
    foreach ($file in Get-ChildItem -LiteralPath $candidateDirectory -File -Force | Sort-Object Name) {
        $artifactRecords += [pscustomobject]@{ relativePath = $file.Name; length = [long]$file.Length; sha256 = Get-Sha256HexFromFile -Path $file.FullName }
    }
    $runStatus = 'Passed'
    $runExitCode = 0
    Write-Output "PASS: GlassDesk $($release.productVersion) local release created at $candidateDirectory"
    Write-Output $verifyResult.Output.Trim()
}
catch {
    $runError = $_.Exception.Message
    $runStatus = 'Failed'
    $runExitCode = 1
    [Console]::Error.WriteLine(('FAIL: ' + $runError))
}
finally {
    if ($runEvidenceDirectory -and (Test-Path -LiteralPath $runEvidenceDirectory -PathType Container)) {
        $report = [ordered]@{
            schemaVersion = 1
            status = $runStatus
            commit = $null
            dirty = $null
            startedAtUtc = $startedAtUtc.ToString('o')
            finishedAtUtc = [DateTime]::UtcNow.ToString('o')
            operatingSystem = [System.Environment]::OSVersion.VersionString
            powershellVersion = $PSVersionTable.PSVersion.ToString()
            driver = 'build/package.ps1; static verifier does not launch installers'
            dataRoot = $acceptanceDataRoot
            stagingDirectory = $publishDirectory
            packageDirectory = $candidateDirectory
            artifacts = $artifactRecords
            counts = [pscustomobject]@{ Passed = $(if ($runStatus -eq 'Passed') { 1 } else { 0 }); Failed = $(if ($runStatus -eq 'Failed') { 1 } else { 0 }); Blocked = 0; NotRun = 0; Skipped = 0 }
            exitCode = $runExitCode
            error = $runError
        }
        try {
            $head = (& git -C $repoRoot rev-parse HEAD 2>$null | Out-String).Trim()
            if ($head -match '^[0-9a-fA-F]{40}$') { $report.commit = $head }
            $status = (& git -C $repoRoot status --porcelain=v1 2>$null | Out-String)
            $report.dirty = -not [string]::IsNullOrWhiteSpace($status)
        }
        catch { }
        try { Write-JsonFile -Path (Join-Path $runEvidenceDirectory 'package-run-report.json') -Value $report }
        catch { [Console]::Error.WriteLine(('Could not write package run report: ' + $_.Exception.Message)) }
    }
}

exit $runExitCode
