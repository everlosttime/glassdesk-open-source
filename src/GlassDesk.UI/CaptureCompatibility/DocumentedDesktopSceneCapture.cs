using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassDesk.UI.Controls;

/// <summary>
/// A live scene made from documented DWM thumbnails. Own-process windows are never
/// registered, so they remain screenshot-visible without recursively sampling the glass.
/// All resources and thumbnail changes belong to the feed's capture STA.
/// </summary>
internal sealed class DocumentedDesktopSceneCapture : IDesktopSceneCapture
{
    private readonly ScreenRect _region;
    private readonly Action<CaptureTrace>? _trace;
    private readonly Func<int[]>? _excludedProcesses;
    private nint _carrier, _owner;
    private HwndSource? _surface;
    private readonly DrawingVisual _background = new();
    private readonly WallpaperBackdrop _wallpaper = new();
    private PersistentWindowCapture? _capture;
    private readonly List<nint> _thumbnails = [];
    private SceneWindow[]? _members;
    private long _barrier;
    internal nint CarrierHandle => _carrier;
    nint IDesktopSceneCapture.CarrierHandle => CarrierHandle;
    public long SceneRevision => _barrier;

    internal DocumentedDesktopSceneCapture(ScreenRect region, bool diagnosticOnScreen = false, Action<CaptureTrace>? trace = null)
        : this(region, diagnosticOnScreen, trace, null) { }
    internal DocumentedDesktopSceneCapture(ScreenRect region, bool diagnosticOnScreen = false,
        Action<CaptureTrace>? trace = null, Func<int[]>? excludedProcesses = null)
    {
        if (region.Width <= 0 || region.Height <= 0 || (long)region.Width * region.Height > 32_000_000)
            throw new ArgumentOutOfRangeException(nameof(region));
        _region = region; _trace = trace; _excludedProcesses = excludedProcesses;
        try
        {
            _owner = CreateWindowExW(0, "STATIC", "GlassDesk scene owner", 0x80000000, 0, 0, 1, 1, 0, 0, 0, 0);
            if (_owner == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            // A normal redirected popup lets documented thumbnails render to its surface.
            // No TOOLWINDOW, display-affinity exclusion or private composition attributes.
            _surface = new HwndSource(new HwndSourceParameters("GlassDesk compatible desktop scene", region.Width, region.Height)
            {
                WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 8, ParentWindow = _owner,
                PositionX = diagnosticOnScreen ? region.X : GetSystemMetrics(76) - region.Width - 64,
                PositionY = diagnosticOnScreen ? region.Y : GetSystemMetrics(77)
            });
            _carrier = _surface.Handle; _surface.RootVisual = _background;
            var enabled = 1;
            DwmSetWindowAttribute(_carrier, 11, ref enabled, sizeof(int)); // DISALLOW_PEEK
            DwmSetWindowAttribute(_carrier, 12, ref enabled, sizeof(int)); // EXCLUDED_FROM_PEEK
            ShowWindow(_carrier, 4);
            UpdateWallpaper();
            UpdateExclusions();
            _capture ??= new PersistentWindowCapture(_carrier, trace);
            _trace?.Invoke(new("BackdropProvider", "DocumentedThumbnails", Stopwatch.GetTimestamp()));
        }
        catch { Dispose(); throw; }
    }

    internal bool UpdateExclusions()
    {
        UpdateWallpaper();
        var members = Snapshot();
        if (_members is not null && _members.AsSpan().SequenceEqual(members)) return false;
        var next = new List<nint>();
        try
        {
            // EnumWindows is top to bottom. Register bottom to top so newer thumbnails
            // are composited above earlier ones, over the wallpaper surface.
            foreach (var window in members)
            {
                var hr = DwmRegisterThumbnail(_carrier, window.Handle, out var thumbnail);
                if (hr < 0)
                {
                    continue; // A disappearing/unsupported foreign window is not fatal.
                }
                next.Add(thumbnail);
                if (DwmQueryThumbnailSourceSize(thumbnail, out var size) < 0 || size.Width <= 0 || size.Height <= 0)
                {
                    continue;
                }
                var sourceBounds = window.Bounds;
                var left = Math.Max(_region.X, sourceBounds.X);
                var top = Math.Max(_region.Y, sourceBounds.Y);
                var right = Math.Min(_region.X + _region.Width, sourceBounds.X + sourceBounds.Width);
                var bottom = Math.Min(_region.Y + _region.Height, sourceBounds.Y + sourceBounds.Height);
                if (right <= left || bottom <= top) continue;
                var properties = new ThumbnailProperties
                {
                    Flags = 0x1f,
                    Destination = new(left - _region.X, top - _region.Y, right - _region.X, bottom - _region.Y),
                    Source = new(Scale(left - sourceBounds.X, size.Width, sourceBounds.Width),
                        Scale(top - sourceBounds.Y, size.Height, sourceBounds.Height),
                        Scale(right - sourceBounds.X, size.Width, sourceBounds.Width),
                        Scale(bottom - sourceBounds.Y, size.Height, sourceBounds.Height)),
                    Opacity = 255, Visible = 1, ClientOnly = 0
                };
                hr = DwmUpdateThumbnailProperties(thumbnail, ref properties);
                if (hr < 0) _trace?.Invoke(new("Thumbnail", "ForeignWindowUnavailable", Stopwatch.GetTimestamp()));
            }
            foreach (var thumbnail in _thumbnails) DwmUnregisterThumbnail(thumbnail);
            _thumbnails.Clear(); _thumbnails.AddRange(next); next.Clear();
            _members = members;
            _barrier = Stopwatch.GetTimestamp();
            var restart = _capture is not null;
            _capture?.Dispose(); _capture = null;
            Marshal.ThrowExceptionForHR(DwmFlush());
            // A static public thumbnail may emit only its one changed frame. Draining
            // that frame after commit would leave the consumer waiting forever. Start
            // a fresh pool at the committed scene boundary instead.
            if (restart) _capture = new PersistentWindowCapture(_carrier, _trace);
            _trace?.Invoke(new("Scene", "DocumentedSceneChanged", _barrier, BarrierTicks: _barrier));
            return true;
        }
        finally { foreach (var thumbnail in next) DwmUnregisterThumbnail(thumbnail); }
    }

    private static int Scale(int coordinate, int pixels, int extent) => (int)((long)coordinate * pixels / extent);

    private void UpdateWallpaper()
    {
        if (_surface is null || !_wallpaper.Update(new System.Windows.Rect(_region.X, _region.Y, _region.Width, _region.Height),
            _region.Width, _region.Height, 0)) return;
        var pixels = _wallpaper.Pixels;
        if (pixels is null)
        {
            // A removed wallpaper file/COM restart still has a Windows background color.
            // This is an explicit compatibility path; dynamic wallpaper needs the worker.
            var color = GetSysColor(1);
            pixels = new byte[checked(_region.Width * _region.Height * 4)];
            for (var i = 0; i < pixels.Length; i += 4)
            { pixels[i] = (byte)(color >> 16); pixels[i + 1] = (byte)(color >> 8); pixels[i + 2] = (byte)color; pixels[i + 3] = 255; }
        }
        var bitmap = BitmapSource.Create(_region.Width, _region.Height, 96, 96, PixelFormats.Bgra32, null,
            pixels, _region.Width * 4);
        var scale = _surface.CompositionTarget.TransformFromDevice;
        using var drawing = _background.RenderOpen();
        drawing.DrawImage(bitmap, new System.Windows.Rect(0, 0, _region.Width * scale.M11, _region.Height * scale.M22));
    }

    private SceneWindow[] Snapshot()
    {
        var windows = new List<SceneWindow>();
        var excluded = _excludedProcesses?.Invoke() ?? [];
        var shell = GetShellWindow();
        if (shell == 0 || !GetWindowRect(shell, out var desktop))
            throw new InvalidOperationException("Desktop shell is unavailable for compatible capture.");
        EnumWindowProc callback = (hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (hwnd == shell || pid == 0 || pid == Environment.ProcessId || excluded.Contains((int)pid)
                || !IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
            if (DwmGetWindowAttribute(hwnd, 14, out int cloaked, sizeof(int)) >= 0 && cloaked != 0) return true;
            if (DwmGetWindowAttribute(hwnd, 9, out Rect bounds, Marshal.SizeOf<Rect>()) < 0 && !GetWindowRect(hwnd, out bounds)) return true;
            if (bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top || bounds.Left >= _region.X + _region.Width
                || bounds.Top >= _region.Y + _region.Height || bounds.Right <= _region.X || bounds.Bottom <= _region.Y) return true;
            windows.Add(new(hwnd, new(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top)));
            return true;
        };
        if (!EnumWindows(callback, 0)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        windows.Reverse();
        return windows.ToArray();
    }

    internal CapturePixels? TryRead(TimeSpan waitForFrame = default, CancellationToken cancellation = default,
        long sourceNotBeforeTicks = 0, bool reusePixels = false)
    {
        UpdateExclusions();
        var frame = _capture!.TryRead(Math.Max(_barrier, sourceNotBeforeTicks), waitForFrame, cancellation, reusePixels);
        return UpdateExclusions() ? null : frame;
    }
    internal void ReleaseReadbackBuffers() => _capture?.ReleaseReadbackBuffers();
    bool IDesktopSceneCapture.UpdateExclusions() => UpdateExclusions();
    CapturePixels? IDesktopSceneCapture.TryRead(TimeSpan wait, CancellationToken cancellation, long notBefore, bool reuse)
        => TryRead(wait, cancellation, notBefore, reuse);
    void IDesktopSceneCapture.ReleaseReadbackBuffers() => ReleaseReadbackBuffers();

    public void Dispose()
    {
        _capture?.Dispose(); _capture = null;
        foreach (var thumbnail in _thumbnails) DwmUnregisterThumbnail(thumbnail);
        _thumbnails.Clear();
        _surface?.Dispose(); _surface = null; _carrier = 0;
        if (_owner != 0) { DestroyWindow(_owner); _owner = 0; }
    }

    private readonly record struct SceneWindow(nint Handle, ScreenRect Bounds);
    [StructLayout(LayoutKind.Sequential)] private struct Rect(int left, int top, int right, int bottom)
    { public int Left = left, Top = top, Right = right, Bottom = bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Size { public int Width, Height; }
    [StructLayout(LayoutKind.Sequential)] private struct ThumbnailProperties
    { public uint Flags; public Rect Destination, Source; public byte Opacity; public int Visible, ClientOnly; }
    private delegate bool EnumWindowProc(nint hwnd, nint parameter);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint CreateWindowExW(uint extendedStyle,
        [MarshalAs(UnmanagedType.LPWStr)] string className, [MarshalAs(UnmanagedType.LPWStr)] string name,
        uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern uint GetSysColor(int index);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowProc callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("dwmapi.dll")] private static extern int DwmRegisterThumbnail(nint destination, nint source, out nint thumbnail);
    [DllImport("dwmapi.dll")] private static extern int DwmUnregisterThumbnail(nint thumbnail);
    [DllImport("dwmapi.dll")] private static extern int DwmUpdateThumbnailProperties(nint thumbnail, ref ThumbnailProperties properties);
    [DllImport("dwmapi.dll")] private static extern int DwmQueryThumbnailSourceSize(nint thumbnail, out Size size);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out Rect value, int size);
}
