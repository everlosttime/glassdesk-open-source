using System.Runtime.InteropServices;
using GlassDesk.Platform.Displays;

namespace GlassDesk.Platform;

/// <summary>Own-process capture exclusion with a fail-closed registry. No content is saved.</summary>
public sealed class CaptureExclusionSession : IDisposable
{
    private static readonly Dictionary<nint, bool> Windows = [];
    private static readonly object Gate = new();
    private readonly nint _handle;
    private readonly uint _previous;
    private bool _disposed;
    private CaptureExclusionSession(nint handle, uint previous) { _handle = handle; _previous = previous; }

    public static CaptureExclusionSession? TryCreate(nint handle)
    {
        lock (Gate)
        {
            if (handle == 0 || GetWindowThreadProcessId(handle, out var process) == 0 || process != Environment.ProcessId) return null;
            // Unsupported layered-window combinations MUST remain in the failed registry.
            Windows[handle] = false;
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)
                || !GetWindowDisplayAffinity(handle, out var previous)) return null;
            if (!SetWindowDisplayAffinity(handle, 0x11)) return null;
            if (!GetWindowDisplayAffinity(handle, out var current) || current != 0x11)
            {
                SetWindowDisplayAffinity(handle, previous);
                return null;
            }
            Windows[handle] = true;
            return new CaptureExclusionSession(handle, previous);
        }
    }

    public static bool CanCapture
    {
        get
        {
            lock (Gate)
            {
                foreach (var handle in Windows.Keys.Where(handle => !IsWindow(handle)).ToArray()) Windows.Remove(handle);
                return Windows.Count > 0 && Windows.All(pair => pair.Value
                    && GetWindowDisplayAffinity(pair.Key, out var affinity) && affinity == 0x11);
            }
        }
    }

    internal static bool CanCaptureRegion(PixelRect region)
    {
        if (!CanCapture) return false;
        var safe = true;
        // WPF Popup HWNDs are not GlassWindows. Never recapture their menus/tooltips.
        var enumerated = EnumWindows((handle, _) =>
        {
            if (GetWindowThreadProcessId(handle, out var process) == 0) { safe = false; return false; }
            if (process != Environment.ProcessId || !IsWindowVisible(handle)) return true;
            lock (Gate) { if (Windows.TryGetValue(handle, out var excluded) && excluded) return true; }
            if (GetWindowRect(handle, out var bounds) && bounds.Left < region.Right && bounds.Right > region.X
                && bounds.Top < region.Bottom && bounds.Bottom > region.Y) { safe = false; return false; }
            return true;
        }, 0);
        return enumerated && safe;
    }

    // Before revealing a hidden window, no affinity change is necessary. Reject
    // overlapping product windows/popups rather than sampling our own UI.
    internal static bool CanCaptureBeforeReveal(PixelRect region, nint emptyOwner = 0)
    {
        var safe = true;
        var enumerated = EnumWindows((handle, _) =>
        {
            if (GetWindowThreadProcessId(handle, out var process) == 0) { safe = false; return false; }
            if (process != Environment.ProcessId || !IsWindowVisible(handle) || handle == emptyOwner) return true;
            if (!GetWindowRect(handle, out var bounds)) { safe = false; return false; }
            if (bounds.Left < region.Right && bounds.Right > region.X && bounds.Top < region.Bottom && bounds.Bottom > region.Y)
            { safe = false; return false; }
            return true;
        }, 0);
        return enumerated && safe;
    }

    public static void Forget(nint handle) { lock (Gate) Windows.Remove(handle); }
    public void Dispose()
    {
        lock (Gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (IsWindow(_handle)) SetWindowDisplayAffinity(_handle, _previous);
            Windows.Remove(_handle);
        }
    }

    private delegate bool EnumWindowProc(nint handle, nint data);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(nint handle, uint affinity);
    [DllImport("user32.dll")] private static extern bool GetWindowDisplayAffinity(nint handle, out uint affinity);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint handle, out uint process);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint handle);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint handle);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint handle, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowProc callback, nint data);
}
