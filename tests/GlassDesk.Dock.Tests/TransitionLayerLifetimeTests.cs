using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Threading;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock.Tests;

public sealed class TransitionLayerLifetimeTests
{
    [Fact]
    public void HardwarePixelsSurviveBackdropRetirementWithoutAStaleEmptyFrame()
    {
        StaTest.Run(() =>
        {
            var pixels = new byte[320 * 180 * 4];
            for (var y = 0; y < 180; y++)
            for (var x = 0; x < 320; x++)
            {
                var i = (y * 320 + x) * 4;
                pixels[i] = (byte)(x / 2); pixels[i + 1] = (byte)y;
                pixels[i + 2] = (byte)(255 - x / 2); pixels[i + 3] = 255;
            }
            var bitmap = BitmapSource.Create(320, 180, 96, 96, PixelFormats.Bgra32, null, pixels, 320 * 4);
            var layer = new TransitionGlassLayer { Opacity = 1 };
            var window = new Window { Width = 320, Height = 180, WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize, Background = Brushes.Black, ShowActivated = false,
                ShowInTaskbar = false, Content = layer };
            void Prepare()
            {
                layer.SetBackdrop(bitmap);
                layer.SetPanel(new Rect(10, 10, 300, 160), new Rect(0, 0, 320, 180), 30, .8, true);
            }
            CapturePixels Capture(bool requireMaterial = false)
            {
                var deadline = System.Diagnostics.Stopwatch.StartNew();
                do
                {
                window.UpdateLayout();
                var frame = new DispatcherFrame();
                var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
                timer.Tick += (_, _) => frame.Continue = false;
                timer.Start();
                try { Dispatcher.PushFrame(frame); }
                finally { timer.Stop(); }
                var captured = WgcWindowCapture.Capture(new WindowInteropHelper(window).Handle, TimeSpan.FromSeconds(3))
                    ?? throw new InvalidOperationException("Hardware material pixels unavailable.");
                // A newly created WGC session can first return an all-zero frame.
                // Such a frame is not a valid material reference for restoration.
                if (!requireMaterial || captured.Center[2] > 0 && captured.Center[3] > 0) return captured;
                } while (deadline.Elapsed < TimeSpan.FromSeconds(3));
                throw new InvalidOperationException("GPU material did not become visible before the comparison.");
            }
            try
            {
                Prepare(); window.Show();
                Assert.True(layer.UsesGpuLens, "This hardware-output validation requires the real GPU lens.");
                var before = Capture(requireMaterial: true);
                layer.SetBackdrop(null);
                Assert.Null(layer.Effect);
                var empty = Capture();
                for (var i = 0; i < empty.Pixels.Length; i += 4)
                    Assert.True(empty.Pixels[i] == 0 && empty.Pixels[i + 1] == 0 && empty.Pixels[i + 2] == 0);
                Prepare();
                Assert.True(layer.UsesGpuLens);
                var after = Capture(requireMaterial: true);
                Assert.Equal(before.Width, after.Width); Assert.Equal(before.Height, after.Height);
                Assert.Equal(before.Pixels, after.Pixels);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void EmptyMaterialCannotCreateOrRetainAnEffect()
    {
        StaTest.Run(() =>
        {
            var layer = new TransitionGlassLayer();
            layer.SetPanel(new Rect(0, 0, 200, 100), new Rect(0, 0, 200, 100), 20, 1);
            Assert.Null(layer.Effect);
        });
    }

    [Fact]
    public void ClearingBackdropReleasesEffectAndRestoresItsParametersOnNextMaterial()
    {
        StaTest.Run(() =>
        {
            var layer = new TransitionGlassLayer();
            var backdrop = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 20, 40, 60, 255 }, 4);
            void Prepare()
            {
                layer.SetBackdrop(backdrop);
                layer.SetPanel(new Rect(10, 20, 200, 100), new Rect(0, 0, 240, 140), 20, .8, true);
            }
            Prepare();
            var original = layer.Effect as GlassDesk.UI.Effects.TransitionGlassEffect;
            layer.SetBackdrop(null);
            Assert.Null(layer.Effect);
            layer.SetPanel(new Rect(10, 20, 200, 100), new Rect(0, 0, 240, 140), 20, .8, true);
            Assert.Null(layer.Effect);
            Prepare();
            if (original is not null)
            {
                var restored = Assert.IsType<GlassDesk.UI.Effects.TransitionGlassEffect>(layer.Effect);
                Assert.Equal(original.Panel, restored.Panel);
                Assert.Equal(original.Material, restored.Material);
                Assert.Equal(original.Profile, restored.Profile);
                Assert.Equal(original.Dispersion, restored.Dispersion);
            }
        });
    }
}
