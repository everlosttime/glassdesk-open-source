using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock.Tests;

public sealed class HiddenMaterialResourceTests
{
    [Fact]
    public void ExpiredNeverShownPreparationReleasesItsLens() => StaTest.Run(() =>
    {
        using var surface = new LiquidGlassSurface();
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(LiquidGlassSurface);
        var layer = (TransitionGlassLayer)type.GetField("_transitionLayer", fields)!.GetValue(surface)!;
        var image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 40, 80, 120, 255 }, 4);
        layer.SetBackdrop(image); layer.SetPanel(new(10, 10, 300, 160), new(0, 0, 320, 180), 30, 1);
        Assert.True(layer.UsesGpuLens);
        type.GetField("_revealCaptureUntil", fields)!.SetValue(surface, 1L);
        type.GetMethod("OnFrame", fields)!.Invoke(surface, [null, EventArgs.Empty]);
        Assert.True(layer.UsesGpuLens, "Short hidden preparation should remain warm.");
        ExpireRetention(surface);
        Assert.Null(layer.Effect);
    });

    [Fact]
    public void HiddenSurfaceReleasesLensWithoutLosingItsPreparedBackdrop() => StaTest.Run(() =>
    {
        var scene = new GlassBackdropScene();
        using var surface = new LiquidGlassSurface { ReadBackdropScene = () => scene };
        var window = new Window { Width = 320, Height = 180, Content = surface, ShowInTaskbar = false,
            ShowActivated = false, WindowStyle = WindowStyle.None };
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(LiquidGlassSurface);
        var pixels = new byte[320 * 180 * 4];
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = 40; pixels[i + 1] = 80; pixels[i + 2] = 120; pixels[i + 3] = 255; }
        var image = BitmapSource.Create(320, 180, 96, 96, PixelFormats.Bgra32, null, pixels, 1280); image.Freeze();
        try
        {
            window.Show(); window.UpdateLayout();
            type.GetField("_transitionBackdrop", fields)!.SetValue(surface, image);
            type.GetField("_transitionBackdropScene", fields)!.SetValue(surface, scene);
            type.GetField("_transitionBackdropBounds", fields)!.SetValue(surface, new Rect(0, 0, 320, 180));
            surface.SetContainerTransitionBounds(new Rect(0, 0, 320, 180));
            surface.SetPanelShape(new Rect(10, 10, 300, 160), 30);
            var layer = (TransitionGlassLayer)type.GetField("_transitionLayer", fields)!.GetValue(surface)!;
            Assert.True(layer.UsesGpuLens, "Prepared visible material has no lens.");
            window.Hide();
            Assert.True(layer.UsesGpuLens, "A rapid reopen must not require a fresh GPU lens.");
            ExpireRetention(surface);
            Assert.Null(layer.Effect);
            Assert.Same(image, type.GetField("_transitionBackdrop", fields)!.GetValue(surface));
            surface.SetPanelShape(new Rect(15, 15, 290, 150), 30);
            Assert.Null(layer.Effect); // A queued hidden geometry update must not recreate it.
            window.Show(); surface.SetPanelShape(new Rect(10, 10, 300, 160), 30);
            Assert.True(layer.UsesGpuLens, "Reopened valid material has no lens.");
            Assert.Same(image, type.GetField("_transitionBackdrop", fields)!.GetValue(surface));
        }
        finally { window.Close(); }
    });

    private static void ExpireRetention(LiquidGlassSurface surface)
    {
        var timer = (DispatcherTimer)typeof(LiquidGlassSurface).GetField("_presentationIdleTimer",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface)!;
        timer.Interval = TimeSpan.FromMilliseconds(10);
        var frame = new DispatcherFrame();
        var stop = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        stop.Tick += (_, _) => frame.Continue = false;
        stop.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { stop.Stop(); }
    }
}
