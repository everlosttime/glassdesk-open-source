using System.Diagnostics;
using System.Windows.Media;

namespace GlassDesk.Dock;

/// <summary>Run hover motion once per composed frame, only while a target is changing.</summary>
internal sealed class DockRenderLoop(Func<double, bool> advance)
{
    private bool _active;
    private long _timestamp;
    private TimeSpan _lastRenderingTime = TimeSpan.MinValue;

    internal void Start()
    {
        if (_active) return;
        _active = true;
        _timestamp = Stopwatch.GetTimestamp();
        _lastRenderingTime = TimeSpan.MinValue;
        CompositionTarget.Rendering += OnRendering;
    }

    internal void Stop()
    {
        if (!_active) return;
        CompositionTarget.Rendering -= OnRendering;
        _active = false;
        _timestamp = 0;
        _lastRenderingTime = TimeSpan.MinValue;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (e is RenderingEventArgs rendering)
        {
            if (rendering.RenderingTime == _lastRenderingTime) return;
            _lastRenderingTime = rendering.RenderingTime;
        }
        var now = Stopwatch.GetTimestamp();
        var deltaSeconds = Math.Clamp(Stopwatch.GetElapsedTime(_timestamp, now).TotalSeconds, 0, .1);
        _timestamp = now;
        if (advance(deltaSeconds)) Stop();
    }
}
