# Generates the embedded offline Mandarin reading map from pinned Unicode 17.0.0 data.
param([string]$ZipPath = (Join-Path $env:TEMP 'GlassDesk-Unihan-17.0.0.zip'))
$ErrorActionPreference = 'Stop'
$expected = 'F7A48B2B545ACFAA77B2D607AE28747404CE02BAEFEE16396C5D2D7A8EF34B5E'
if (-not (Test-Path -LiteralPath $ZipPath)) { Invoke-WebRequest 'https://www.unicode.org/Public/17.0.0/ucd/Unihan.zip' -OutFile $ZipPath }
$actual = (Get-FileHash -LiteralPath $ZipPath -Algorithm SHA256).Hash
if ($actual -ne $expected) { throw "Unihan.zip hash mismatch: $actual" }
Add-Type -AssemblyName System.IO.Compression
$archive = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
try {
    $entry = $archive.GetEntry('Unihan_Readings.txt')
    $reader = [System.IO.StreamReader]::new($entry.Open(), [System.Text.Encoding]::UTF8)
    $lines = [System.Collections.Generic.List[string]]::new()
    try {
        while ($null -ne ($line = $reader.ReadLine())) {
            if ($line -notmatch '^(U\+[0-9A-F]+)\tkMandarin\t(\S+)') { continue }
            $reading = $Matches[2].Split(' ')[0].Normalize([Text.NormalizationForm]::FormD)
            $plain = -join ($reading.ToCharArray() | Where-Object { [Globalization.CharUnicodeInfo]::GetUnicodeCategory($_) -ne [Globalization.UnicodeCategory]::NonSpacingMark })
            $lines.Add("$($Matches[1])`t$($plain.ToLowerInvariant())")
        }
    } finally { $reader.Dispose() }
    $target = Join-Path $PSScriptRoot '..\src\GlassDesk.AppServices\Resources\unihan-mandarin-17.txt'
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($target))) | Out-Null
    [IO.File]::WriteAllLines([IO.Path]::GetFullPath($target), $lines, [Text.UTF8Encoding]::new($false))
    Write-Host "Generated $($lines.Count) readings: $target"
    Get-FileHash $target -Algorithm SHA256
} finally { $archive.Dispose() }
