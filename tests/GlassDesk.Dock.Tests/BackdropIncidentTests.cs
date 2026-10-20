using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace GlassDesk.Dock.Tests;

public sealed class BackdropIncidentTests
{
    [Fact]
    public async Task FailureAutomaticallyPersistsOnlyTheBoundedBeforeAndAfterMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), "GlassDesk-incidents-" + Guid.NewGuid().ToString("N"));
        var now = Stopwatch.GetTimestamp();
        using var recorder = Create(root, () => now);
        try
        {
            Observe(recorder, new("Geometry", "Old", now - Stopwatch.Frequency * 31));
            Observe(recorder, new("Geometry", "Recent", now - Stopwatch.Frequency * 10));
            Observe(recorder, new("CaptureFailure", "TimeoutException", now));
            Invoke(recorder, "FlushReady");
            await Writes(recorder);
            Assert.Empty(Directory.GetFiles(root, "*.json"));
            now += Stopwatch.Frequency * 2;
            Observe(recorder, new("Recovery", "ResourcesRetired", now));
            now += Stopwatch.Frequency;
            Invoke(recorder, "FlushReady");
            await Writes(recorder);
            var file = Assert.Single(Directory.GetFiles(root, "*.json"));
            using var report = JsonDocument.Parse(File.ReadAllText(file));
            Assert.False(report.RootElement.GetProperty("screenPixelsIncluded").GetBoolean());
            var events = report.RootElement.GetProperty("events").EnumerateArray().ToArray();
            Assert.DoesNotContain(events, e => e.GetProperty("Reason").GetString() == "Old");
            Assert.Contains(events, e => e.GetProperty("Reason").GetString() == "Recent");
            Assert.Contains(events, e => e.GetProperty("Reason").GetString() == "TimeoutException");
            Assert.Contains(events, e => e.GetProperty("Reason").GetString() == "ResourcesRetired");
        }
        finally { recorder.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task PerFrameTrafficDoesNotWriteIncidentsOrEvictTheirCause()
    {
        var root = Path.Combine(Path.GetTempPath(), "GlassDesk-incidents-" + Guid.NewGuid().ToString("N"));
        var now = Stopwatch.GetTimestamp();
        using var recorder = Create(root, () => now);
        try
        {
            Observe(recorder, new("Geometry", "Changed", now));
            for (var i = 0; i < 10000; i++) Observe(recorder, new("CaptureCost", "ResizeCompleted", now));
            Invoke(recorder, "FlushReady"); await Writes(recorder);
            Assert.Empty(Directory.GetFiles(root, "*.json"));
            Observe(recorder, new("MaterialLoss", "SceneUnknown", now));
            now += Stopwatch.Frequency * 3;
            Invoke(recorder, "FlushReady"); await Writes(recorder);
            var json = File.ReadAllText(Assert.Single(Directory.GetFiles(root, "*.json")));
            Assert.Contains("Changed", json);
            Assert.DoesNotContain("ResizeCompleted", json);
        }
        finally { recorder.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task IncidentRotationRetainsAtMostTenFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "GlassDesk-incidents-" + Guid.NewGuid().ToString("N"));
        var now = Stopwatch.GetTimestamp();
        using var recorder = Create(root, () => now);
        try
        {
            for (var i = 0; i < 12; i++)
            {
                Observe(recorder, new("CaptureFailure", "TimeoutException", now, Generation: i));
                now += Stopwatch.Frequency * 4;
                Invoke(recorder, "FlushReady"); await Writes(recorder);
            }
            var files = Directory.GetFiles(root, "*.json");
            Assert.Equal(10, files.Length);
            Assert.True(files.Sum(f => new FileInfo(f).Length) <= 20 * 1024 * 1024);
        }
        finally { recorder.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static IDisposable Create(string root, Func<long> clock)
    {
        var type = typeof(CaptureTraceLog).Assembly.GetType("GlassDesk.UI.BackdropIncidentRecorder");
        Assert.NotNull(type);
        return (IDisposable)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic,
            null, [root, clock, TimeSpan.FromSeconds(3)], null)!;
    }
    private static object? Invoke(IDisposable recorder, string method, params object[] args)
        => recorder.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(recorder, args);
    private static void Observe(IDisposable recorder, CaptureTrace trace) => Invoke(recorder, "Observe", trace);
    private static Task Writes(IDisposable recorder) => (Task)Invoke(recorder, "WaitForWritesAsync")!;
}
