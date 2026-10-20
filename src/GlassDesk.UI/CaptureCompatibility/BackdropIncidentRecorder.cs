using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Channels;

namespace GlassDesk.UI;

/// <summary>Bounded state metadata only. Disk work never runs on the rendering or capture thread.</summary>
internal sealed class BackdropIncidentRecorder : IDisposable
{
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly Func<long> _clock;
    private readonly long _postTicks;
    private readonly Queue<CaptureTrace> _recent = new();
    private CaptureTrace? _lastTrace;
    private readonly Channel<Incident> _writes = Channel.CreateBounded<Incident>(new BoundedChannelOptions(2)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly System.Threading.Timer _timer;
    private readonly Task _writer;
    private Task _lastWrite = Task.CompletedTask;
    private long _incidentAt, _incidentEnd;
    private bool _pending, _disposed;
    private sealed record Incident(long AtTicks, CaptureTrace[] Events, TaskCompletionSource Completion);

    internal BackdropIncidentRecorder(string directory, Func<long> clock, TimeSpan postWindow)
    {
        _directory = Path.GetFullPath(directory); _clock = clock;
        _postTicks = (long)(postWindow.TotalSeconds * Stopwatch.Frequency);
        Directory.CreateDirectory(_directory);
        _writer = Task.Run(WriteLoop);
        _timer = new System.Threading.Timer(_ => FlushReady(), null, 250, 250);
    }

    internal void Observe(CaptureTrace trace)
    {
        if (!CaptureTraceLog.IsStateEvent(trace)) return;
        lock (_gate)
        {
            if (_disposed) return;
            var now = _clock();
            CloseReady(now);
            Trim(now);
            // Repeated empty/expired reads are useful counters, not new state.
            if (_lastTrace is { } previous
                && previous.Stage == trace.Stage && previous.Reason == trace.Reason
                && previous.Generation == trace.Generation && previous.SurfaceId == trace.SurfaceId
                && trace.AtTicks - previous.AtTicks < Stopwatch.Frequency / 4) return;
            if (_recent.Count == 1024) _recent.Dequeue();
            _recent.Enqueue(trace);
            _lastTrace = trace;
            if (!_pending && IsTrigger(trace))
            { _pending = true; _incidentAt = now; _incidentEnd = now + _postTicks; }
        }
    }

    private static bool IsTrigger(CaptureTrace trace) => trace.Stage is "CaptureFailure" or "MaterialLoss" or "VisualSwap"
        || trace.Stage == "UIScene" && trace.Reason == "UnknownAfterRetry"
        || trace.Stage == "Rendering" && trace.Reason == "BasicBackgroundRequired"
        || trace.Stage == "NativePixels" && (trace.TransparentFraction > 0 || trace.NearBlackFraction >= .95);

    private void Trim(long now)
    {
        // While an incident collects its post-window, preserve its full pre-window.
        var earliest = (_pending ? _incidentAt : now) - Stopwatch.Frequency * 30;
        while (_recent.TryPeek(out var trace) && trace.AtTicks < earliest) _recent.Dequeue();
    }

    internal void FlushReady()
    {
        lock (_gate) { if (!_disposed) CloseReady(_clock()); }
    }

    private void CloseReady(long now)
    {
        if (!_pending || now < _incidentEnd) return;
        EnqueueIncident();
    }

    private void EnqueueIncident()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var start = _incidentAt - Stopwatch.Frequency * 30;
        var incident = new Incident(_incidentAt, _recent.Where(e => e.AtTicks >= start && e.AtTicks <= _incidentEnd).ToArray(), completion);
        if (_writes.Writer.TryWrite(incident)) _lastWrite = completion.Task;
        else completion.SetResult(); // Slow/unavailable disk cannot grow a queue or stall glass.
        _pending = false;
    }

    internal Task WaitForWritesAsync() { lock (_gate) return _lastWrite; }

    private async Task WriteLoop()
    {
        await foreach (var incident in _writes.Reader.ReadAllAsync())
        {
            try
            {
                var name = "dock-backdrop-" + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + Guid.NewGuid().ToString("N") + ".json";
                var path = Path.Combine(_directory, name);
                var report = new
                {
                    timestamp = DateTimeOffset.Now, processId = Environment.ProcessId,
                    applicationVersion = FileVersionInfo.GetVersionInfo(typeof(BackdropIncidentRecorder).Assembly.Location).FileVersion,
                    qpcFrequency = Stopwatch.Frequency, incidentAtTicks = incident.AtTicks,
                    preWindowSeconds = 30, postWindowSeconds = _postTicks / (double)Stopwatch.Frequency,
                    screenPixelsIncluded = false, windowTitlesIncluded = false,
                    note = "State and sampled pixel statistics only. A dark scene is valid content. These events do not measure displayed FPS.",
                    events = incident.Events
                };
                await File.WriteAllBytesAsync(path + ".tmp", JsonSerializer.SerializeToUtf8Bytes(report));
                File.Move(path + ".tmp", path);
                Rotate();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { Trace.WriteLine("Backdrop incident write unavailable: " + error.GetType().Name); }
            finally { incident.Completion.TrySetResult(); }
        }
    }

    private void Rotate()
    {
        var files = new DirectoryInfo(_directory).GetFiles("dock-backdrop-*.json")
            .OrderByDescending(f => f.Name, StringComparer.Ordinal).ToArray();
        long retained = 0;
        for (var i = 0; i < files.Length; i++)
        {
            retained += files[i].Length;
            if (i >= 10 || retained > 20 * 1024 * 1024) files[i].Delete();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; _timer.Dispose();
            if (_pending) EnqueueIncident();
            _writes.Writer.TryComplete();
        }
        // The writer owns only metadata; shutdown never waits on the Dispatcher.
    }
}
