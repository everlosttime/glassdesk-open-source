# Receives evidence files uploaded from the test guest.
#
# WHY THIS EXISTS
#   The guest's Guest Additions file channel (guestcontrol stat/copyfrom/copyto) keeps stalling
#   with "Error starting guest session (current status is: starting)" and a guest reboot does
#   not always clear it. The screenshot and keyboard channels (controlvm) keep working, so the
#   run itself is fine — only the collection path was unreliable. VirtualBox's NAT maps
#   10.0.2.2 to the host's loopback, so the guest can reach a listener here without any port
#   forwarding, and the evidence comes back over the network instead.
#
# USAGE
#   Start this BEFORE launching the run in the guest. It writes everything to
#   <OutDirectory> and exits when it has seen a file named acceptancedone.txt (or on timeout).
#
# ASCII only on purpose: Windows PowerShell 5.1 reads a .ps1 without a BOM using the system
# ANSI code page, so non-ASCII text in this file would fail to parse on a Chinese-locale
# machine.

[CmdletBinding()]
param(
    [int]$Port = 8765,
    [string]$OutDirectory = 'D:\GlassDesk-VM\evidence\uploads',
    # Files in here are served on GET, so a script can be handed to the guest without the
    # guestcontrol file channel (which stalls): the Run dialog fetches it with one short command.
    [string]$PayloadDirectory = 'D:\GlassDesk-VM\payload',
    [int]$TimeoutMinutes = 120
)

$ErrorActionPreference = 'Stop'

New-Item -ItemType Directory -Force -Path $OutDirectory | Out-Null
New-Item -ItemType Directory -Force -Path $PayloadDirectory | Out-Null

$listener = New-Object System.Net.HttpListener
$listener.Prefixes.Add("http://127.0.0.1:$Port/")

try {
    $listener.Start()
}
catch {
    Write-Host "Could not listen on 127.0.0.1:$Port - $($_.Exception.Message)" -ForegroundColor Yellow
    exit 2
}

Write-Host "listening on http://127.0.0.1:$Port/ (the guest reaches it at http://10.0.2.2:$Port/)"
Write-Host "writing to $OutDirectory"
Write-Host ''

$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
$received = @()
$done = $false

while (-not $done -and (Get-Date) -lt $deadline) {
    $contextTask = $listener.GetContextAsync()

    while (-not $contextTask.Wait(2000)) {
        if ((Get-Date) -ge $deadline) { break }
    }

    if (-not $contextTask.IsCompleted) { continue }

    $context = $contextTask.Result
    $name = [System.IO.Path]::GetFileName($context.Request.Url.AbsolutePath)

    if ([string]::IsNullOrWhiteSpace($name)) { $name = 'unnamed' }

    # ---- GET: hand a file to the guest ----
    if ($context.Request.HttpMethod -eq 'GET') {
        $source = Join-Path $PayloadDirectory $name

        if (Test-Path $source) {
            $bytes = [System.IO.File]::ReadAllBytes($source)
            $context.Response.StatusCode = 200
            $context.Response.ContentType = 'application/octet-stream'
            $context.Response.ContentLength64 = $bytes.Length
            $context.Response.OutputStream.Write($bytes, 0, $bytes.Length)
            Write-Host ("  served {0} ({1:N0} bytes)" -f $name, $bytes.Length)
        }
        else {
            $context.Response.StatusCode = 404
            Write-Host ("  asked for {0}, not in the payload folder" -f $name) -ForegroundColor Yellow
        }

        $context.Response.OutputStream.Close()
        continue
    }

    # ---- POST: receive evidence from the guest ----
    $target = Join-Path $OutDirectory ("{0:HHmmss}-{1}" -f (Get-Date), $name)

    $stream = [System.IO.File]::Create($target)
    try {
        $context.Request.InputStream.CopyTo($stream)
    }
    finally {
        $stream.Dispose()
    }

    $bytes = (Get-Item $target).Length
    Write-Host ("  received {0} ({1:N0} bytes)" -f $name, $bytes)
    $received += $target

    $response = [System.Text.Encoding]::UTF8.GetBytes('ok')
    $context.Response.StatusCode = 200
    $context.Response.ContentLength64 = $response.Length
    $context.Response.OutputStream.Write($response, 0, $response.Length)
    $context.Response.OutputStream.Close()

    if ($name -eq 'acceptancedone.txt') { $done = $true }
}

$listener.Stop()
$listener.Close()

Write-Host ''
if ($received.Count -eq 0) {
    Write-Host 'Nothing arrived before the deadline.' -ForegroundColor Yellow
    exit 2
}

Write-Host "Received $($received.Count) file(s):"
$received | ForEach-Object { Write-Host "  $_" }
exit 0