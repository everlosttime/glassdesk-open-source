namespace GlassDesk.Platform.Shell;

/// <summary>Event-based in normal operation; 1 Hz geometry fallback only when native hooks are unavailable.</summary>
public sealed class PerformanceFullscreenMonitor : IDisposable
{
    private readonly Action<PerformanceFullscreenObservation> _publish;
    private FullscreenWatcher? _watcher;
    private Timer? _fallback;
    private readonly Lock _sync = new();
    private bool _disposed;

    public PerformanceFullscreenMonitor(Action<PerformanceFullscreenObservation> publish)
    {
        _publish = publish ?? throw new ArgumentNullException(nameof(publish));
        _watcher = FullscreenWatcher.TryStart(onStateInvalidated: OnInvalidated, onPerformanceObservation: publish);
        if (_watcher is null) StartFallback();
    }

    public bool UsesFallback { get { lock (_sync) return _fallback is not null; } }

    public void Refresh()
    {
        lock (_sync) { if (_disposed) return; }
        if (_watcher?.IsAvailable == true) _watcher.Refresh();
        else { StartFallback(); PublishFallback(null); }
    }

    private void OnInvalidated()
    {
        // Initial observation is published before TryStart returns; null is not runtime failure.
        if (_watcher is { IsAvailable: false }) StartFallback();
    }

    private void StartFallback()
    {
        lock (_sync)
        {
            if (_disposed || _fallback is not null) return;
            _fallback = new Timer(PublishFallback, null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        }
    }

    private void PublishFallback(object? state)
    {
        lock (_sync) { if (_disposed) return; }
        try { _publish(FullscreenDetector.ReadPerformanceObservation()); }
        catch (Exception) { /* Keep the last safe state; manual sleep remains available. */ }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _fallback?.Dispose();
            _fallback = null;
        }
        _watcher?.Dispose();
        _watcher = null;
    }
}
