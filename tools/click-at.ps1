# Injects a real mouse click at a screen point using SendInput.
#
# Used for the cross-process click-through acceptance test. WM_NCHITTEST returning
# HTTRANSPARENT is documented to forward only to windows in the SAME thread, so proving
# pass-through requires a real click plus a target window owned by another process.
#
# Everything is built inside the C# helper rather than assembling the struct in PowerShell:
# assigning to a nested struct field of a boxed value type is unreliable, and a silently
# zeroed dwFlags produces events that report as sent but do nothing.
#
# ASCII only: Windows PowerShell 5.1 reads a .ps1 without a BOM using the system ANSI code
# page, so non-ASCII text in this file would fail to parse on a Chinese-locale machine.

param(
    [Parameter(Mandatory = $true)][int]$X,
    [Parameter(Mandatory = $true)][int]$Y,
    [switch]$Verify
)

$ErrorActionPreference = 'Stop'

if (-not ('GlassDesk.ClickInjector' -as [type])) {
    Add-Type -Namespace 'GlassDesk' -Name 'ClickInjector' -MemberDefinition @'
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct MOUSEINPUT {
    public int dx;
    public int dy;
    public uint mouseData;
    public uint dwFlags;
    public uint time;
    public System.IntPtr dwExtraInfo;
}

[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct INPUT {
    public uint type;
    public MOUSEINPUT mi;
}

[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct POINT { public int X; public int Y; }

[System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

[System.Runtime.InteropServices.DllImport("user32.dll")]
private static extern bool SetCursorPos(int x, int y);

[System.Runtime.InteropServices.DllImport("user32.dll")]
private static extern bool GetCursorPos(out POINT p);

[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool SetProcessDPIAware();

private const uint INPUT_MOUSE = 0;
private const uint MOUSEEVENTF_MOVE = 0x0001;
private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
private const uint MOUSEEVENTF_LEFTUP = 0x0004;

/// <summary>
/// Moves the cursor to a physical screen point and clicks the left button once.
/// Returns how many events the system accepted.
/// </summary>
/// <remarks>
/// Positioning uses SetCursorPos (which takes physical pixels directly) and the button
/// events deliberately carry NO move flag.
///
/// The obvious approach -- one MOUSEEVENTF_ABSOLUTE event per button -- was measured to
/// land in the wrong place on this mixed-DPI setup: asking for (700,500) left the cursor at
/// (358,581). Absolute coordinates are normalised against the virtual screen, and that
/// normalisation does not follow the per-monitor rule we rely on elsewhere. Since
/// SetCursorPos already speaks physical pixels, skipping the normalisation entirely is both
/// simpler and verifiable.
/// </remarks>
public static uint Click(int x, int y)
{
    SetCursorPos(x, y);
    System.Threading.Thread.Sleep(120);

    INPUT[] inputs = new INPUT[2];

    inputs[0].type = INPUT_MOUSE;
    inputs[0].mi.dwFlags = MOUSEEVENTF_LEFTDOWN;

    inputs[1].type = INPUT_MOUSE;
    inputs[1].mi.dwFlags = MOUSEEVENTF_LEFTUP;

    return SendInput(2, inputs, System.Runtime.InteropServices.Marshal.SizeOf(typeof(INPUT)));
}

/// <summary>Current cursor position in physical pixels, for sanity checks.</summary>
public static POINT Cursor()
{
    POINT p;
    GetCursorPos(out p);
    return p;
}
'@
}

[void][GlassDesk.ClickInjector]::SetProcessDPIAware()

$sent = [GlassDesk.ClickInjector]::Click($X, $Y)

if ($Verify) {
    $cursor = [GlassDesk.ClickInjector]::Cursor()
    $ok = if ($cursor.X -eq $X -and $cursor.Y -eq $Y) { 'cursor OK' } else { 'CURSOR MISMATCH' }
    Write-Output ("clicked ({0},{1})  sent={2}/2  cursor now ({3},{4})  {5}" -f $X, $Y, $sent, $cursor.X, $cursor.Y, $ok)
} else {
    Write-Output ("clicked ({0},{1})  sent={2}/2" -f $X, $Y, $sent)
}