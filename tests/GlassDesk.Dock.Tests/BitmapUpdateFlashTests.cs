using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock.Tests;

public sealed class BitmapUpdateFlashTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedBitmapUpdatesKeepTheLayeredWindowMaterialVisible(bool shader)
    {
        StaTest.Run(() =>
        {
            const int width = 380, height = 64;
            var pixels = new byte[width * height * 4];
            for (var i = 0; i < pixels.Length; i += 4)
            { pixels[i] = 100; pixels[i + 1] = 130; pixels[i + 2] = 160; pixels[i + 3] = 255; }
            Assert.True(LiquidGlassSurface.TryUpdateBackdropBitmap(null, pixels, width, height, out var bitmap));
            var layer = new TransitionGlassLayer { Opacity = 1 };
            layer.SetBackdrop(bitmap);
            layer.SetPanel(new Rect(0, 0, width, height), new Rect(0, 0, width, height), 25, 1, frosted: !shader);
            var window = new Window { Width = width, Height = height, WindowStyle = WindowStyle.None,
                AllowsTransparency = true, Background = Brushes.Transparent, ResizeMode = ResizeMode.NoResize,
                ShowActivated = false, ShowInTaskbar = false, Content = layer };
            try
            {
                window.Show(); window.UpdateLayout();
                using var capture = new PersistentWindowCapture(new WindowInteropHelper(window).Handle);
                var watch = Stopwatch.StartNew(); var frames = 0;
                while (watch.Elapsed < TimeSpan.FromSeconds(4))
                {
                    Assert.True(LiquidGlassSurface.TryUpdateBackdropBitmap(bitmap, pixels, width, height, out _));
                    var dispatcherFrame = new DispatcherFrame();
                    var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(12) };
                    timer.Tick += (_, _) => dispatcherFrame.Continue = false;
                    timer.Start(); try { Dispatcher.PushFrame(dispatcherFrame); } finally { timer.Stop(); }
                    var current = capture.TryRead(waitForFrame: TimeSpan.FromMilliseconds(10));
                    if (current is null || watch.ElapsedMilliseconds < 500) continue;
                    frames++;
                    Assert.True(current.Center[0] > 50 && current.Center[3] > 200,
                        $"Material lost at {watch.ElapsedMilliseconds} ms: {string.Join(',', current.Center)}; shader={shader}");
                }
                Assert.True(frames > 20, $"Insufficient GPU frames: {frames}");
            }
            finally { window.Close(); }
        });
    }
}
