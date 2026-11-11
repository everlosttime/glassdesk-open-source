using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock.Tests;

public sealed class TaskbarBackdropHandoffTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void TaskbarRevealOrHideWhileLiveWallpaperCaptureWaitsNeverMakesAnOpaqueDock(bool initiallyVisible, bool deadlineExpired)
    {
        StaTest.Run(() =>
        {
            var wallpaper = new GlassBackdropScene.SceneWindow((nint)101, 42, 0, 0, 1920, 1080);
            var tray = new GlassBackdropScene.SceneWindow((nint)102, 43, 0, 1016, 1920, 1080);
            var scene = initiallyVisible ? new GlassBackdropScene(tray, wallpaper) : new GlassBackdropScene(wallpaper);
            using var surface = new LiquidGlassSurface { ReadBackdropScene = () => scene, IsBackdropActive = () => true };
            using var feed = new SharedBackdropFeed((_, stop) => stop.WaitHandle.WaitOne());
            var background = (GlassBackgroundLayer)Get(surface, "_background")!;
            typeof(GlassBackgroundLayer).GetField("_compatible", Private)!.SetValue(background, feed);
            var owner = new Fixture { Width = 200, Height = 100, Content = surface, ShowActivated = false };
            try
            {
                owner.Show(); surface.UpdateLayout(); surface.SetPanelShape(new Rect(0, 0, 200, 100), 40);
                // Same fixed capture path as the production Dock while it slides.
                surface.SetContainerTransitionBounds(new Rect(0, -100, 200, 200));
                var bitmap = new WriteableBitmap(1, 1, 96, 96, PixelFormats.Bgra32, null);
                bitmap.WritePixels(new Int32Rect(0, 0, 1, 1), new byte[] { 20, 210, 70, 255 }, 4, 0);
                Set(surface, "_transitionBackdrop", bitmap); Set(surface, "_transitionBackdropBounds", new Rect(0, -100, 200, 200));
                Set(surface, "_transitionBackdropScene", scene);
                Set(surface, "_materialStamp", typeof(LiquidGlassSurface).GetMethod("ReadMaterialStamp", Private)!.Invoke(surface, null)!);
                Assert.Equal(255, RenderCenter(surface)[3]);
                scene = initiallyVisible ? new GlassBackdropScene(wallpaper) : new GlassBackdropScene(tray, wallpaper);
                surface.RequestBackdropRefresh(); Set(surface, "_materialRetentionUntil", 1L); surface.RequestBackdropRefresh();
                if (deadlineExpired)
                {
                    typeof(GlassBackgroundLayer).GetField("_pendingSince", Private)!.SetValue(background, Stopwatch.GetTimestamp() - Stopwatch.Frequency * 4);
                    typeof(LiquidGlassSurface).GetMethod("OnFrame", Private)!.Invoke(surface, [null, EventArgs.Empty]);
                }
                var pixel = RenderCenter(surface);
                Assert.Null(Get(surface, "_transitionBackdrop"));
                Assert.InRange(pixel[3], (byte)1, (byte)80);
                Assert.True(pixel[1] < 100, "The previous wallpaper capture must not be frozen across the taskbar scene change.");
            }
            finally { owner.Close(); }
        });
    }
    private static byte[] RenderCenter(LiquidGlassSurface surface)
    {
        surface.Measure(new Size(200, 100)); surface.Arrange(new Rect(0, 0, 200, 100)); surface.UpdateLayout();
        var bitmap = new RenderTargetBitmap(200, 100, 96, 96, PixelFormats.Pbgra32); bitmap.Render(surface);
        var pixel = new byte[4]; bitmap.CopyPixels(new Int32Rect(100, 50, 1, 1), pixel, 4, 0); return pixel;
    }
    private static object? Get(object target, string name) => target.GetType().GetField(name, Private)!.GetValue(target);
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Private)!.SetValue(target, value);
    private sealed class Fixture : GlassWindow;
}
