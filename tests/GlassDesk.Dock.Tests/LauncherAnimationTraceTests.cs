using System.IO;
using System.Reflection;
using System.Text.Json;
using GlassDesk.Launcher;
using GlassDesk.Platform.Displays;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock.Tests;

public sealed class LauncherAnimationTraceTests
{
    [Fact]
    public void ReversalsCloseEachTraceWithItsOwnDirectionAndRequest()
    {
        StaTest.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "GlassDesk-reverse-trace-" + Guid.NewGuid().ToString("N"));
            var previous = Environment.GetEnvironmentVariable("GLASSDESK_ANIMATION_TRACE_DIR");
            Environment.SetEnvironmentVariable("GLASSDESK_ANIMATION_TRACE_DIR", root);
            var window = new LauncherWindow();
            try
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var display = DisplayService.Primary()!;
                var source = new PixelRect(display.WorkArea.X + 60, display.WorkArea.Bottom - 84, 48, 48);
                var render = typeof(LauncherWindow).GetMethod("OnContainerTransformRendering", flags)!;
                window.ShowWithTransition(source);
                Thread.Sleep(10); render.Invoke(window, [null, EventArgs.Empty]);
                window.CloseWithTransition();
                Thread.Sleep(10); render.Invoke(window, [null, EventArgs.Empty]);
                window.ShowWithTransition(source);
                Thread.Sleep(10); render.Invoke(window, [null, EventArgs.Empty]);
                typeof(LauncherWindow).GetMethod("CompleteContainerTransform", flags)!.Invoke(window, [1d]);
                Assert.True(SpinWait.SpinUntil(() => Directory.Exists(root) && Directory.GetFiles(root, "*.json").Length == 3, 3000));
                var traces = Directory.GetFiles(root, "*.json").Select(p => JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(p)))
                    .OrderBy(t => t.GetProperty("requestId").GetInt64()).ToArray();
                Assert.Equal(new long[] { 1, 2, 3 }, traces.Select(t => t.GetProperty("requestId").GetInt64()));
                Assert.Equal(new[] { "open", "close", "open" }, traces.Select(t => t.GetProperty("direction").GetString()));
                Assert.Equal(new[] { "reversed", "reversed", "completed" }, traces.Select(t => t.GetProperty("outcome").GetString()));
                Assert.True(traces[1].GetProperty("requestQpc").GetInt64() > traces[0].GetProperty("requestQpc").GetInt64());
                Assert.True(traces[1].GetProperty("reversesAnimation").GetBoolean());
            }
            finally { window.Close(); Environment.SetEnvironmentVariable("GLASSDESK_ANIMATION_TRACE_DIR", previous); }
        });
    }

    [Fact]
    public void DiagnosticRunWritesFrameAndPreparationTimingsOffTheUiThread()
    {
        StaTest.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "GlassDesk-animation-trace-" + Guid.NewGuid().ToString("N"));
            var traceDirectory = Path.Combine(root, "timings");
            var previous = Environment.GetEnvironmentVariable("GLASSDESK_ANIMATION_TRACE_DIR");
            Environment.SetEnvironmentVariable("GLASSDESK_ANIMATION_TRACE_DIR", traceDirectory);
            var window = new LauncherWindow();
            var surface = (LiquidGlassSurface)window.FindName("GlassSurface");
            using var feed = new SharedBackdropFeed((_, cancellation) => cancellation.WaitHandle.WaitOne());
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var background = typeof(LiquidGlassSurface).GetField("_background", flags)!.GetValue(surface)!;
            var field = background.GetType().GetField("_compatible", flags)!;
            ((IDisposable)field.GetValue(background)!).Dispose(); field.SetValue(background, feed);
            var scene = new GlassBackdropScene(); surface.ReadBackdropScene = () => scene;
            surface.CaptureRevealBackdrop = (_, width, height) =>
            {
                Thread.Sleep(20);
                return Enumerable.Repeat((byte)80, width * height * 4).ToArray();
            };
            try
            {
                var display = DisplayService.Primary()!;
                window.ShowWithTransition(new PixelRect(display.WorkArea.X + 60, display.WorkArea.Bottom - 84, 48, 48));
                var render = typeof(LauncherWindow).GetMethod("OnContainerTransformRendering", flags)!;
                Thread.Sleep(10); render.Invoke(window, [null, EventArgs.Empty]);
                Thread.Sleep(10); render.Invoke(window, [null, EventArgs.Empty]);
                typeof(LauncherWindow).GetMethod("CompleteContainerTransform", flags)!.Invoke(window, [1d]);
                Assert.True(SpinWait.SpinUntil(() => Directory.Exists(traceDirectory)
                    && Directory.GetFiles(traceDirectory, "*.json").Length > 0, 3000), "Animation timing log was not written.");
                using var document = JsonDocument.Parse(File.ReadAllText(Directory.GetFiles(traceDirectory, "*.json")[0]));
                var data = document.RootElement;
                Assert.Equal(2, data.GetProperty("schemaVersion").GetInt32());
                Assert.True(data.GetProperty("requestId").GetInt64() > 0);
                Assert.NotEqual(0, data.GetProperty("hwnd").GetInt64());
                Assert.Equal("completed", data.GetProperty("outcome").GetString());
                Assert.True(data.GetProperty("firstCallbackMilliseconds").GetDouble() > 0);
                Assert.True(data.GetProperty("frameSamples")[0].GetProperty("geometryMilliseconds").GetDouble() >= 0);
                Assert.Equal("unmeasured", data.GetProperty("displayEvidence").GetString());
                Assert.True(data.GetProperty("frameSamples").GetArrayLength() >= 2);
                Assert.True(data.GetProperty("revealPreparationMilliseconds").GetDouble() >= 0);
                Assert.NotEqual(data.GetProperty("uiThreadId").GetInt32(), data.GetProperty("writerThreadId").GetInt32());
                Assert.True(data.GetProperty("surfaceMetrics").GetProperty("revealCaptureMilliseconds").GetDouble() >= 15);
                Assert.True(data.GetProperty("preparationStages").GetArrayLength() > 0);
                var metrics = data.GetProperty("surfaceMetrics");
                Assert.InRange(metrics.GetProperty("renderingTier").GetInt32(), 0, 2);
                Assert.Equal(JsonValueKind.Number, metrics.GetProperty("processRenderMode").ValueKind);
                Assert.True(metrics.GetProperty("gpuLensActive").ValueKind is JsonValueKind.True or JsonValueKind.False);
                Assert.DoesNotContain("\"failure\"", metrics.GetRawText(), StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("C:\\Users", document.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                window.Close(); Environment.SetEnvironmentVariable("GLASSDESK_ANIMATION_TRACE_DIR", previous);
            }
        });
    }
}
