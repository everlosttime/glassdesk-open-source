# Run under PowerShell 7 on Windows as a normal user, in an STA.
# Pass the built companion with -CompanionAssembly to test under Windows PowerShell 5.1.
# Operates only on uniquely named fixtures in the current user's temporary directory.
[CmdletBinding()]
param([string]$CompanionAssembly)

$ErrorActionPreference = 'Stop'
if ([Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA') {
    throw 'Launch PowerShell with -STA.'
}

if ($CompanionAssembly) {
    $assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $CompanionAssembly).Path)
    $serviceType = $assembly.GetType('NaiwaDesktop.RecycleService', $true)
}
else {
    $service = Join-Path $PSScriptRoot '../contrib/Naiwa/RecycleService.cs'
    if (-not (Test-Path -LiteralPath $service -PathType Leaf)) {
        throw 'The Naiwa recycle service is missing.'
    }
    $serviceType = Add-Type -Path $service -PassThru | Where-Object FullName -eq 'NaiwaDesktop.RecycleService'
}

$tag = 'GlassDesk-Naiwa-Recycle-' + [Guid]::NewGuid().ToString('N')
$fixture = Join-Path ([IO.Path]::GetTempPath()) $tag
$goodFile = Join-Path $fixture "$tag-file.txt"
$goodFolder = Join-Path $fixture "$tag-folder"
$nestedFile = Join-Path $goodFolder 'child.txt'
$lockedFile = Join-Path $fixture "$tag-locked.txt"
$missingFile = Join-Path $fixture "$tag-missing.txt"
$uncFile = '\\localhost\NonexistentGlassDeskTestShare\' + $tag
$shell = New-Object -ComObject Shell.Application
$bin = $shell.Namespace(10)
if ($null -eq $bin) { throw 'The Windows Recycle Bin is unavailable.' }

function Find-RecycledFixture([string]$Name) {
    foreach ($item in $bin.Items()) {
        if (($item.Name -eq $Name -or $item.Name -eq [IO.Path]::GetFileNameWithoutExtension($Name)) -and
            $item.ExtendedProperty('System.Recycle.DeletedFrom') -eq $fixture) {
            return $item
        }
    }
}

function Wait-ForRecycledFixture([string]$Path) {
    $name = [IO.Path]::GetFileName($Path)
    for ($attempt = 0; $attempt -lt 50; $attempt++) {
        $item = Find-RecycledFixture $name
        if ($null -ne $item) { return $item }
        Start-Sleep -Milliseconds 100
    }
    throw "Fixture not found in the Windows Recycle Bin: $name"
}

$lockedHandle = $null
$restored = $false
try {
    [void][IO.Directory]::CreateDirectory($goodFolder)
    [IO.File]::WriteAllText($goodFile, $tag)
    [IO.File]::WriteAllText($nestedFile, $tag)
    [IO.File]::WriteAllText($lockedFile, $tag)
    $lockedHandle = [IO.File]::Open($lockedFile, 'Open', 'Read', 'None')

    $recycle = $serviceType.GetMethod('Recycle', [Reflection.BindingFlags]'NonPublic,Static')
    $paths = [string[]]@($uncFile, $missingFile, $lockedFile, $goodFile, $goodFile, $goodFolder)
    $count = $recycle.Invoke($null, (, $paths))
    if ($count -ne 2) { throw "Expected two confirmed recycled items, received $count." }
    if ([IO.File]::Exists($goodFile) -or [IO.Directory]::Exists($goodFolder)) { throw 'Recycled fixtures remained at their original locations.' }
    if (-not [IO.File]::Exists($lockedFile)) { throw 'The locked fixture was removed.' }
    foreach ($path in @($goodFile, $goodFolder)) {
        $item = Wait-ForRecycledFixture $path
        $item.InvokeVerb('undelete')
    }
    for ($attempt = 0; $attempt -lt 50 -and (-not [IO.File]::Exists($goodFile) -or -not [IO.File]::Exists($nestedFile)); $attempt++) {
        Start-Sleep -Milliseconds 100
    }
    if ([IO.File]::ReadAllText($goodFile) -ne $tag -or [IO.File]::ReadAllText($nestedFile) -ne $tag) {
        throw 'Fixture restoration did not preserve original file contents.'
    }
    $restored = $true
    Write-Output 'PASS Windows Recycle Bin, restore, mixed batch, duplicates, locked file, missing file and UNC rejection.'
}
finally {
    if ($null -ne $lockedHandle) { $lockedHandle.Dispose() }
    if (-not $restored) {
        foreach ($path in @($goodFile, $goodFolder)) {
            $item = Find-RecycledFixture ([IO.Path]::GetFileName($path))
            if ($null -ne $item) { $item.InvokeVerb('undelete') }
        }
    }
    foreach ($path in @($nestedFile, $goodFile, $lockedFile)) {
        if ([IO.File]::Exists($path)) { [IO.File]::Delete($path) }
    }
    if ([IO.Directory]::Exists($goodFolder)) { [IO.Directory]::Delete($goodFolder) }
    if ([IO.Directory]::Exists($fixture)) { [IO.Directory]::Delete($fixture) }
}
