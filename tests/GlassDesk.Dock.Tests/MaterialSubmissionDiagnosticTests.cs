using System.Reflection;
using System.Windows;
using GlassDesk.UI;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock.Tests;

public sealed class MaterialSubmissionDiagnosticTests
{
    [Fact]
    public void SubmittedPixelMetadataSeparatesTheRealWindowFromItsOpticalProfile()
    {
        StaTest.Run(() =>
        {
            using var surface = new LiquidGlassSurface { UseLaunchpadOptics = false };
            var window = new StatusBarWindow { Content = surface };
            try
            {
                var method = typeof(LiquidGlassSurface).GetMethod("RecordSubmittedPixels", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(method);
                var input = new byte[] { 0, 180, 0, 255, 0, 180, 0, 255 };
                var submitted = new byte[] { 0, 0, 0, 255, 0, 0, 0, 255 };
                method.Invoke(surface, [input, submitted, 2, 1, 123456L]);
                var log = (CaptureTraceLog)typeof(BackdropRecovery).GetField("Diagnostics", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
                var events = log.Snapshot().RecentEvents.Where(e => e.SourceTicks == 123456L).ToArray();
                Assert.Contains(events, e => e.Stage == "MaterialInputPixels" && e.Surface == "StatusBarWindow" && e.MeanChannel == 60);
                Assert.Contains(events, e => e.Stage == "MaterialSubmittedPixels" && e.Surface == "StatusBarWindow" && e.NearBlackFraction == 1);
                Assert.Equal(new byte[] { 0, 180, 0, 255, 0, 180, 0, 255 }, input);
                Assert.Equal(new byte[] { 0, 0, 0, 255, 0, 0, 0, 255 }, submitted);
            }
            finally { window.Close(); }
        });
    }
}
