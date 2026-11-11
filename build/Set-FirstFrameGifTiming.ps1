[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RecordingDirectory)
$ErrorActionPreference='Stop'
$directory=[IO.Path]::GetFullPath($RecordingDirectory)
$metadata=Get-Content -LiteralPath (Join-Path $directory 'first-frame-recording.json') -Raw | ConvertFrom-Json
$ticks=@($metadata.desktopSampleTicks)
if($ticks.Count -ne $metadata.desktopFrames -or $ticks.Count -eq 0){throw 'Missing original desktop sample timestamps.'}
$path=Join-Path $directory 'desktop-first-frames.gif'
$bytes=[IO.File]::ReadAllBytes($path)
if([Text.Encoding]::ASCII.GetString($bytes,0,6) -notin @('GIF87a','GIF89a')){throw 'Not a GIF stream.'}
$offset=13
if($bytes[10] -band 128){$offset+=3*(1 -shl (($bytes[10] -band 7)+1))}
$frame=0;$pendingControl=-1
while($offset -lt $bytes.Length){
 switch($bytes[$offset]){
  0x3B {$offset=$bytes.Length;break}
  0x21 {
   if($bytes[$offset+1] -eq 0xF9){
    if($bytes[$offset+2] -ne 4 -or $bytes[$offset+7] -ne 0){throw 'Invalid graphic control extension.'}
    $pendingControl=$offset;$offset+=8
   } else {
    $offset+=2
    do {$length=[int]$bytes[$offset];$offset+=1+$length} while($length -ne 0)
   }
   break
  }
  0x2C {
   if($frame -ge $ticks.Count -or $pendingControl -lt 0){throw 'GIF frames do not match timestamped control blocks.'}
   $milliseconds=if($frame+1 -lt $ticks.Count){1000*([double]$ticks[$frame+1]-[double]$ticks[$frame])/[double]$metadata.qpcFrequency}else{33}
   $delay=[int][Math]::Clamp([Math]::Round($milliseconds/10),1,65535)
   $bytes[$pendingControl+4]=[byte]($delay -band 255)
   $bytes[$pendingControl+5]=[byte](($delay -shr 8) -band 255)
   $bytes[$pendingControl+3]=[byte](($bytes[$pendingControl+3] -band 227) -bor 8)
   $packed=$bytes[$offset+9];$offset+=10
   if($packed -band 128){$offset+=3*(1 -shl (($packed -band 7)+1))}
   $offset++ # LZW minimum code size; skip compressed sub-blocks without interpreting pixels.
   do {$length=[int]$bytes[$offset];$offset+=1+$length} while($length -ne 0)
   $frame++;$pendingControl=-1
   break
  }
  default {throw "Unknown GIF block at $offset."}
 }
}
if($frame -ne $ticks.Count){throw 'Incomplete GIF frame sequence.'}
# WPF's encoder drops frame delay metadata. Preserve every pixel and palette;
# restore playback timing only from the captured QPC timestamps, never invented FPS.
[IO.File]::WriteAllBytes($path,$bytes)
[ordered]@{frames=$frame;source='Original desktopSampleTicks/QPC, rounded to GIF centiseconds';pixelsReencoded=$false} |
 ConvertTo-Json | Set-Content -LiteralPath (Join-Path $directory 'gif-timing-verification.json') -Encoding utf8
