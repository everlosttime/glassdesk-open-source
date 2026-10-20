# Verifies a GlassDesk release package without extracting or launching any payload.
# Keep this script ASCII so it remains readable by Windows PowerShell 5.1 on any locale.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackageDirectory,
    [string]$ReleaseManifest
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($ReleaseManifest)) {
    $ReleaseManifest = Join-Path (Split-Path -Parent $PSScriptRoot) 'packaging/release.json'
}

function Read-JsonFile {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Missing JSON file: $Path" }
    return Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
}

function Get-Sha256HexFromStream {
    param([System.IO.Stream]$Stream)
    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    try {
        $digest = $algorithm.ComputeHash($Stream)
        return ([System.BitConverter]::ToString($digest)).Replace('-', '').ToLowerInvariant()
    }
    finally { $algorithm.Dispose() }
}

function Get-Sha256HexFromFile {
    param([string]$Path)
    $stream = [System.IO.File]::OpenRead($Path)
    try { return Get-Sha256HexFromStream -Stream $stream }
    finally { $stream.Dispose() }
}

function Assert-RelativePackagePath {
    param([string]$Path, [string]$Context)
    if ([string]::IsNullOrWhiteSpace($Path)) { throw "Unsafe path in ${Context}: empty path." }
    if ($Path.Contains('\') -or $Path.Contains(':') -or $Path.StartsWith('/') -or [System.IO.Path]::IsPathRooted($Path)) {
        throw "Unsafe path in ${Context}: '$Path'."
    }
    foreach ($segment in $Path.Split('/')) {
        if ([string]::IsNullOrWhiteSpace($segment) -or $segment -eq '.' -or $segment -eq '..' -or
            $segment.EndsWith('.') -or $segment.EndsWith(' ') -or $segment -match '[<>"|?*\x00-\x1f]') {
            throw "Unsafe path in ${Context}: '$Path'."
        }
        $deviceName = ($segment -split '\.')[0]
        if ($deviceName -match '^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$') {
            throw "Unsafe path in ${Context}: reserved device name '$Path'."
        }
    }
}

function Assert-NoReparsePoints {
    param([string]$Root)
    $rootItem = Get-Item -LiteralPath $Root -Force
    if (($rootItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Package directory is a reparse point: '$Root'."
    }
    $pending = New-Object 'System.Collections.Generic.Stack[string]'
    $pending.Push($Root)
    while ($pending.Count -gt 0) {
        $current = $pending.Pop()
        foreach ($child in Get-ChildItem -LiteralPath $current -Force) {
            if (($child.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Package contains a reparse point: '$($child.FullName)'."
            }
            if ($child.PSIsContainer) { $pending.Push($child.FullName) }
        }
    }
}

function Assert-NotForbiddenPayloadPath {
    param([string]$Path)
    $segments = $Path.Split('/')
    foreach ($segment in $segments) {
        if ($segment -match '^(?i)(\.git|obj|tests?|test-results|sdk|ref|referenceassemblies|src|packages|node_modules)$') {
            throw "Forbidden payload path: '$Path'."
        }
    }
    if ($Path -match '(?i)(\.pdb$|\.cs$|\.csproj$|\.slnx?$|\.props$|\.targets$|\.ps1$|\.psm1$|\.psd1$|\.nupkg$|project\.assets\.json$|\.Tests?\.dll$|\.TestAdapter\.dll$|^testhost|^Microsoft\.TestPlatform|^Microsoft\.CodeCoverage|^Microsoft\.CodeAnalysis|^Microsoft\.Build|^NuGet\.|^dotnet\.exe$|^csc\.dll$|^vbc\.dll$|^MSBuild\.dll$)') {
        throw "Forbidden payload file: '$Path'."
    }
}

function Assert-ManifestEntries {
    param([object]$Manifest, [string]$Context)
    if ($null -eq $Manifest.files) { throw "Missing files list in $Context manifest." }
    $map = @{}
    foreach ($entry in $Manifest.files) {
        $relativePath = [string]$entry.relativePath
        Assert-RelativePackagePath -Path $relativePath -Context $Context
        $key = $relativePath.ToLowerInvariant()
        if ($map.ContainsKey($key)) { throw "Duplicate path in $Context manifest: '$relativePath'." }
        if ($null -eq $entry.length -or [long]$entry.length -lt 0) { throw "Invalid length in $Context manifest for '$relativePath'." }
        $sha = ([string]$entry.sha256).ToLowerInvariant()
        if ($sha -notmatch '^[0-9a-f]{64}$') { throw "Invalid SHA-256 in $Context manifest for '$relativePath'." }
        $map[$key] = [pscustomobject]@{ RelativePath = $relativePath; Length = [long]$entry.length; Sha256 = $sha }
    }
    return $map
}

function Assert-PayloadArchive {
    param([string]$ZipPath, [object]$PayloadManifest)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $expected = Assert-ManifestEntries -Manifest $PayloadManifest -Context 'payload'
    $archive = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        $actual = @{}
        foreach ($entry in $archive.Entries) {
            # .NET Framework on Windows reports ZIP separators as backslashes even when the archive stores forward slashes.
            $relativePath = ([string]$entry.FullName).Replace('\', '/')
            if ($relativePath.EndsWith('/')) { throw "Unexpected directory entry in payload ZIP: '$relativePath'." }
            Assert-RelativePackagePath -Path $relativePath -Context 'ZIP'
            $key = $relativePath.ToLowerInvariant()
            if ($actual.ContainsKey($key)) { throw "Duplicate ZIP entry path: '$relativePath'." }
            Assert-NotForbiddenPayloadPath -Path $relativePath
            $entryStream = $entry.Open()
            try { $sha = Get-Sha256HexFromStream -Stream $entryStream }
            finally { $entryStream.Dispose() }
            $actual[$key] = [pscustomobject]@{ RelativePath = $relativePath; Length = [long]$entry.Length; Sha256 = $sha }
        }
        foreach ($key in $expected.Keys) {
            if (-not $actual.ContainsKey($key)) { throw "Payload archive is missing manifest file '$($expected[$key].RelativePath)'." }
            $wanted = $expected[$key]
            $found = $actual[$key]
            if ($found.Length -ne $wanted.Length) { throw "Payload length mismatch for '$($wanted.RelativePath)'." }
            if ($found.Sha256 -ne $wanted.Sha256) { throw "Payload SHA-256 mismatch for '$($wanted.RelativePath)'." }
        }
        foreach ($key in $actual.Keys) {
            if (-not $expected.ContainsKey($key)) { throw "Payload archive has an unlisted file '$($actual[$key].RelativePath)'." }
        }
    }
    finally { $archive.Dispose() }
}

function Assert-ReleaseIdentity {
    param([object]$Release, [object]$PayloadManifest, [object]$Toolchain, [object]$BuildReport)
    if ($Release.schemaVersion -ne 1 -or $Release.productName -ne 'GlassDesk' -or $Release.status -notin @('local-candidate', 'local-release')) {
        throw 'Unsupported or non-release manifest.'
    }
    foreach ($record in @($PayloadManifest, $BuildReport)) {
        if ($record.productName -ne $Release.productName -or $record.productVersion -ne $Release.productVersion -or
            $record.assemblyVersion -ne $Release.assemblyVersion -or $record.fileVersion -ne $Release.fileVersion -or
            $record.appId -ne $Release.appId -or $record.rid -ne $Release.rid) {
            throw 'Product version, file version, AppId, or RID does not match the release manifest.'
        }
    }
    if ($Toolchain.schemaVersion -ne 1 -or $Toolchain.sdkVersion -ne $Release.dotnetSdkVersion -or
        $Toolchain.runtimeVersion -ne $Release.runtimeVersion -or $Toolchain.innoVersion -ne $Release.innoVersion) {
        throw 'Toolchain versions do not match the release manifest.'
    }
    if ($Toolchain.innoPublisher -ne 'Pyrsys B.V.' -or $Toolchain.innoInstallerSignatureStatus -ne 'Valid' -or
        $Toolchain.innoInstallerAttestationStatus -ne 'Verified' -or $Toolchain.innoCompilerSignatureStatus -ne 'Valid' -or
        ([string]$Toolchain.innoInstallerSha256).ToLowerInvariant() -ne ([string]$Release.innoSetupInstallerSha256).ToLowerInvariant() -or
        ([string]$Toolchain.innoCompilerSha256 -notmatch '^[0-9A-Fa-f]{64}$')) {
        throw 'Inno compiler source, publisher signature, or SHA-256 evidence is invalid.'
    }
    if ($BuildReport.status -ne 'Built') { throw 'Build report does not record a completed candidate build.' }
}

function Assert-RequiredPayloadLicenses {
    param([object]$PayloadManifest)
    $names = @{}
    foreach ($entry in $PayloadManifest.files) { $names[[string]$entry.relativePath.ToLowerInvariant()] = $true }
    foreach ($required in @('license.txt', 'thirdpartynotices.txt', 'windows-sdk-license.txt', 'inno-setup-license.txt', 'unicode-license.txt', 'third-party-notices.md')) {
        if (-not $names.ContainsKey($required)) { throw "Missing required payload license or notice: '$required'." }
    }
    if (-not $names.ContainsKey('release-notes.md')) { throw "Missing required release-notes.md from payload." }
}

function Assert-DeliveryChecksums {
    param([string]$Root, [object]$ChecksumManifest, [string[]]$ExpectedFiles)
    if ($ChecksumManifest.schemaVersion -ne 1) { throw 'Unsupported checksum manifest schema.' }
    $records = Assert-ManifestEntries -Manifest $ChecksumManifest -Context 'checksum'
    $expected = @{}
    foreach ($name in $ExpectedFiles) { $expected[$name.ToLowerInvariant()] = $true }
    foreach ($key in $expected.Keys) {
        if (-not $records.ContainsKey($key)) { throw "Checksum manifest is missing '$key'." }
    }
    foreach ($key in $records.Keys) {
        if (-not $expected.ContainsKey($key)) { throw "Checksum manifest has an extra entry '$($records[$key].RelativePath)'." }
        $record = $records[$key]
        $path = Join-Path $Root $record.RelativePath
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Checksum file is missing: '$($record.RelativePath)'." }
        $item = Get-Item -LiteralPath $path -Force
        if ([long]$item.Length -ne $record.Length) { throw "Checksum length mismatch for '$($record.RelativePath)'." }
        if ((Get-Sha256HexFromFile -Path $path) -ne $record.Sha256) { throw "Checksum SHA-256 mismatch for '$($record.RelativePath)'." }
    }
}

function Assert-SetupVersionMetadata {
    param([string]$Path, [object]$Release)
    $versionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
    $expectedFileVersion = [string]$Release.fileVersion
    $expectedProductVersion = [string]$Release.productVersion
    $fileVersionParts = '{0}.{1}.{2}.{3}' -f $versionInfo.FileMajorPart, $versionInfo.FileMinorPart, $versionInfo.FileBuildPart, $versionInfo.FilePrivatePart
    $productVersionParts = '{0}.{1}.{2}.{3}' -f $versionInfo.ProductMajorPart, $versionInfo.ProductMinorPart, $versionInfo.ProductBuildPart, $versionInfo.ProductPrivatePart

    if ($fileVersionParts -cne $expectedFileVersion -or ([string]$versionInfo.FileVersion).Trim() -cne $expectedFileVersion) {
        throw "Setup file version does not match release.fileVersion '$expectedFileVersion'."
    }
    if ($productVersionParts -cne $expectedFileVersion) {
        throw "Setup product version resource does not match release.fileVersion '$expectedFileVersion'."
    }
    if (([string]$versionInfo.ProductVersion).Trim() -cne $expectedProductVersion) {
        throw "Setup product text version does not match release.productVersion '$expectedProductVersion'."
    }
}

try {
    if (-not (Test-Path -LiteralPath $PackageDirectory -PathType Container)) { throw "Package directory does not exist: '$PackageDirectory'." }
    if (-not (Test-Path -LiteralPath $ReleaseManifest -PathType Leaf)) { throw "Release manifest does not exist: '$ReleaseManifest'." }
    $root = [System.IO.Path]::GetFullPath($PackageDirectory).TrimEnd('\')
    Assert-NoReparsePoints -Root $root

    $release = Read-JsonFile -Path $ReleaseManifest
    $baseName = [string]$release.packageBaseName
    if ($baseName -notmatch '^GlassDesk-[0-9A-Za-z.-]+-win-x64$') { throw 'Invalid package base name in release manifest.' }
    $zipName = $baseName + '.zip'
    $setupName = $baseName + '-Setup.exe'
    $expectedRootFiles = @($zipName, $setupName, 'payload-manifest.json', 'toolchain.json', 'build-report.json', 'INNO-SETUP-LICENSE.txt', 'SHA256SUMS.json')
    $actualRootFiles = @(Get-ChildItem -LiteralPath $root -Force)
    foreach ($item in $actualRootFiles) {
        if ($item.PSIsContainer) { throw "Unexpected extra directory in candidate: '$($item.Name)'." }
    }
    $actualNames = @{}
    foreach ($item in $actualRootFiles) { $actualNames[$item.Name.ToLowerInvariant()] = $true }
    foreach ($name in $expectedRootFiles) {
        if (-not $actualNames.ContainsKey($name.ToLowerInvariant())) { throw "Candidate file is missing: '$name'." }
    }
    foreach ($name in $actualNames.Keys) {
        if ($name -notin @($expectedRootFiles | ForEach-Object { $_.ToLowerInvariant() })) { throw "Unexpected extra candidate file: '$name'." }
    }

    $payloadManifestPath = Join-Path $root 'payload-manifest.json'
    $toolchainPath = Join-Path $root 'toolchain.json'
    $buildReportPath = Join-Path $root 'build-report.json'
    $payloadManifest = Read-JsonFile -Path $payloadManifestPath
    $toolchain = Read-JsonFile -Path $toolchainPath
    $buildReport = Read-JsonFile -Path $buildReportPath
    $checksumManifest = Read-JsonFile -Path (Join-Path $root 'SHA256SUMS.json')
    Assert-ReleaseIdentity -Release $release -PayloadManifest $payloadManifest -Toolchain $toolchain -BuildReport $buildReport
    Assert-RequiredPayloadLicenses -PayloadManifest $payloadManifest
    $installerLicense = Get-Item -LiteralPath (Join-Path $root 'INNO-SETUP-LICENSE.txt') -Force
    if ($installerLicense.Length -le 0) { throw 'Inno Setup license file is empty.' }
    Assert-PayloadArchive -ZipPath (Join-Path $root $zipName) -PayloadManifest $payloadManifest
    $checksumFiles = @($expectedRootFiles | Where-Object { $_ -ne 'SHA256SUMS.json' })
    Assert-DeliveryChecksums -Root $root -ChecksumManifest $checksumManifest -ExpectedFiles $checksumFiles
    Assert-SetupVersionMetadata -Path (Join-Path $root $setupName) -Release $release
    Write-Output 'PASS: package files, payload, licenses, application and Setup versions, and SHA-256 manifests are consistent.'
    exit 0
}
catch {
    [Console]::Error.WriteLine(('FAIL: ' + $_.Exception.Message))
    exit 1
}
