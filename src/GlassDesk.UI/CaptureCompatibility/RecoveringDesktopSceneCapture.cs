using System.Diagnostics;

/// <summary>One-way recovery: stop an unsupported/crashed helper before starting documented capture.</summary>
internal sealed class RecoveringDesktopSceneCapture : IDesktopSceneCapture
{
    private IDesktopSceneCapture? _source;
    private readonly ScreenRect _region;
    private readonly Action<CaptureTrace>? _trace;
    private bool _fallback;
    private long _initialDeadline;
    private readonly Func<IDesktopSceneCapture> _createFallback;
    private readonly Func<IDesktopSceneCapture> _createPrimary;
    private readonly Func<long> _clock;
    private long _lastBarrier;
    private long _lastReadAt;
    private bool _disposed;
    private long _reprobeAt;
    public bool AllowsStaticContent => _source?.AllowsStaticContent == true;
    public bool SupportsSampling => _source?.SupportsSampling == true;
    public long SceneRevision => _source?.SceneRevision ?? 0;
    internal nint CarrierHandle => _source?.CarrierHandle ?? 0;
    nint IDesktopSceneCapture.CarrierHandle => CarrierHandle;

    internal RecoveringDesktopSceneCapture(ScreenRect region, Action<CaptureTrace>? trace = null, string? executable = null)
        : this(region, () => new IsolatedDesktopSceneCapture(region, trace, executable),
            () => new RecoveringDesktopSceneCapture(region,
                () => new IsolatedDesktopSceneCapture(region, true, trace, executable),
                () => new IsolatedDesktopSceneCapture(region, GlassDesk.UI.Controls.GlassBackdropSource.Wallpaper, trace, executable),
                Stopwatch.GetTimestamp, trace), Stopwatch.GetTimestamp, trace) { }

    internal RecoveringDesktopSceneCapture(ScreenRect region, Func<IDesktopSceneCapture> createNative,
        Func<IDesktopSceneCapture> createFallback, Func<long> clock)
        : this(region, createNative, createFallback, clock, null) { }

    private RecoveringDesktopSceneCapture(ScreenRect region, Func<IDesktopSceneCapture> createNative,
        Func<IDesktopSceneCapture> createFallback, Func<long> clock, Action<CaptureTrace>? trace)
    {
        _region = region; _createPrimary = createNative; _createFallback = createFallback; _clock = clock; _trace = trace;
        try
        {
            _source = createNative();
            _initialDeadline = _clock() + Stopwatch.Frequency * 3;
        }
        catch (Exception error) { Recover(error); }
    }

    private void Recover(Exception error)
    {
        var retired = _source; _source = null;
        try { retired?.Dispose(); }
        catch { _disposed = true; throw; } // Never overlap a resource owner whose exit is unconfirmed.
        _fallback = true;
        _reprobeAt = _clock() + Stopwatch.Frequency * 30;
        Trace.WriteLine("Native capture helper failed; documented compatibility background: " + error.Message);
        _trace?.Invoke(new("BackdropProvider", "DocumentedFallback", Stopwatch.GetTimestamp()));
        _source = _createFallback();
        _initialDeadline = _clock() + Stopwatch.Frequency * 3;
    }
    internal bool UpdateExclusions()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_fallback && _clock() >= _reprobeAt)
        {
            var retired = _source; _source = null;
            try { retired?.Dispose(); }
            catch { _disposed = true; throw; }
            _fallback = false;
            try
            {
                _source = _createPrimary();
                _initialDeadline = _clock() + Stopwatch.Frequency * 3;
                _trace?.Invoke(new("BackdropProvider", "EnhancedReprobe", Stopwatch.GetTimestamp()));
            }
            catch (Exception error) { Recover(error); }
            return true;
        }
        try
        {
            var changed = _source!.UpdateExclusions();
            if (changed && _initialDeadline == 0) _initialDeadline = _clock() + Stopwatch.Frequency * 3;
            return changed;
        }
        catch (Exception error) when (!_fallback)
        { Recover(error); return true; }
    }
    bool IDesktopSceneCapture.UpdateExclusions() => UpdateExclusions();
    internal CapturePixels? TryRead(TimeSpan waitForFrame = default, CancellationToken cancellation = default,
        long sourceNotBeforeTicks = 0, bool reusePixels = false)
        => ReadFrame(source=>source.TryRead(waitForFrame,cancellation,sourceNotBeforeTicks,reusePixels),
            cancellation,sourceNotBeforeTicks);
    internal CapturePixels? TryReadSampled(PixelBounds crop,int width,int height,byte[] target,
        TimeSpan waitForFrame=default,CancellationToken cancellation=default,long sourceNotBeforeTicks=0)
        => ReadFrame(source=>source.TryReadSampled(crop,width,height,target,waitForFrame,cancellation,sourceNotBeforeTicks),
            cancellation,sourceNotBeforeTicks);
    private CapturePixels? ReadFrame(Func<IDesktopSceneCapture,CapturePixels?> read,CancellationToken cancellation,long sourceNotBeforeTicks)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellation.ThrowIfCancellationRequested();
        try
        {
            // A long hidden interval does not count as an active capture stall.
            var now = _clock();
            var resumed = _lastReadAt != 0 && now - _lastReadAt >= Stopwatch.Frequency * 3;
            _lastReadAt = now;
            if (sourceNotBeforeTicks > _lastBarrier)
            {
                _lastBarrier = sourceNotBeforeTicks;
                // Active scene churn must not indefinitely postpone failure detection.
                if (_initialDeadline == 0 || resumed) _initialDeadline = now + Stopwatch.Frequency * 3;
            }
            var frame = read(_source!);
            if (frame is not null) _initialDeadline = _fallback || AllowsStaticContent ? 0 : _clock() + Stopwatch.Frequency * 3;
            else if (!cancellation.IsCancellationRequested && _initialDeadline != 0 && _clock() > _initialDeadline)
                // A static documented thumbnail may emit just one frame. Only
                // require another after a scene/resume boundary. If that fresh
                // frame never arrives, let the feed recreate the failed session.
                throw new TimeoutException((_fallback ? "Documented capture" : "Native capture helper") + " stopped delivering frames.");
            return frame;
        }
        catch (Exception error) when (!_fallback && !cancellation.IsCancellationRequested)
        {
            Recover(error);
            return ReadFrame(read,cancellation,sourceNotBeforeTicks);
        }
    }
    CapturePixels? IDesktopSceneCapture.TryRead(TimeSpan wait, CancellationToken cancellation, long notBefore, bool reuse)
        => TryRead(wait, cancellation, notBefore, reuse);
    CapturePixels? IDesktopSceneCapture.TryReadSampled(PixelBounds crop,int width,int height,byte[] target,TimeSpan wait,
        CancellationToken cancellation,long notBefore) => TryReadSampled(crop,width,height,target,wait,cancellation,notBefore);
    internal void ReleaseReadbackBuffers()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try { _source!.ReleaseReadbackBuffers(); }
        catch (Exception error) when (!_fallback) { Recover(error); }
    }
    void IDesktopSceneCapture.ReleaseReadbackBuffers() => ReleaseReadbackBuffers();
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var source = _source; _source = null; source?.Dispose();
    }
}
