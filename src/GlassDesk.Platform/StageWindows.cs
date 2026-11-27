using System.Runtime.InteropServices;
using GlassDesk.Platform.Displays;
using GlassDesk.Platform.Interop;

namespace GlassDesk.Platform;

public readonly record struct StageWindowIdentity(long Handle, uint Process, uint Thread, long ProcessStarted)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public nint Hwnd => (nint)Handle;
}

[StructLayout(LayoutKind.Sequential)]
public record struct StageWindowPlacement(
    int Size, int Flags, int ShowCommand, int MinX, int MinY, int MaxX, int MaxY,
    int Left, int Top, int Right, int Bottom);

public sealed record StageWindow(
    StageWindowIdentity Identity, string Title, PixelRect Bounds, StageWindowPlacement Placement,
    Guid Desktop, bool Minimized)
{
    public nint Handle => Identity.Hwnd;
}

public sealed class StageWindows : IDisposable
{
    public const uint ForegroundEvent = 3;

    private readonly IVirtualDesktopManager? _desktops;

    public StageWindows()
    {
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("AA509086-5CA9-4C25-8F95-589D3C07B48A"));
            _desktops = type is null ? null : Activator.CreateInstance(type) as IVirtualDesktopManager;
        }
        catch (COMException) { }
    }

    public bool IsAvailable => _desktops is not null;
    public static nint Foreground => User32.GetForegroundWindow();
    public static nint RootOwner(nint handle) => WindowTopmostWindows.RootOwner(handle);
    public static bool IsMinimized(nint handle) => User32.IsIconic(handle);
    public static bool IsDesktopForeground => Foreground == User32.GetShellWindow() || Foreground == User32.GetDesktopWindow()
        || User32.ReadClassName(Foreground) is "Progman" or "WorkerW";

    public static uint? GetLastInputTick()
    {
        var input = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref input) ? input.Tick : null;
    }

    public IReadOnlyList<StageWindow> Enumerate(Func<nint, bool> include)
    {
        var windows = new List<StageWindow>();
        if (_desktops is null) return windows;
        User32.EnumWindows((handle, _) =>
        {
            if (!include(handle)) return 1;
            var window = Inspect(handle);
            if (window is not null) windows.Add(window);
            return 1;
        }, 0);
        return windows;
    }

    public StageWindow? Inspect(nint handle)
    {
        if (_desktops is null || handle == 0 || handle == User32.GetShellWindow()
            || !User32.IsWindowVisible(handle) || User32.GetWindow(handle, User32.GwOwner) != 0
            || IsHungAppWindow(handle) || DwmApi.IsCloaked(handle)
            || (User32.GetWindowLong(handle, User32.GwlExStyle) & (0x80 | 0x08 | 0x08000000)) != 0
            || User32.ReadClassName(handle) is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            return null;
        if (_desktops.IsWindowOnCurrentVirtualDesktop(handle, out var current) < 0 || !current
            || _desktops.GetWindowDesktopId(handle, out var desktop) < 0)
            return null;

        var window = WindowTopmostWindows.Capture(handle, selectable: false);
        var placement = new StageWindowPlacement { Size = Marshal.SizeOf<StageWindowPlacement>() };
        if (window is null || string.IsNullOrWhiteSpace(window.Title) || !GetWindowPlacement(handle, ref placement))
            return null;
        return new(new(handle, window.ProcessId, window.ThreadId, window.ProcessStarted), window.Title,
            window.Bounds, placement, desktop, IsMinimized(handle));
    }

    public static bool IsPresented(StageWindowIdentity identity)
    {
        var window = identity.Hwnd;
        return Matches(identity) && User32.IsWindowVisible(window) && !IsMinimized(window) && !DwmApi.IsCloaked(window)
            && Display32.GetWindowRect(window, out var bounds) && bounds.Width > 0 && bounds.Height > 0
            && (bounds.Left, bounds.Top) != (-32000, -32000);
    }

    public static bool Matches(StageWindowIdentity identity)
    {
        var thread = User32.GetWindowThreadProcessId(identity.Hwnd, out var process);
        if (thread == 0 || thread != identity.Thread || process != identity.Process || process == Environment.ProcessId)
            return false;
        var processHandle = Kernel32.OpenProcess(Kernel32.ProcessQueryLimitedInformation, false, process);
        if (processHandle == 0) return false;
        try
        {
            return WindowTopmostNative.GetProcessTimes(processHandle, out var started, out _, out _, out _)
                && started == identity.ProcessStarted;
        }
        finally { Kernel32.CloseHandle(processHandle); }
    }

    public static bool CanCapture(nint handle) => !IsMinimized(handle)
        && (!GetWindowDisplayAffinity(handle, out var affinity) || affinity == 0);

    private const string ParkingProperty = "GlassDesk.StageManager.Parking";

    public static bool MarkParked(StageWindowIdentity identity, int token) => Matches(identity)
        && SetProp(identity.Hwnd, ParkingProperty, token);

    public static bool HasParkingMark(StageWindowIdentity identity, int token) => Matches(identity)
        && GetProp(identity.Hwnd, ParkingProperty) == token;

    public static void ClearParkingMark(StageWindowIdentity identity, int token)
    {
        if (HasParkingMark(identity, token)) _ = RemoveProp(identity.Hwnd, ParkingProperty);
    }

    public static bool Minimize(StageWindowIdentity identity) => Matches(identity)
        && (IsMinimized(identity.Hwnd) || ShowWindowAsync(identity.Hwnd, 7)); // SW_SHOWMINNOACTIVE

    public static bool Restore(StageWindowIdentity identity, StageWindowPlacement saved)
    {
        if (!Matches(identity)) return false;
        // Ask Windows to use its last placement and retain its own Snap bookkeeping.
        // WINDOWPLACEMENT is a recovery path for a window that still reports minimized.
        return ShowWindowAsync(identity.Hwnd, saved.ShowCommand == 3 ? 3 : 4);
    }

    public static bool RestorePlacement(StageWindowIdentity identity, StageWindowPlacement saved)
    {
        if (!Matches(identity)) return false;
        saved.Size = Marshal.SizeOf<StageWindowPlacement>();
        saved.Flags = 4; // WPF_ASYNCWINDOWPLACEMENT
        saved.ShowCommand = saved.ShowCommand == 3 ? 3 : 4;
        return SetWindowPlacement(identity.Hwnd, in saved);
    }

    private const uint KeepWindowGeometry = 0x0001 | 0x0002 | 0x0200 | 0x4000; // No size/move/owner reorder, async.
    private const uint NoActivate = 0x0010;

    public static void Raise(StageWindowIdentity identity)
    {
        if (Matches(identity))
            _ = Dpi32.SetWindowPos(identity.Hwnd, 0, 0, 0, 0, 0, KeepWindowGeometry | NoActivate);
    }

    public static bool StackBehind(StageWindowIdentity identity, nint front) =>
        front != identity.Hwnd && User32.IsWindow(front) && Matches(identity)
        && !WindowTopmostWindows.IsTopmost(front) && !WindowTopmostWindows.IsTopmost(identity.Hwnd)
        && Dpi32.SetWindowPos(identity.Hwnd, front, 0, 0, 0, 0, KeepWindowGeometry | NoActivate);

    public static bool IsDirectlyBehind(nint window, nint front) =>
        User32.GetWindow(front, 2) == window; // GW_HWNDNEXT

    public static void Activate(StageWindowIdentity identity)
    {
        if (!Matches(identity) || IsMinimized(identity.Hwnd)) return;
        var root = identity.Hwnd;
        var popup = GetLastActivePopup(root);
        var target = popup != 0 && RootOwner(popup) == root && User32.IsWindowVisible(popup)
            && IsWindowEnabled(popup) && (User32.GetWindowLong(popup, User32.GwlExStyle) & 0x08000000) == 0
            ? popup : root;
        if (!WindowActivation.TryBringToForeground(target) && IsWindowEnabled(target))
            _ = Dpi32.SetWindowPos(target, 0, 0, 0, 0, 0, KeepWindowGeometry);
    }

    public void Dispose()
    {
        if (_desktops is not null) Marshal.ReleaseComObject(_desktops);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo { public uint Size, Tick; }

    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInputInfo input);

    [ComImport, Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopManager
    {
        [PreserveSig] int IsWindowOnCurrentVirtualDesktop(nint window, [MarshalAs(UnmanagedType.Bool)] out bool current);
        [PreserveSig] int GetWindowDesktopId(nint window, out Guid desktop);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetProp(nint window, string name, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint GetProp(nint window, string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint RemoveProp(nint window, string name);
    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(nint window, ref StageWindowPlacement placement);
    [DllImport("user32.dll")] private static extern bool SetWindowPlacement(nint window, in StageWindowPlacement placement);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint window, int command);
    [DllImport("user32.dll")] private static extern bool IsHungAppWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(nint window);
    [DllImport("user32.dll")] private static extern nint GetLastActivePopup(nint window);
    [DllImport("user32.dll")] private static extern bool GetWindowDisplayAffinity(nint window, out uint affinity);
}
