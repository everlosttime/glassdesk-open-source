using System.Reflection;
using GlassDesk.Platform;
using GlassDesk.Platform.Displays;

namespace GlassDesk.Dock.Tests;

public sealed class DocumentedCaptureIndicatorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadingOurDocumentedCompositeDoesNotCreateASystemCaptureSession(bool desktopOnly)
    {
        StaTest.Run(() =>
        {
            var bounds = DisplayService.Primary()!.Bounds;
            var region = new ScreenRect(bounds.X + 100, bounds.Y + 100, 160, 120);
            using var scene = new DocumentedDesktopSceneCapture(region, false, null, null, desktopOnly);
            var reader = typeof(DocumentedDesktopSceneCapture).GetField("_capture", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scene);
            Assert.False(reader is PersistentWindowCapture,
                "The app's own composite must be read without starting a Windows.Graphics.Capture session and its capture border.");
            var frame = scene.TryRead(TimeSpan.FromMilliseconds(100));
            Assert.NotNull(frame);
            Assert.Equal(160 * 120 * 4, frame.Pixels.Length);
        });
    }
}
