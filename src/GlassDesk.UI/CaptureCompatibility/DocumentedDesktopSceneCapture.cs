using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using GlassDesk.UI.CaptureCompatibility;
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
    private readonly bool _diagnosticOnScreen;
    private readonly Func<nint> _desktopWindow;
    private nint _carrier, _owner;
    private NativeWallpaperSurface? _nativeWallpaper;
    private readonly WallpaperBackdrop _wallpaper = new();
    private readonly bool _requireVerifiedWallpaper;
    private readonly bool _desktopOnly;
    private IWindowCapture? _capture;
    private DesktopBackgroundComposer? _backgroundComposer;
    private byte[]? _backgroundPixels;
    private long _nextBackgroundTraceTicks;
    private readonly List<nint> _thumbnails = [];
    private readonly HashSet<nint> _excludedOverlays = [];
    private SceneWindow[]? _members;
    private nint _lastDesktopWindow;
    private long _barrier;
    // 动态壁纸：画面只存在于壁纸引擎自己的子窗口里，DWM 缩略图那条路拿不到实时内容。
    private nint _liveWallpaper, _liveHost;
    private long _nextWallpaperScanTicks;
    private LiveWallpaperSurface? _liveSurface;
    private byte[]? _livePixels;
    private int _liveWidth, _liveHeight;
    internal nint CarrierHandle => _carrier;
    nint IDesktopSceneCapture.CarrierHandle => CarrierHandle;
    public long SceneRevision => _barrier;

    internal DocumentedDesktopSceneCapture(ScreenRect region, bool diagnosticOnScreen = false, Action<CaptureTrace>? trace = null)
        : this(region, diagnosticOnScreen, trace, null) { }
    internal DocumentedDesktopSceneCapture(ScreenRect region, bool diagnosticOnScreen = false,
        Action<CaptureTrace>? trace = null, Func<int[]>? excludedProcesses = null)
        : this(region,diagnosticOnScreen,trace,excludedProcesses,GetShellWindow) { }

    internal DocumentedDesktopSceneCapture(ScreenRect region, bool diagnosticOnScreen, Action<CaptureTrace>? trace,
        Func<int[]>? excludedProcesses, bool desktopOnly)
        : this(region, diagnosticOnScreen, trace, excludedProcesses, GetShellWindow,
            GlassDesk.Platform.WindowsCapabilities.BuildNumber, desktopOnly) { }

    internal DocumentedDesktopSceneCapture(ScreenRect region,bool diagnosticOnScreen,
        Action<CaptureTrace>? trace,Func<int[]>? excludedProcesses,Func<nint> desktopWindow)
        : this(region, diagnosticOnScreen, trace, excludedProcesses, desktopWindow, GlassDesk.Platform.WindowsCapabilities.BuildNumber) { }

    internal DocumentedDesktopSceneCapture(ScreenRect region,bool diagnosticOnScreen,
        Action<CaptureTrace>? trace,Func<int[]>? excludedProcesses,Func<nint> desktopWindow,int windowsBuild)
        : this(region, diagnosticOnScreen, trace, excludedProcesses, desktopWindow, windowsBuild, false) { }

    internal DocumentedDesktopSceneCapture(ScreenRect region,bool diagnosticOnScreen,
        Action<CaptureTrace>? trace,Func<int[]>? excludedProcesses,Func<nint> desktopWindow,int windowsBuild,bool desktopOnly)
    {
        if (region.Width <= 0 || region.Height <= 0 || (long)region.Width * region.Height > 32_000_000)
            throw new ArgumentOutOfRangeException(nameof(region));
        _region = region; _trace = trace; _excludedProcesses = excludedProcesses; _diagnosticOnScreen = diagnosticOnScreen;
        _desktopWindow=desktopWindow;
        _requireVerifiedWallpaper = windowsBuild < 22000;
        _desktopOnly = desktopOnly;
        var stage = "SceneWindow";
        try
        {
            _owner = CreateWindowExW(0, "STATIC", "GlassDesk scene owner", 0x80000000, 0, 0, 1, 1, 0, 0, 0, 0);
            if (_owner == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            // Our native composite stays transparent and absent from taskbar/Alt+Tab.
            // It requires no WGC eligibility or capture-session permission. Product
            // windows retain their original display affinity and remain recordable.
            var carrierStyle = diagnosticOnScreen ? 8u : 0x80020u;
            if (!diagnosticOnScreen)
                carrierStyle |= 0x08000080u; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
            var windowStyle = 0x8000000Eu; // WS_POPUP | SS_BITMAP for the native composite.
            _carrier=CreateWindowExW(carrierStyle,"STATIC","GlassDesk compatible desktop scene",windowStyle,
                region.X,region.Y,region.Width,region.Height,0,0,0,0);
            if(_carrier==0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            _nativeWallpaper = new NativeWallpaperSurface(_carrier, region.Width, region.Height);
            if (!diagnosticOnScreen)
            {
                if(!SetLayeredWindowAttributes(_carrier,0,0,2))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
            var enabled = 1;
            DwmSetWindowAttribute(_carrier, 11, ref enabled, sizeof(int)); // DISALLOW_PEEK
            DwmSetWindowAttribute(_carrier, 12, ref enabled, sizeof(int)); // EXCLUDED_FROM_PEEK
            // Keep a real monitor intersection for DWM thumbnail updates, with
            // global opacity zero so the composite never becomes a desktop overlay.
            if (!SetWindowPos(_carrier, diagnosticOnScreen ? -1 : 1, region.X, region.Y, region.Width, region.Height, 0x50))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            stage = "DwmThumbnailsAndWallpaper";
            UpdateExclusions();
            stage = "WindowCapture";
            if (_diagnosticOnScreen || _backgroundComposer?.IsBackgroundOnly != true)
                _capture ??= StartWindowCapture();
            _trace?.Invoke(new("BackdropProvider", "DocumentedThumbnails", Stopwatch.GetTimestamp()));
        }
        catch (Exception error)
        {
            Dispose();
            throw new InvalidOperationException($"Documented scene initialization failed at {stage} (0x{error.HResult:X8}): {error.Message}", error);
        }
    }

    internal bool UpdateExclusions()
    {
        var members = Snapshot(out var desktopWindow);
        UpdateWallpaper();
        if (_members is not null && _lastDesktopWindow==desktopWindow && _members.AsSpan().SequenceEqual(members)) return false;
        var next = new List<nint>();
        var foreground = new List<ScreenRect>();
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
                else foreground.Add(new(left - _region.X, top - _region.Y, right - left, bottom - top));
            }
            foreach (var thumbnail in _thumbnails) DwmUnregisterThumbnail(thumbnail);
            _thumbnails.Clear(); _thumbnails.AddRange(next); next.Clear();
            _members = members;
            _backgroundComposer = new DesktopBackgroundComposer(_region.Width, _region.Height, foreground);
            _lastDesktopWindow=desktopWindow;
            _barrier = Stopwatch.GetTimestamp();
            Marshal.ThrowExceptionForHR(DwmFlush());
            // The capture item already owns this window. Recreating it after each
            // move rechecks shell-hidden HWND eligibility on Win10 and may fail.
            // Refresh only the pool/session after the committed thumbnail change;
            // keep the item/device and the original source-time barrier.
            _capture?.RefreshSessionAtSceneBoundary();
            _trace?.Invoke(new("Scene", "DocumentedSceneChanged", _barrier, BarrierTicks: _barrier));
            return true;
        }
        finally { foreach (var thumbnail in next) DwmUnregisterThumbnail(thumbnail); }
    }

    private IWindowCapture StartWindowCapture()
    {
        // This is our own native wallpaper/thumbnail composite. Read its existing
        // redirected pixels without creating a WGC session or capture indicator.
        var reader = new PrintWindowWindowCapture(_carrier, _region.Width, _region.Height, _trace);
        _trace?.Invoke(new("WindowCapture", "PrintWindow", Stopwatch.GetTimestamp()));
        return reader;
    }

    private static int Scale(int coordinate, int pixels, int extent) => (int)((long)coordinate * pixels / extent);

    private void UpdateWallpaper(bool requireStaticPixels = false)
    {
        var liveCoversRegion = !requireStaticPixels && !(_requireVerifiedWallpaper && !LiveBackdropMode.Enabled)
            && _liveWallpaper != 0 && GetWindowRect(_liveWallpaper, out var liveBounds)
            && liveBounds.Left <= _region.X && liveBounds.Top <= _region.Y
            && liveBounds.Right >= (long)_region.X + _region.Width
            && liveBounds.Bottom >= (long)_region.Y + _region.Height;
        if (_nativeWallpaper is null || !_wallpaper.Update(new System.Windows.Rect(_region.X, _region.Y, _region.Width, _region.Height),
            _region.Width, _region.Height, 0, renderPixels: !liveCoversRegion)) return;
        _wallpaper.ReleaseDecodedImages();
        var pixels = _wallpaper.Pixels;
        if (pixels is null)
        {
            // 实时模式下不再因为「壁纸文件读不到」而让整个场景失败：
            // 动态壁纸常常把 Windows 壁纸设成纯色，真正的画面在承载它的子窗口里。
            if(_requireVerifiedWallpaper && !LiveBackdropMode.Enabled)
                throw new InvalidOperationException("No verified wallpaper background is available; keep translucent glass instead of synthesizing a black live scene.");
            // A removed wallpaper file/COM restart still has a Windows background color.
            // This is an explicit compatibility path; dynamic wallpaper needs the worker.
            var color = GetSysColor(1);
            pixels = new byte[checked(_region.Width * _region.Height * 4)];
            for (var i = 0; i < pixels.Length; i += 4)
            { pixels[i] = (byte)(color >> 16); pixels[i + 1] = (byte)(color >> 8); pixels[i + 2] = (byte)color; pixels[i + 3] = 255; }
        }
        _backgroundPixels = pixels;
        if (_diagnosticOnScreen || _backgroundComposer is { IsBackgroundOnly: false })
            _nativeWallpaper.Update(pixels);
        _trace?.Invoke(new("SceneBackground", "NativeWallpaper", Stopwatch.GetTimestamp(),
            Width: _region.Width, Height: _region.Height));
    }

    private SceneWindow[] Snapshot(out nint desktopWindow)
    {
        var windows = new List<SceneWindow>();
        var excluded = _excludedProcesses?.Invoke() ?? [];
        var shell = _desktopWindow();
        desktopWindow=shell;
        if (shell == 0 || !GetWindowRect(shell, out var desktop))
            throw new InvalidOperationException("Desktop shell is unavailable for compatible capture.");
        // Rebuild the wallpaper independently of the outer desktop's owned popup
        // group. Win11 DX11 renderers may need their internal WorkerW host.
        nint liveHost = 0;
        if ((_requireVerifiedWallpaper || IsDesktopHost(shell)) && (_liveWallpaper == 0 || !IsWindow(_liveWallpaper)
            || Stopwatch.GetTimestamp() >= _nextWallpaperScanTicks))
        {
            var scanned = FindLiveWallpaper(out liveHost);
            if (scanned != _liveWallpaper || liveHost != _liveHost)
            {
                _liveWallpaper = scanned; _liveHost = liveHost;
                _liveSurface?.Dispose(); _liveSurface = null; _livePixels = null;
                _trace?.Invoke(new("Wallpaper", scanned == 0 ? "StaticOnly" : "LiveWindow", Stopwatch.GetTimestamp()));
            }
            _nextWallpaperScanTicks = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
        }
        if (_desktopOnly)
        {
            // This path never enumerates/registers applications, menus, the taskbar,
            // or our launchpad. Live wallpaper children draw into the carrier directly.
            return IsDesktopHost(shell) ? []
                : [new(shell, new(desktop.Left, desktop.Top, desktop.Right - desktop.Left, desktop.Bottom - desktop.Top))];
        }
        EnumWindowProc callback = (hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0 || pid == Environment.ProcessId || excluded.Contains((int)pid)
                || !IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
            if (DwmGetWindowAttribute(hwnd, 14, out int cloaked, sizeof(int)) >= 0 && cloaked != 0) return true;
            if((_requireVerifiedWallpaper && hwnd==shell) || IsDesktopHost(hwnd))
            {
                // A Shell thumbnail flattens the desktop-owned bars into its scene.
                // The independent live/static wallpaper already supplies this layer.
                return true;
            }
            if (DwmGetWindowAttribute(hwnd, 9, out Rect bounds, Marshal.SizeOf<Rect>()) < 0 && !GetWindowRect(hwnd, out bounds)) return true;
            if (bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top || bounds.Left >= _region.X + _region.Width
                || bounds.Top >= _region.Y + _region.Height || bounds.Right <= _region.X || bounds.Bottom <= _region.Y) return true;
            if (CompatibleWindowVisibility.ExclusionReason(hwnd) is { } reason)
            {
                if (_excludedOverlays.Count == 64) _excludedOverlays.Clear();
                if (_excludedOverlays.Add(hwnd))
                {
                    var name = new System.Text.StringBuilder(128);
                    GetClassNameW(hwnd, name, name.Capacity);
                    _trace?.Invoke(new("SceneWindowExcluded", reason, Stopwatch.GetTimestamp(),
                        WindowHandle: hwnd.ToInt64(), WindowProcess: pid, WindowClass: name.ToString(),
                        Width: bounds.Right - bounds.Left, Height: bounds.Bottom - bounds.Top));
                }
                return true;
            }
            windows.Add(new(hwnd, new(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top)));
            return true;
        };
        var completed=EnumWindows(callback,0);
        if (!completed) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        windows.Reverse();
        return windows.ToArray();
    }

    /// <summary>
    /// 找出动态壁纸窗口：桌面宿主（Progman / WorkerW）下面那个属于其它进程、
    /// 可见、并且盖住取景区域相当一部分的子窗口。返回 0 表示桌面上没有动态壁纸。
    /// </summary>
    private nint FindLiveWallpaper(out nint host)
    {
        nint best = 0, bestHost = 0;
        long bestArea = 0;
        // 子窗口至少要盖住取景区域的四分之一，才认为是壁纸而不是某个工具栏/提示条。
        var minimumArea = (long)_region.Width * _region.Height / 4;
        var excluded = _excludedProcesses?.Invoke() ?? [];
        EnumWindowProc hosts = (hwnd, _) =>
        {
            if (!IsDesktopHost(hwnd)) return true;
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out var hostPid);
            if (hostPid == 0) return true;
            EnumWindowProc children = (child, _) =>
            {
                GetWindowThreadProcessId(child, out var childPid);
                if (childPid == 0 || childPid == hostPid || childPid == Environment.ProcessId
                    || excluded.Contains((int)childPid) || !IsWindowVisible(child)) return true;
                if (DwmGetWindowAttribute(child, 9, out Rect bounds, Marshal.SizeOf<Rect>()) < 0
                    && !GetWindowRect(child, out bounds)) return true;
                var width = Math.Min(bounds.Right, _region.X + _region.Width) - Math.Max(bounds.Left, _region.X);
                var height = Math.Min(bounds.Bottom, _region.Y + _region.Height) - Math.Max(bounds.Top, _region.Y);
                if (width <= 0 || height <= 0) return true;
                var area = (long)width * height;
                if (area < minimumArea || area <= bestArea) return true;
                bestArea = area;
                best = child;
                bestHost = hwnd;
                return true;
            };
            EnumChildWindows(hwnd, children, 0);
            return true;
        };
        EnumWindows(hosts, 0);
        // DX11 renderers can return empty PrintWindow pixels. An internal
        // WorkerW composites the renderer without the outer desktop's owned bars.
        if (!_requireVerifiedWallpaper && best != 0)
        {
            var parent = GetParent(best);
            if (parent != 0 && parent != bestHost && IsDesktopHost(parent) && IsChild(bestHost, parent))
                best = parent;
        }
        host = bestHost;
        return best;
    }

    /// <summary>
    /// 把动态壁纸窗口的当前画面画进载体背景。载体背景是 WPF 绘制的，
    /// PrintWindow(载体) 会把它一并读回来，于是玻璃背后的底图就是实时壁纸，
    /// 其它窗口的 DWM 缩略图仍然盖在它上面。
    /// </summary>
    private bool DrawLiveWallpaper(long sourceNotBeforeTicks=0)
    {
        if (_requireVerifiedWallpaper && _wallpaper.Pixels is { } desktopPixels)
            _backgroundPixels = desktopPixels;
        if (_liveWallpaper == 0) return true;
        if (_nativeWallpaper is null || !GetWindowRect(_liveWallpaper, out var source)) return false;
        // The foreign window can move after Snapshot/UpdateWallpaper. Base this
        // fallback decision on the very same rectangle passed to TryDraw below.
        if (source.Left > _region.X || source.Top > _region.Y
            || source.Right < (long)_region.X + _region.Width || source.Bottom < (long)_region.Y + _region.Height)
            UpdateWallpaper(requireStaticPixels: true);
        var sourceWidth = source.Right - source.Left;
        var sourceHeight = source.Bottom - source.Top;
        if (sourceWidth <= 0 || sourceHeight <= 0) return false;
        var windowWidth=sourceWidth;var windowHeight=sourceHeight;
        if(GlassDesk.Platform.Displays.DisplayService.Primary() is { } primary)
        {
            var readback=WallpaperReadbackExtent.LimitToPrimary(new(source.Left,source.Top,sourceWidth,sourceHeight),
                new(primary.Bounds.X,primary.Bounds.Y,primary.Bounds.Width,primary.Bounds.Height),_region);
            sourceWidth=readback.Width;sourceHeight=readback.Height;
        }
        if (_liveSurface is null || _liveWidth != sourceWidth || _liveHeight != sourceHeight)
        {
            _liveSurface?.Dispose();
            _liveSurface = new LiveWallpaperSurface(sourceWidth, sourceHeight, _region.Width, _region.Height, _trace,
                Environment.GetEnvironmentVariable(SharedWallpaperBuffer.ScopeEnvironmentVariable));
            _liveWidth = sourceWidth; _liveHeight = sourceHeight;
            _livePixels = new byte[_region.Width * _region.Height * 4];
            _trace?.Invoke(new("WallpaperBuffer","Created",Stopwatch.GetTimestamp(),WindowHandle:_liveWallpaper,
                Width:sourceWidth,Height:sourceHeight,Detail:$"target={_region.Width}x{_region.Height};sourceRgbaBytes={(long)sourceWidth*sourceHeight*4};window={windowWidth}x{windowHeight};primaryBounded={sourceWidth!=windowWidth || sourceHeight!=windowHeight}"));
        }
        if (_liveSurface is null || _livePixels is null) return false;
        if (_wallpaper.Pixels is { } fallback && fallback.Length == _livePixels.Length)
            Buffer.BlockCopy(fallback, 0, _livePixels, 0, fallback.Length);
        if (!_liveSurface.TryDraw(_liveWallpaper, _region.X, _region.Y, _region.Width, _region.Height,
                source.Left, source.Top, source.Right, source.Bottom, _livePixels,sourceNotBeforeTicks)) return false;
        _backgroundPixels = _livePixels;
        // With no foreign coverage the final composer would overwrite the entire
        // carrier read with these same pixels. Avoid its upload, flush and readback.
        if (_diagnosticOnScreen || _backgroundComposer?.IsBackgroundOnly != true)
            _nativeWallpaper.Update(_livePixels);
        return true;
    }

    private static unsafe bool IsDesktopHost(nint hwnd)
    {
        Span<char> name = stackalloc char[16];
        fixed (char* buffer = name)
        {
            var length = GetClassNameW(hwnd, buffer, name.Length);
            var className = name[..length];
            return className.SequenceEqual("Progman") || className.SequenceEqual("WorkerW");
        }
    }

    internal CapturePixels? TryRead(TimeSpan waitForFrame = default, CancellationToken cancellation = default,
        long sourceNotBeforeTicks = 0, bool reusePixels = false)
    {
        if (_carrier == 0 || cancellation.IsCancellationRequested
            || sourceNotBeforeTicks > Stopwatch.GetTimestamp()) return null;
        UpdateExclusions();
        // 每帧把动态壁纸画进载体背景：这一层没有别的办法拿到实时画面。
        if (!DrawLiveWallpaper(Math.Max(_barrier,sourceNotBeforeTicks)))
        {
            // A failed live acquisition is unavailable, not a new static/black
            // wallpaper frame. Let the caller retry within its freshness policy.
            _trace?.Invoke(new("CaptureFailure", "LiveWallpaperReadbackUnavailable", Stopwatch.GetTimestamp(),
                WindowHandle: _liveWallpaper));
            return null;
        }
        if (!_diagnosticOnScreen && _backgroundComposer?.IsBackgroundOnly == true && _backgroundPixels is { } pixels)
        {
            var sourceTicks = _liveWallpaper!=0 ? _liveSurface!.LastSourceQpcTicks : Stopwatch.GetTimestamp();
            if (cancellation.IsCancellationRequested || sourceTicks < Math.Max(_barrier, sourceNotBeforeTicks)) return null;
            // Only explicit reuse consumers may observe the next acquisition's writes.
            // The source was freshly read above; do not replay a failed live read.
            var direct = new CapturePixels(_region.Width, _region.Height, reusePixels ? pixels : pixels.ToArray())
                { SourceQpcTicks = sourceTicks };
            if (UpdateExclusions()) return null;
            _capture?.Dispose(); _capture = null;
            _nativeWallpaper?.ReleaseBitmap();
            _trace?.Invoke(new("BackgroundCapture", "Direct", sourceTicks, SourceTicks: sourceTicks,
                Width: _region.Width, Height: _region.Height));
            return direct;
        }
        // Static wallpaper may not have changed since the carrier was retired.
        // Restore it before the first composite read after foreign coverage returns.
        if (_nativeWallpaper is { HasBitmap: false } && _backgroundPixels is { } restoredBackground)
            _nativeWallpaper.Update(restoredBackground);
        _capture ??= StartWindowCapture();
        var frame = _capture.TryRead(Math.Max(_barrier, sourceNotBeforeTicks), waitForFrame, cancellation, reusePixels);
        if(frame is not null && _liveWallpaper!=0 && _liveSurface!.LastSourceQpcTicks>0)
            frame=frame with{SourceQpcTicks=Math.Min(frame.SourceQpcTicks,_liveSurface.LastSourceQpcTicks)};
        if (frame is not null && _backgroundComposer is not null && _backgroundPixels is { } background)
        {
            var traced = TraceBackground(frame, background);
            _backgroundComposer.Apply(frame.Pixels, background);
            if (traced) TracePixelStatistics(frame, "AfterComposition");
        }
        return UpdateExclusions() ? null : frame;
    }

    private bool TraceBackground(CapturePixels frame, byte[] background)
    {
        if (_trace is null || Stopwatch.GetTimestamp() < _nextBackgroundTraceTicks) return false;
        _nextBackgroundTraceTicks = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
        TracePixelStatistics(new(frame.Width, frame.Height, background), _liveWallpaper != 0 ? "LivePixels"
            : _wallpaper.Pixels is null ? "SystemColorFallback" : _wallpaper.SourceKind);
        TracePixelStatistics(frame, "CarrierBeforeComposition");
        return true;
    }

    private void TracePixelStatistics(CapturePixels frame, string reason)
    {
        var diagnostics = new NativeCaptureDiagnostics();
        diagnostics.ObservePixels(frame, 0);
        foreach (var sample in diagnostics.Drain())
            _trace?.Invoke(sample with { Stage = "DesktopBackground", Reason = reason });
    }
    internal void ReleaseReadbackBuffers() => _capture?.ReleaseReadbackBuffers();
    bool IDesktopSceneCapture.UpdateExclusions() => UpdateExclusions();
    CapturePixels? IDesktopSceneCapture.TryRead(TimeSpan wait, CancellationToken cancellation, long notBefore, bool reuse)
        => TryRead(wait, cancellation, notBefore, reuse);
    void IDesktopSceneCapture.ReleaseReadbackBuffers() => ReleaseReadbackBuffers();

    public void Dispose()
    {
        _capture?.Dispose(); _capture = null;
        _liveSurface?.Dispose(); _liveSurface = null;
        _livePixels = null;
        foreach (var thumbnail in _thumbnails) DwmUnregisterThumbnail(thumbnail);
        _thumbnails.Clear();
        _nativeWallpaper?.Dispose(); _nativeWallpaper = null;
        if(_carrier!=0) {DestroyWindow(_carrier);_carrier=0;}
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
    [DllImport("user32.dll", SetLastError=true)] private static extern bool SetWindowPos(nint hwnd,nint after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll", SetLastError=true)] private static extern nint SetWindowLongPtrW(nint hwnd,int index,nint value);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint hwnd,int index);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd,uint command);
    [DllImport("user32.dll")] private static extern nint GetParent(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsChild(nint parent, nint child);
    [DllImport("user32.dll",SetLastError=true)] private static extern bool SetLayeredWindowAttributes(nint hwnd,uint color,byte alpha,uint flags);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowProc callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint hwnd,EnumWindowProc callback,nint parameter);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetClassNameW(nint hwnd,System.Text.StringBuilder name,int count);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern unsafe int GetClassNameW(nint hwnd, char* name, int count);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
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
