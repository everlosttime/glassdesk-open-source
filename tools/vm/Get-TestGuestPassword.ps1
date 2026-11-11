# Resolves the disposable test VM credential without storing it in the repo.
# The DPAPI file is protected to the Windows account that created it.
[CmdletBinding()]
param(
    [string]$StagingDirectory = 'D:\GlassDesk-VM',
    [string]$UserPassword = $env:GLASSDESK_VM_PASSWORD
)

$ErrorActionPreference = 'Stop'
if (-not [string]::IsNullOrWhiteSpace($UserPassword)) {
    return $UserPassword
}

$protectedPath = Join-Path $StagingDirectory 'guest-password.dpapi'
if (-not (Test-Path -LiteralPath $protectedPath)) {
    throw "Test VM credential missing. Set GLASSDESK_VM_PASSWORD or provide $protectedPath for this Windows user."
}

$protected = Get-Content -LiteralPath $protectedPath -Raw | ConvertTo-SecureString
$passwordPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($protected)
try {
    return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($passwordPointer)
}
finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($passwordPointer)
}
