using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace GlassDesk.Launcher;

public partial class LauncherWindow
{
    private readonly bool _experimentalHighRefreshCapture = Environment.GetEnvironmentVariable("GLASSDESK_EXPERIMENTAL_120HZ") == "1";
    private bool UseHighRefreshCapture => _settings.HighRefreshAnimationsEnabled || _experimentalHighRefreshCapture;
    private readonly string? _animationTraceDirectory = Environment.GetEnvironmentVariable("GLASSDESK_ANIMATION_TRACE_DIR");
    private AnimationTrace? _animationTrace;
    private long _showRequestedAt;
    private double _lastRevealPreparationMilliseconds;
    private List<PreparationTiming>? _preparationTimings;
    private long _nextAnimationRequestId;
    private long _animationRequestId;
    private long _animationRequestStartedAt;
    private bool _requestReversesAnimation;
    private FrameWorkTiming _frameWorkTiming;
    private readonly record struct PreparationTiming(string stage, double milliseconds);
    private readonly record struct FrameWorkTiming(double geometryMilliseconds, double glassMilliseconds,
        double shadowMilliseconds, double contentMilliseconds, bool frameCommitted = true);

    private void StartAnimationRequest(bool opening)
    {
        _animationRequestId = ++_nextAnimationRequestId;
        _animationRequestStartedAt = Stopwatch.GetTimestamp();
        _requestReversesAnimation = _transformActive;
        _showRequestedAt = opening ? _animationRequestStartedAt : 0;
        _lastRevealPreparationMilliseconds = 0;
        _preparationTimings = string.IsNullOrWhiteSpace(_animationTraceDirectory) ? null : new(8);
    }

    private void MarkPreparationTiming(string stage, ref long started)
    {
        var now = Stopwatch.GetTimestamp();
        _preparationTimings?.Add(new(stage, Stopwatch.GetElapsedTime(started, now).TotalMilliseconds));
        started = now;
    }

    private void BeginAnimationTrace(bool opening)
    {
        if (string.IsNullOrWhiteSpace(_animationTraceDirectory)) return;
        _animationTrace = new AnimationTrace(_animationTraceDirectory, opening, UseHighRefreshCapture,
            opening && _showRequestedAt != 0 ? Stopwatch.GetElapsedTime(_showRequestedAt).TotalMilliseconds : 0,
            opening ? _lastRevealPreparationMilliseconds : 0,
            _preparationTimings?.ToArray() ?? [], _animationRequestId, _animationRequestStartedAt,
            Handle.ToInt64(), _requestReversesAnimation);
    }

    private void FinishAnimationTrace(string outcome = "cancelled")
    {
        var trace = _animationTrace;
        _animationTrace = null;
        trace?.Finish(GlassSurface.InspectPerformance(), outcome);
    }

    private sealed class AnimationTrace(string directory, bool opening, bool highRefresh,
        double requestPreparationMilliseconds, double revealPreparationMilliseconds, PreparationTiming[] preparationStages,
        long requestId, long requestStarted, long hwnd, bool reversesAnimation)
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
        private readonly DateTimeOffset _timestamp = DateTimeOffset.UtcNow;
        private readonly long _started = Stopwatch.GetTimestamp();
        private readonly int _uiThread = Environment.CurrentManagedThreadId;
        private readonly List<FrameTiming> _samples = new(128);
        private long _previous;
        private bool _truncated;
        private readonly record struct FrameTiming(double sinceStartMilliseconds, double gapMilliseconds, double updateMilliseconds,
            long qpc, double geometryMilliseconds, double glassMilliseconds, double shadowMilliseconds, double contentMilliseconds, bool frameCommitted);

        private static Dictionary<string, object> SelectNumericMetrics(JsonElement value)
        {
            var selected = new Dictionary<string, object>();
            foreach (var property in value.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Object)
                    selected[property.Name] = SelectNumericMetrics(property.Value);
                else if (property.Value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                    selected[property.Name] = property.Value.Clone();
            }
            return selected;
        }

        internal void Record(long start, FrameWorkTiming work)
        {
            if (_samples.Count >= 4096) { _truncated = true; return; }
            _samples.Add(new(Stopwatch.GetElapsedTime(_started, start).TotalMilliseconds,
                _previous == 0 ? 0 : Stopwatch.GetElapsedTime(_previous, start).TotalMilliseconds,
                Stopwatch.GetElapsedTime(start).TotalMilliseconds, start,
                work.geometryMilliseconds, work.glassMilliseconds, work.shadowMilliseconds, work.contentMilliseconds, work.frameCommitted));
            _previous = start;
        }

        internal void Finish(object surfaceMetrics, string outcome, double finalizationMilliseconds = 0)
        {
            // Copy a bounded list of numbers on the Dispatcher. Formatting, directory
            // creation and disk I/O must never extend the animation's last frame.
            var samples = _samples.ToArray();
            var elapsed = Stopwatch.GetElapsedTime(_started).TotalMilliseconds;
            var ended = Stopwatch.GetTimestamp();
            _ = Task.Run(() =>
            {
                try
                {
                    var gaps = samples.Where(s => s.gapMilliseconds > 0).Select(s => s.gapMilliseconds).ToArray();
                    var payload = new
                    {
                        schemaVersion = 2, timestampUtc = _timestamp, direction = opening ? "open" : "close",
                        requestId, requestQpc = requestStarted, animationQpc = _started, endQpc = ended,
                        qpcFrequency = Stopwatch.Frequency, processId = Environment.ProcessId, hwnd,
                        reversesAnimation, outcome, finalizationMilliseconds, displayEvidence = "unmeasured",
                        firstCallbackMilliseconds = samples.Length == 0 ? (double?)null
                            : Stopwatch.GetElapsedTime(requestStarted, samples[0].qpc).TotalMilliseconds,
                        experimentalHighRefreshCapture = highRefresh,
                        note = "UI rendering callbacks and CPU stage durations; not displayed GPU FPS. No screenshots or application titles.",
                        uiThreadId = _uiThread, writerThreadId = Environment.CurrentManagedThreadId,
                        requestPreparationMilliseconds, revealPreparationMilliseconds, preparationStages,
                        animationMilliseconds = elapsed, frameSamples = samples, samplesTruncated = _truncated,
                        averageGapMilliseconds = gaps.Length == 0 ? 0 : gaps.Average(),
                        maximumGapMilliseconds = gaps.Length == 0 ? 0 : gaps.Max(),
                        gapsOver50Milliseconds = gaps.Count(gap => gap > 50),
                        // Capture failures can contain paths or window details. Keep only
                        // numeric/boolean snapshots, never arbitrary diagnostic strings.
                        surfaceMetrics = SelectNumericMetrics(JsonSerializer.SerializeToElement(surfaceMetrics)),
                    };
                    Directory.CreateDirectory(directory);
                    var path = Path.Combine(directory, $"{_timestamp:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}.json");
                    var pending = path + ".tmp";
                    File.WriteAllText(pending, JsonSerializer.Serialize(payload, JsonOptions));
                    File.Move(pending, path);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
                { Trace.WriteLine("Animation timing log unavailable: " + error.GetType().Name); }
            });
        }
    }
}
