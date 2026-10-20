using System.Runtime.InteropServices;
using Microsoft.Win32;
using GlassDesk.Platform.Displays;

namespace GlassDesk.Platform;

/// <summary>The native DESKTOP_WALLPAPER_POSITION values.</summary>
public enum DesktopWallpaperPosition : uint
{
    Center = 0,
    Tile = 1,
    Stretch = 2,
    Fit = 3,
    Fill = 4,
    Span = 5,
}

/// <summary>A wallpaper image and the physical screen bounds it belongs to.</summary>
/// <remarks>An empty path represents a solid-color desktop.</remarks>
public sealed record WallpaperMonitor(string Path, PixelRect Bounds);

/// <summary>A read-only snapshot of Windows' current wallpaper configuration.</summary>
/// <param name="BackgroundColor">Native COLORREF, encoded as 0x00BBGGRR.</param>
public sealed record DesktopWallpaperSnapshot(
    IReadOnlyList<WallpaperMonitor> Monitors,
    DesktopWallpaperPosition Position,
    uint BackgroundColor);

/// <summary>Reads wallpaper metadata without capturing the desktop or changing its settings.</summary>
/// <remarks>
/// Call from a COM-initialized capture worker and cache the returned snapshot.
/// The result contains no COM objects. The caller chooses its refresh interval.
/// </remarks>
public static class DesktopWallpaperSource
{
    private static readonly Guid WallpaperClassId = new("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD");

    public static DesktopWallpaperSnapshot? TryRead()
        => TryReadCom() ?? TryReadSystemWallpaper();

    private static DesktopWallpaperSnapshot? TryReadCom()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 2))
        {
            return null;
        }

        object? instance = null;
        try
        {
            var type = Type.GetTypeFromCLSID(WallpaperClassId, throwOnError: false);
            if (type is null)
            {
                return null;
            }

            instance = Activator.CreateInstance(type);
            if (instance is not IDesktopWallpaper wallpaper ||
                wallpaper.GetMonitorDevicePathCount(out var count) != 0 ||
                count is 0 or > 256 ||
                wallpaper.GetPosition(out var position) != 0 ||
                position > DesktopWallpaperPosition.Span ||
                wallpaper.GetBackgroundColor(out var background) != 0)
            {
                return null;
            }

            var monitors = new List<WallpaperMonitor>((int)count);
            for (uint index = 0; index < count; index++)
            {
                // A monitor may disappear between enumeration and GetMonitorRECT.
                // S_FALSE means detached; never treat its uninitialized bounds as a screen.
                if (wallpaper.GetMonitorDevicePathAt(index, out var monitorId) != 0 ||
                    string.IsNullOrEmpty(monitorId) ||
                    wallpaper.GetMonitorRect(monitorId, out var rect) != 0 ||
                    wallpaper.GetWallpaper(monitorId, out var path) != 0)
                {
                    continue;
                }

                var width = (long)rect.Right - rect.Left;
                var height = (long)rect.Bottom - rect.Top;
                if (width is <= 0 or > int.MaxValue || height is <= 0 or > int.MaxValue)
                {
                    continue;
                }

                monitors.Add(new WallpaperMonitor(path ?? string.Empty,
                    new PixelRect(rect.Left, rect.Top, (int)width, (int)height)));
            }

            return monitors.Count == 0
                ? null
                : new DesktopWallpaperSnapshot(Array.AsReadOnly(monitors.ToArray()), position, background);
        }
        catch (Exception exception) when (exception is COMException or UnauthorizedAccessException
            or InvalidCastException or NotSupportedException or TypeLoadException)
        {
            // Explorer/COM can be unavailable during sign-in, a restart, or a remote session.
            // The Dock keeps its transparent material when no reliable wallpaper exists.
            return null;
        }
        finally
        {
            if (instance is not null && Marshal.IsComObject(instance))
            {
                Marshal.ReleaseComObject(instance);
            }
        }
    }

    private static DesktopWallpaperSnapshot? TryReadSystemWallpaper()
    {
        // Public read-only fallback when the wallpaper COM service is unavailable.
        // SPI exposes one desktop image; retain only the primary monitor's snapshot.
        // Per-monitor images still require IDesktopWallpaper and separate validation.
        var path = new char[260];
        if (!SystemParametersInfo(0x0073, (uint)path.Length, path, 0)) return null;
        var primary = DisplayService.Primary();
        if (primary is null) return null;
        var position = DesktopWallpaperPosition.Fill;
        try
        {
            using var desktop = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", false);
            position = desktop?.GetValue("TileWallpaper")?.ToString() == "1" ? DesktopWallpaperPosition.Tile
                : desktop?.GetValue("WallpaperStyle")?.ToString() switch
                {
                    "0" => DesktopWallpaperPosition.Center,
                    "2" => DesktopWallpaperPosition.Stretch,
                    "6" => DesktopWallpaperPosition.Fit,
                    _ => DesktopWallpaperPosition.Fill
                };
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException)
        { return null; }
        var length = Array.IndexOf(path, '\0');
        return new([new WallpaperMonitor(new string(path, 0, length < 0 ? path.Length : length), primary.Bounds)], position, GetSysColor(1));
    }

    [DllImport("user32.dll", EntryPoint="SystemParametersInfoW", CharSet=CharSet.Unicode, SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint count,
        [Out, MarshalAs(UnmanagedType.LPArray, ArraySubType=UnmanagedType.U2)] char[] value, uint flags);
    [DllImport("user32.dll")] private static extern uint GetSysColor(int index);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    // Method order and GUIDs match the Windows SDK's ShObjIdl_core.h. Keep unused setter
    // slots so that the getters resolve to their actual COM vtable offsets.
    [ComImport]
    [Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDesktopWallpaper
    {
        [PreserveSig]
        int SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorId,
            [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);

        [PreserveSig]
        int GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorId,
            [MarshalAs(UnmanagedType.LPWStr)] out string? wallpaper);

        [PreserveSig]
        int GetMonitorDevicePathAt(uint monitorIndex,
            [MarshalAs(UnmanagedType.LPWStr)] out string? monitorId);

        [PreserveSig]
        int GetMonitorDevicePathCount(out uint count);

        [PreserveSig]
        int GetMonitorRect([MarshalAs(UnmanagedType.LPWStr)] string monitorId, out NativeRect displayRect);

        [PreserveSig]
        int SetBackgroundColor(uint color);

        [PreserveSig]
        int GetBackgroundColor(out uint color);

        [PreserveSig]
        int SetPosition(DesktopWallpaperPosition position);

        [PreserveSig]
        int GetPosition(out DesktopWallpaperPosition position);
    }
}
