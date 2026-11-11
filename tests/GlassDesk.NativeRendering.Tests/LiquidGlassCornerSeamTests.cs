using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using System.Windows.Media;

namespace GlassDesk.NativeRendering.Tests;

// The native test presenter compiles the production WPF lens body unchanged.
// Read back its actual GPU output: RenderTargetBitmap does not run the ps_3_0 lens.
public sealed class LiquidGlassCornerSeamTests
{
    [Fact]
    public void DeepCornerInteriorDoesNotRestartTheLensAtFractionalCoordinates() => NativeEngineTests.OnSta(() =>
    {
        const int width = 240, height = 140, sw = 336, sh = 236;
        var host = new System.Windows.Window();
        var info = Info.Create(width, height, new System.Windows.Interop.WindowInteropHelper(host).EnsureHandle());
        info.Flags = 1;
        Assert.Equal(0, DirectPresenterTests.Create(ref info, out var presenter));
        try
        {
            var source = new byte[sw * sh * 4];
            for (var y = 0; y < sh; y++)
            for (var x = 0; x < sw; x++)
            {
                var i = (y * sw + x) * 4;
                source[i] = source[i + 1] = source[i + 2] = (byte)(y / 2 + 40); source[i + 3] = 255;
            }
            var pin = GCHandle.Alloc(source, GCHandleType.Pinned);
            try
            {
                var upload = new StaticMaterialTests.Upload { Size = 32, Version = 1, Width = sw, Height = sh,
                    Stride = sw * 4, Pixels = pin.AddrOfPinnedObject() };
                Assert.Equal(0, DirectPresenterTests.UploadBitmap(presenter, ref upload));
            }
            finally { pin.Free(); }
            var material = new StaticMaterialTests.Material { Size = 48, Version = 1, DipWidth = sw, DipHeight = sh,
                Strength = 1, Clarity = 1, TintOpacity = 0, Dark = 1 };
            var scene = new DirectGlassTests.GlassScene { Size = 80, Version = 2, Outline = 1,
                HostWidth = width, HostHeight = height, PanelX = 7.4999f, PanelY = 8.5001f, PanelWidth = 224, PanelHeight = 124,
                BackdropX = -48, BackdropY = -48, BackdropWidth = sw, BackdropHeight = sh,
                Radius = 40, Strength = 0, Dispersion = 1, InnerGain = 1, OuterGain = 1 };
            Assert.Equal(0, DirectGlassTests.RenderGlass(presenter, ref material, ref scene));
            Assert.Equal(0, DirectPresenterTests.ReadPixel(presenter, 191, 48, out var flat));
            scene.Strength = 1;
            Assert.Equal(0, DirectGlassTests.RenderGlass(presenter, ref material, ref scene));
            Assert.Equal(0, DirectPresenterTests.ReadPixel(presenter, 191, 48, out var lens));
            // This point is almost 40 DIP inside the shape, beyond the 24 DIP lens
            // band. Fractional layout must not turn it into another optical edge.
            var expected = BitConverter.GetBytes(flat); var actual = BitConverter.GetBytes(lens);
            for (var c = 0; c < 4; c++) Assert.InRange(Math.Abs(expected[c] - actual[c]), 0, 1);
        }
        finally { DirectPresenterTests.Destroy(presenter); host.Close(); }
    });

    [Theory]
    [InlineData(1, .5f, 124)]
    [InlineData(1.5, .5f, 124)]
    [InlineData(2, .5f, 124)]
    [InlineData(1, 1f, 124)]
    [InlineData(1, .5f, 84)]
    [InlineData(1.5, .5f, 84)]
    [InlineData(2, .5f, 84)]
    [InlineData(1, 1f, 84)]
    public void MovingBackdropHasNoStepAtCornerToStraightJoins(double scale, float clarity, int panelHeight) => NativeEngineTests.OnSta(() =>
    {
        var width = (int)(240 * scale); var height = (int)(140 * scale);
        var sw = (uint)(336 * scale); var sh = (uint)(236 * scale);
        var host = new System.Windows.Window();
        var info = Info.Create((uint)width, (uint)height, new System.Windows.Interop.WindowInteropHelper(host).EnsureHandle());
        info.Flags = 1;
        Assert.Equal(0, DirectPresenterTests.Create(ref info, out var presenter));
        try
        {
            var material = new StaticMaterialTests.Material { Size = 48, Version = 1, DipWidth = 336, DipHeight = 236,
                Strength = 1, Clarity = clarity, TintOpacity = .16f, Dark = 1 };
            var scene = new DirectGlassTests.GlassScene { Size = 80, Version = 2, Outline = 1,
                HostWidth = 240, HostHeight = 140, PanelX = 8, PanelY = 8, PanelWidth = 224, PanelHeight = panelHeight,
                BackdropX = -48, BackdropY = -48, BackdropWidth = 336, BackdropHeight = 236,
                Radius = 40, Strength = 1, Dispersion = 1, InnerGain = 1, OuterGain = 1 };
            var evidence = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Api.LibraryPath)!, "corner-seam", $"height-{panelHeight}-scale-{scale}-clarity-{clarity}");
            System.IO.Directory.CreateDirectory(evidence);
            var maximum = 0;
            var observations = new List<object>();
            foreach (var horizontal in new[] { false, true })
            foreach (var phase in new[] { 0, 12, 24, 36 })
            {
                var source = new byte[sw * sh * 4];
                for (var y = 0; y < sh; y++)
                for (var x = 0; x < sw; x++)
                {
                    var i = (y * sw + x) * 4;
                    var value = (byte)Math.Round(128 + 90 * Math.Sin(((horizontal ? x : y) / scale + phase) * .07));
                    source[i] = source[i + 1] = source[i + 2] = value; source[i + 3] = 255;
                }
                var pin = GCHandle.Alloc(source, GCHandleType.Pinned);
                try
                {
                    var upload = new StaticMaterialTests.Upload { Size = 32, Version = 1, Width = sw, Height = sh,
                        Stride = sw * 4, Pixels = pin.AddrOfPinnedObject() };
                    Assert.Equal(0, DirectPresenterTests.UploadBitmap(presenter, ref upload));
                }
                finally { pin.Free(); }
                Assert.Equal(0, DirectGlassTests.RenderGlass(presenter, ref material, ref scene));
                var pixels = new byte[width * height * 4];
                Assert.Equal(0, DirectGlassTests.ReadPixels(presenter, pixels, (ulong)pixels.Length, (uint)width * 4));
                var frameMaximum = 0;
                // A smooth backdrop varying perpendicular to a join must not acquire
                // a vertical/horizontal stripe there. Both sides are fully covered.
                foreach (var join in horizontal ? new[] { 48, 8 + panelHeight - 40 } : new[] { 48, 192 })
                foreach (var side in new[] { 0, 1 })
                for (var depth = 4; depth <= 28; depth++)
                {
                    var along = (int)(join * scale);
                    var across = (int)((side == 0 ? 8 + depth : (horizontal ? 232 : 8 + panelHeight) - depth) * scale);
                    var a = horizontal ? ((along - 1) * width + across) * 4 : (across * width + along - 1) * 4;
                    var b = horizontal ? (along * width + across) * 4 : (across * width + along) * 4;
                    Assert.Equal(255, pixels[a + 3]); Assert.Equal(255, pixels[b + 3]);
                    frameMaximum = Math.Max(frameMaximum, Math.Abs(pixels[a + 1] - pixels[b + 1]));
                }
                maximum = Math.Max(maximum, frameMaximum);
                observations.Add(new { horizontal, phase, maximumJoinStep = frameMaximum });
                var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = System.IO.File.Create(System.IO.Path.Combine(evidence, $"axis-{horizontal}-phase-{phase}.png"));
                encoder.Save(stream);
            }
            System.IO.File.WriteAllText(System.IO.Path.Combine(evidence, "metrics.json"), System.Text.Json.JsonSerializer.Serialize(observations));
            Assert.True(maximum <= 2, $"Smooth moving backdrop acquired a {maximum}/255 channel step at a corner join (scale {scale}, clarity {clarity}).");
        }
        finally { DirectPresenterTests.Destroy(presenter); host.Close(); }
    });
}
