using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using GlassDesk.Launcher;

internal static partial class Program
{
    private static bool WaitForManualAcceptance(string evidence, TimeSpan timeout, Func<string, bool>? probe = null)
    {
        var marker = Path.Combine(evidence, "stop-manual");
        var readMarker = probe ?? File.Exists;
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        // Manual and automated acceptance both pump only in-memory state on the
        // UI dispatcher. Storage latency must not become drag/input latency.
        var watcher = Task.Run(async () =>
        {
            var checks = 0;
            double maximumMilliseconds = 0;
            var found = false;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var started = Stopwatch.GetTimestamp();
                    found = readMarker(marker);
                    maximumMilliseconds = Math.Max(maximumMilliseconds, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    checks++;
                    if (found) return true;
                    await Task.Delay(100, token);
                }
                return false;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return false; }
            finally
            {
                File.WriteAllText(Path.Combine(evidence, "manual-stop-watch.json"), JsonSerializer.Serialize(new
                { checks, maximumProbeMilliseconds = maximumMilliseconds, markerObserved = found,
                    note = "Marker reads run on a background worker; UI pump checks task completion only." }));
            }
        });
        try { return PumpUntil(() => watcher.IsCompleted, timeout) && watcher.GetAwaiter().GetResult(); }
        finally { cancellation.Cancel(); }
    }

    private static void VerifyManualStopResponsiveness(LauncherWindow window, string evidence)
    {
        var stop = Path.Combine(evidence, "stop-manual");
        var activeRead = 0;
        var reads = 0;
        var renderingDuringRead = 0;
        var inputDuringRead = 0;
        EventHandler render = (_, _) => { if (Volatile.Read(ref activeRead) != 0) renderingDuringRead++; };
        var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) => { if (Volatile.Read(ref activeRead) != 0) inputDuringRead++; };
        var writer = Task.Run(async () => { await Task.Delay(1000); await File.WriteAllTextAsync(stop, ""); });
        CompositionTarget.Rendering += render;
        timer.Start();
        bool stopped;
        try
        {
            stopped = WaitForManualAcceptance(evidence, TimeSpan.FromSeconds(4), path =>
            {
                // Model a delayed storage boundary, then read the actual marker.
                // The contract is that this read never prevents WPF from dispatching.
                Interlocked.Increment(ref reads);
                Interlocked.Exchange(ref activeRead, 1);
                try { Thread.Sleep(200); return File.Exists(path); }
                finally { Interlocked.Exchange(ref activeRead, 0); }
            });
        }
        finally { timer.Stop(); CompositionTarget.Rendering -= render; writer.GetAwaiter().GetResult(); }
        Check("结束标记读取延迟时输入和渲染仍可运行", "regression/manual-stop",
            renderingDuringRead > 0 && inputDuringRead > 0, "存储读取期间输入与渲染均前进",
            $"rendering={renderingDuringRead}, input={inputDuringRead}, reads={reads}");
        Check("实际结束标记关闭手动验收等待", "regression/manual-stop", stopped,
            "真实文件触发结束", stopped.ToString());
        File.WriteAllText(Path.Combine(evidence, "manual-stop-responsiveness.json"), JsonSerializer.Serialize(new
        { reads, renderingDuringRead, inputDuringRead, stopped, storageDelayMilliseconds = 200, note = "Delayed real-file boundary; not displayed FPS." }));
    }
}
