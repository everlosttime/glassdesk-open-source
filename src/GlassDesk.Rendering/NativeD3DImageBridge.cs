using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace GlassDesk.Rendering;

public sealed record NativeRenderStatistics(ulong Generation, ulong SubmittedFrames, ulong AcquiredFrames,
    ulong DroppedFrames, ulong CpuReadbacks, ulong OutputTextureBytes, uint Width, uint Height,
    uint LeasedSlots, uint PendingSlots, string AdapterLuid);

public sealed record StaticGlassMaterial(float DipWidth, float DipHeight, float Strength, float Clarity,
    float TintOpacity, bool Frosted, bool Dark);

/// <summary>
/// Opt-in GPU transport for calibration and static wallpaper prefiltering.
/// Normal updates never read pixels. D3DImage's front-buffer copy still exists.
/// </summary>
public sealed class NativeD3DImageBridge : DispatcherObject, IDisposable
{
    private readonly EngineHandle _engine;
    private FrameLease? _displayed;
    private bool _disposed, _suspended, _rebindRequired;
    private NativeRenderStatistics? _lastStats;
    public D3DImage Image { get; } = new();
    public bool IsFallbackRequired { get; private set; }
    public int RecoveryAttempts { get; private set; }
    public ulong SurfaceUpdates { get; private set; }
    public string? LastError { get; private set; }
    public ulong WallpaperUploads { get; private set; }

    public void UploadWallpaper(byte[] bgra, int width, int height)
    {
        VerifyAccess(); ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(bgra);
        if (width <= 0 || height <= 0 || bgra.LongLength != (long)width * height * 4)
            throw new ArgumentException("Wallpaper must contain a complete BGRA frame.");
        var pin = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            var bitmap = new NativeMethods.Bitmap { Size = 32, Version = 1, Width = (uint)width,
                Height = (uint)height, Stride = checked((uint)width * 4), Pixels = pin.AddrOfPinnedObject() };
            Marshal.ThrowExceptionForHR(NativeMethods.UploadBitmap(_engine, ref bitmap));
            WallpaperUploads++;
        }
        finally { pin.Free(); }
    }

    public bool TryRenderMaterial(StaticGlassMaterial material) => TryRenderCore(() =>
    {
        var parameters = new NativeMethods.Material { Size = 48, Version = 1,
            DipWidth = material.DipWidth, DipHeight = material.DipHeight,
            Strength = material.Strength, Clarity = material.Clarity, TintOpacity = material.TintOpacity,
            Kind = material.Frosted ? 1u : 0u, Dark = material.Dark ? 1u : 0u };
        return NativeMethods.RenderMaterial(_engine, ref parameters);
    });

    // Poll a submitted static frame without filtering/uploading unchanged wallpaper again.
    public bool TryPresent() => TryRenderCore(() => 0);

    public NativeD3DImageBridge(nint window, int width, int height)
    {
        VerifyAccess();
        if (!Environment.Is64BitProcess || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            throw new PlatformNotSupportedException("Native rendering requires Windows 10 2004+ x64.");
        var info = new NativeMethods.CreateInfo
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.CreateInfo>(), Version = 1,
            Width = checked((uint)width), Height = checked((uint)height), Window = window
        };
        var hr = NativeMethods.Create(ref info, out _engine);
        if (hr < 0) { _engine.Dispose(); Marshal.ThrowExceptionForHR(hr); }
        Image.IsFrontBufferAvailableChanged += OnFrontBufferAvailabilityChanged;
    }

    private void OnFrontBufferAvailabilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    { if (Image.IsFrontBufferAvailable) _rebindRequired = true; }

    public NativeRenderStatistics Statistics
    {
        get
        {
            VerifyAccess();
            if (_engine.IsClosed && _lastStats is not null) return _lastStats;
            var stats = new NativeMethods.Stats { Size = (uint)Marshal.SizeOf<NativeMethods.Stats>(), Version = 1 };
            Marshal.ThrowExceptionForHR(NativeMethods.GetStats(_engine, ref stats));
            return _lastStats = new(stats.Generation, stats.Submitted, stats.Acquired, stats.Dropped,
                stats.Readbacks, stats.OutputBytes, stats.Width, stats.Height, stats.Leased, stats.Pending,
                $"{unchecked((uint)stats.AdapterHigh):X8}:{stats.AdapterLow:X8}");
        }
    }

    public bool TryRender(double seconds, bool animated)
    {
        if (!double.IsFinite(seconds) || seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        return TryRenderCore(() =>
        {
            var scene = new NativeMethods.Scene
            {
                Size = (uint)Marshal.SizeOf<NativeMethods.Scene>(), Version = 1,
                Seconds = seconds, Opacity = 1, Radius = 32, Pattern = animated ? 1u : 0u
            };
            return NativeMethods.Render(_engine, ref scene);
        });
    }

    private bool TryRenderCore(Func<int> submit)
    {
        VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Keep WPF's interop bitmap bound while unavailable: detaching it here
        // would discard its notification channel for front-buffer restoration.
        if (_suspended || IsFallbackRequired || !Image.IsFrontBufferAvailable) return false;
        if (!Image.TryLock(new Duration(TimeSpan.Zero)))
        {
            // WPF LockImpl increments its lock count even when its timed wait
            // fails. Balance it without writing or swapping any GPU surface.
            Image.Unlock();
            return false;
        }
        var recover = false;
        try
        {
            Marshal.ThrowExceptionForHR(submit());
            var frame = new NativeMethods.Frame { Size = (uint)Marshal.SizeOf<NativeMethods.Frame>(), Version = 1 };
            var hr = NativeMethods.Acquire(_engine, ref frame);
            Marshal.ThrowExceptionForHR(hr);
            if (hr == 1)
            {
                if (!_rebindRequired || _displayed is null) return false;
                var retained = _displayed.Frame;
                Image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, retained.Surface, enableSoftwareFallback: false);
                Image.AddDirtyRect(new Int32Rect(0,0,(int)retained.Width,(int)retained.Height));
                _rebindRequired = false; SurfaceUpdates++; return true;
            }
            var next = new FrameLease(_engine, frame);
            try { Image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, frame.Surface, enableSoftwareFallback: false); }
            catch { next.Dispose(); throw; }
            var previous = _displayed;
            _displayed = next;
            // TryLock completed the previous WPF copy; SetBackBuffer detached it.
            previous?.Dispose();
            Image.AddDirtyRect(new Int32Rect(0, 0, checked((int)frame.Width), checked((int)frame.Height)));
            SurfaceUpdates++;
            _rebindRequired = false;
            return true;
        }
        catch (COMException error) { LastError = $"0x{error.HResult:X8}: {error.Message}"; recover = true; }
        finally { Image.Unlock(); }
        if (recover) TryRecover();
        return false;
    }

    public void Resize(int width, int height)
    {
        VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (IsFallbackRequired) return;
        Detach();
        Marshal.ThrowExceptionForHR(NativeMethods.Resize(_engine, (uint)width, (uint)height));
    }

    public bool TryRecover()
    {
        VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsFallbackRequired) return false;
        Detach();
        if (RecoveryAttempts++ == 0)
        {
            var hr = NativeMethods.Recreate(_engine);
            if (hr >= 0) return true;
            LastError = $"Native device recreation failed: 0x{hr:X8}.";
        }
        IsFallbackRequired = true;
        _lastStats = Statistics;
        _engine.Dispose();
        return false;
    }

    public void Suspend() { VerifyAccess(); ObjectDisposedException.ThrowIf(_disposed, this); _suspended = true; Detach(); }
    public void Resume() { VerifyAccess(); ObjectDisposedException.ThrowIf(_disposed, this); _suspended = false; }
    private void Detach()
    {
        // Blocking Lock is restricted to teardown/resize/recovery, never a frame update.
        Image.Lock();
        try
        {
            Image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, 0);
            _displayed?.Dispose();
            _displayed = null;
            _rebindRequired = false;
        }
        finally { Image.Unlock(); }
    }
    public void Dispose()
    {
        VerifyAccess();
        if (_disposed) return;
        Image.IsFrontBufferAvailableChanged -= OnFrontBufferAvailabilityChanged;
        Detach();
        _lastStats = Statistics;
        _engine.Dispose();
        _disposed = true;
    }
}
