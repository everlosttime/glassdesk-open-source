# Independent upstream GPL component. No installer is run; source is distributed alongside it.
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$OutputDirectory,
      [string]$CacheDirectory = (Join-Path $PSScriptRoot '../artifacts/headsetcontrol-cache'))
$ErrorActionPreference = 'Stop'
$assets = @(
    @{ Name='headsetcontrol-windows-x86_64.exe'; Hash='81ACAE4C083AAA40A2E9C1BCE7B3DD9625E74B9D9EE96384BB43B2DFF7FF0FA5'; Url='https://github.com/Sapd/HeadsetControl/releases/download/4.2.0/headsetcontrol-windows-x86_64.exe' },
    @{ Name='headsetcontrol-4.2.0.tar.gz'; Hash='BAB9046B33D487730D5D46537811BE4036F107E4C44BA3A47293426A038E7DA7'; Url='https://github.com/Sapd/HeadsetControl/releases/download/4.2.0/headsetcontrol-4.2.0.tar.gz' },
    @{ Name='LICENSE.txt'; Hash='F01E72BE1EC09516D50F7B728338EE92CFB3CC36D6EFA477CB2FBCBD040D70C5'; Url='https://raw.githubusercontent.com/Sapd/HeadsetControl/4.2.0/license' },
    @{ Name='hidapi-0.15.0.tar.gz'; Hash='5D84DEC684C27B97B921D2F3B73218CB773CF4EA915CAEE317AC8FC73CEF8136'; Url='https://codeload.github.com/libusb/hidapi/tar.gz/refs/tags/hidapi-0.15.0' },
    @{ Name='HIDAPI-LICENSE.txt'; Hash='30EB1BEF29B46F8BA7AB8B416035DBD93CB034A45481DD97815B944284582CD2'; Url='https://raw.githubusercontent.com/libusb/hidapi/hidapi-0.15.0/LICENSE-bsd.txt' }
)
New-Item -ItemType Directory -Force -Path $CacheDirectory | Out-Null
$target = Join-Path $OutputDirectory 'components/headsetcontrol'
New-Item -ItemType Directory -Force -Path $target | Out-Null
foreach ($asset in $assets) {
    $cached = Join-Path $CacheDirectory $asset.Name
    if (-not (Test-Path -LiteralPath $cached -PathType Leaf)) {
        Invoke-WebRequest -Uri $asset.Url -OutFile $cached -UseBasicParsing
    }
    if ((Get-FileHash -LiteralPath $cached -Algorithm SHA256).Hash -cne $asset.Hash) {
        throw "HeadsetControl pinned asset checksum mismatch: $($asset.Name)"
    }
    Copy-Item -LiteralPath $cached -Destination (Join-Path $target $asset.Name) -Force
}
$notice = @'
HeadsetControl 4.2.0 by HeadsetControl contributors
https://github.com/Sapd/HeadsetControl/releases/tag/4.2.0
An independent executable distributed under GPL-3.0; see LICENSE.txt.
Corresponding upstream source: headsetcontrol-4.2.0.tar.gz in this directory.
The upstream binary reports HIDAPI 0.15.0; its source and BSD license are also included.
GlassDesk invokes its read-only JSON interface as a separate process.
'@
[System.IO.File]::WriteAllText((Join-Path $target 'NOTICE.txt'), $notice, (New-Object System.Text.UTF8Encoding($false)))
Write-Output 'PASS: pinned HeadsetControl executable, license and corresponding source copied.'
