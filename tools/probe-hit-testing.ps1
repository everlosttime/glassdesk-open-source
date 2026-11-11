# Probes whether the transparent parts of a GlassDesk window intercept the mouse.
#
# Layered windows are documented to hit-test by shape and transparency, so alpha-zero
# areas should let mouse input through. That claim decides whether the rounded corners of
# the Dock need an extra WM_NCHITTEST handler, so it is measured rather than assumed.
#
# ASCII only: Windows PowerShell 5.1 reads a .ps1 without a BOM using the system ANSI code
# page, so non-ASCII text in this file would fail to parse on a Chinese-locale machine.

param(
    [string]$WindowTitleMatch = 'GlassDesk'
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

if (-not ('GlassDesk.HitProbe' -as [type])) {
    Add-Type -Namespace 'GlassDesk' -Name 'HitProbe' -MemberDefinition @'
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct POINT { public int X; public int Y; }

[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool GetWindowRect(System.IntPtr hWnd, out RECT rect);

[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern System.IntPtr WindowFromPoint(POINT point);

[System.Runtime.InteropServices.DllImport("user32.dll", CharSet=System.Runtime.InteropServices.CharSet.Unicode)]
public static extern int GetWindowText(System.IntPtr hWnd, System.Text.StringBuilder text, int maxCount);

[System.Runtime.InteropServices.DllImport("user32.dll", CharSet=System.Runtime.InteropServices.CharSet.Unicode)]
public static extern int GetClassName(System.IntPtr hWnd, System.Text.StringBuilder text, int maxCount);

[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool SetProcessDPIAware();

public delegate bool EnumProc(System.IntPtr hWnd, System.IntPtr lParam);

[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool EnumWindows(EnumProc callback, System.IntPtr lParam);

[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern uint GetWindowThreadProcessId(System.IntPtr hWnd, out uint processId);

[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool IsWindowVisible(System.IntPtr hWnd);

[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern System.IntPtr SendMessage(System.IntPtr hWnd, int msg, System.IntPtr wParam, System.IntPtr lParam);
'@
}

[void][GlassDesk.HitProbe]::SetProcessDPIAware()

# Enumerate every top-level window and match by title.
#
# Not using Process.MainWindowTitle: the product's windows set ShowInTaskbar=false, and a
# process whose windows are all non-taskbar can report an empty main window title.
$script:found = @()

$callback = [GlassDesk.HitProbe+EnumProc] {
    param($hWnd, $lParam)

    if ([GlassDesk.HitProbe]::IsWindowVisible($hWnd)) {
        $text = New-Object System.Text.StringBuilder 512
        [void][GlassDesk.HitProbe]::GetWindowText($hWnd, $text, $text.Capacity)
        $title = $text.ToString()

        if ($title -like "*$WindowTitleMatch*") {
            $script:found += [pscustomobject]@{ Handle = $hWnd; Title = $title }
        }
    }

    return $true
}

[void][GlassDesk.HitProbe]::EnumWindows($callback, [System.IntPtr]::Zero)

# Prefer the launchpad when several windows match.
$target = $script:found | Where-Object { $_.Title -like '*启动台*' } | Select-Object -First 1
if (-not $target) { $target = $script:found | Select-Object -First 1 }

if (-not $target) {
    Write-Output "NOT FOUND: no visible window whose title matches '$WindowTitleMatch'."
    Write-Output ("visible matching titles seen: {0}" -f ($script:found.Count))
    exit 2
}

$hwnd = $target.Handle
$rect = New-Object GlassDesk.HitProbe+RECT
[void][GlassDesk.HitProbe]::GetWindowRect($hwnd, [ref]$rect)

$width = $rect.Right - $rect.Left
$height = $rect.Bottom - $rect.Top

Write-Output ("window: {0}" -f $target.Title)
Write-Output ("handle: {0}" -f $hwnd)
Write-Output ("rect:   ({0},{1}) {2}x{3}" -f $rect.Left, $rect.Top, $width, $height)

function Probe([string]$label, [int]$x, [int]$y) {
    $pt = New-Object GlassDesk.HitProbe+POINT
    $pt.X = $x
    $pt.Y = $y

    # WindowFromPoint reflects the window's shape but NOT WM_NCHITTEST, so on its own it
    # cannot tell whether the rounded-corner pass-through works. Kept as a shape probe.
    $hit = [GlassDesk.HitProbe]::WindowFromPoint($pt)
    $shape = if ($hit -eq $hwnd) { 'in shape' } else { 'out of shape' }

    # Send WM_NCHITTEST and read the return code. This is the measurement that actually
    # exercises the handler: HTTRANSPARENT (-1) means the click is forwarded to the window
    # underneath.
    $WM_NCHITTEST = 0x0084
    $packed = [System.IntPtr]((($y -band 0xFFFF) -shl 16) -bor ($x -band 0xFFFF))
    $code = [int][GlassDesk.HitProbe]::SendMessage($hwnd, $WM_NCHITTEST, [System.IntPtr]::Zero, $packed)

    $meaning = switch ($code) {
        -1 { 'HTTRANSPARENT -> passes through' }
        0 { 'HTNOWHERE' }
        1 { 'HTCLIENT -> handled here' }
        2 { 'HTCAPTION' }
        default { "code $code" }
    }

    Write-Output ("  {0,-32} ({1},{2})  {3,-14} {4}" -f $label, $x, $y, $shape, $meaning)
}

# The chrome inset is 16 DIP; at 150% that is 24 physical pixels.
$inset = 24

# Corner radius is 28 DIP = 42 physical at 150%.
$radius = 42

Write-Output ''
Write-Output 'probes:'

# Shadow margin: fully transparent, outside the panel entirely.
Probe 'shadow margin (top-left)' ($rect.Left + 4) ($rect.Top + 4)

# Panel rect corner, but outside the rounded arc: this is the point that used to be
# swallowed by the drop shadow's semi-transparent pixels.
Probe 'panel corner (outside the arc)' ($rect.Left + $inset + 4) ($rect.Top + $inset + 4)

# Inside the arc, at the same offset from the corner along the diagonal.
$diagonal = [int]($radius * 0.5)
Probe 'panel corner (inside the arc)' ($rect.Left + $inset + $radius) ($rect.Top + $inset + $radius)

# Well inside the panel.
Probe 'panel centre' ($rect.Left + [int]($width / 2)) ($rect.Top + [int]($height / 2))

# Inside the panel, near the left edge.
Probe 'panel left edge inside' ($rect.Left + $inset + 4) ($rect.Top + [int]($height / 2))

Write-Output ''
Write-Output 'Expected: the two points outside the rounded rectangle return HTTRANSPARENT,'
Write-Output 'and points inside it are handled by the window itself.'