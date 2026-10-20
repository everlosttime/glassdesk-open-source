using System.Diagnostics;
using System.IO;
using System.Reflection;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock.Tests;

public sealed class CaptureWorkerTraceTests
{
    [Theory]
    [InlineData("native-trace")]
    [InlineData("native-trace-too-large")]
    public void NativeSceneEventsCrossTheRealWorkerBoundaryWithABoundedEnvelope(string fault)
    {
        var start = new ProcessStartInfo(CompatibilityProbePath.File("GlassCaptureProbe.exe"));
        start.ArgumentList.Add("--capture-protocol-fixture"); start.ArgumentList.Add(fault);
        var events = new List<CaptureTrace>();
        var ctor = typeof(IsolatedDesktopSceneCapture).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
            null, [typeof(ScreenRect), typeof(ProcessStartInfo), typeof(GlassBackdropSource), typeof(Action<CaptureTrace>)], null)!;
        using var capture = (IsolatedDesktopSceneCapture)ctor.Invoke([new ScreenRect(0, 0, 2, 2), start,
            GlassBackdropSource.LiveDesktop, (Action<CaptureTrace>)events.Add]);
        if (fault == "native-trace-too-large") Assert.Throws<InvalidDataException>(() => capture.TryRead());
        else
        {
            Assert.NotNull(capture.TryRead());
            Assert.Contains(events, trace => trace.Stage == "VisualSwap" && trace.Reason == "NewThumbnail");
            var native = events.Single(trace => trace.Stage == "VisualSwap");
            var metadata = System.Text.Json.JsonSerializer.SerializeToElement(native);
            Assert.True(metadata.TryGetProperty("WorkerProcess", out var worker), "Native events must identify their owning helper.");
            Assert.InRange(worker.GetInt32(), 1, int.MaxValue);
        }
    }
}
