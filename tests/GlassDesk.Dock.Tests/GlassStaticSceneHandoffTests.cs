using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock.Tests;

public sealed class GlassStaticSceneHandoffTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public void OnlyFreshIdenticalPixelsValidateTheNewSceneWithoutRebuildingTheDisplayedBitmap(bool transition, bool fresh)
    {
        StaTest.Run(() =>
        {
            var original = new GlassBackdropScene.SceneWindow((nint)101, 42, 0, 0, 800, 600);
            var scene = new GlassBackdropScene(original);
            using var surface = new LiquidGlassSurface { ReadBackdropScene = () => scene };
            using var publishing = new ManualResetEventSlim(true);
            long publishedAt = 0;
            using var feed = new SharedBackdropFeed((state, stop) =>
            {
                long sequence = 0;
                while (!stop.IsCancellationRequested)
                {
                    if (publishing.IsSet && state.CurrentRequest is { } request)
                    {
                        var pixels = new byte[request.Width * request.Height * 4];
                        for (var offset = 0; offset < pixels.Length; offset += 4)
                        {
                            pixels[offset + 1] = 200;
                            pixels[offset + 3] = 255;
                        }

                        var now = Stopwatch.GetTimestamp();
                        if (state.Publish(new BackdropFrame(request, ++sequence, now, now, pixels, now)))
                            Volatile.Write(ref publishedAt, now);
                    }

                    if (stop.WaitHandle.WaitOne(8)) return;
                }
            });
            var background = Get(surface, "_background")!;
            typeof(GlassBackgroundLayer).GetField("_compatible", Private)!.SetValue(background, feed);
            var owner = new StaticSceneWindow
            {
                Content = surface, Width = 200, Height = 100, Left = 50, Top = 50, ShowActivated = false,
            };
            try
            {
                owner.Show();
                surface.UpdateLayout();
                surface.SetPanelShape(new Rect(0, 0, 200, 100), 20);
                if (transition) surface.SetContainerTransitionBounds(new Rect(0, 0, 200, 100));
                var frameField = transition ? "_transitionBackdrop" : "_frame";
                var sceneField = transition ? "_transitionBackdropScene" : "_frameScene";
                var timeout = Stopwatch.StartNew();
                while (Get(surface, frameField) is null && timeout.Elapsed < TimeSpan.FromSeconds(3))
                    global::Program.Pump(TimeSpan.FromMilliseconds(10));
                var displayed = Get(surface, frameField);
                Assert.NotNull(displayed);
                global::Program.Pump(TimeSpan.FromMilliseconds(40));
                if (!fresh)
                {
                    publishing.Reset();
                    Thread.Sleep(16);
                    var settle = Stopwatch.StartNew();
                    while ((long)Get(surface, "_lastCaptureSourceTicks")! < Volatile.Read(ref publishedAt)
                        && settle.Elapsed < TimeSpan.FromMilliseconds(80))
                    {
                        typeof(LiquidGlassSurface).GetMethod("OnFrame", Private)!.Invoke(surface, [null, EventArgs.Empty]);
                        global::Program.Pump(TimeSpan.FromMilliseconds(5));
                    }
                    Assert.Equal(Volatile.Read(ref publishedAt), (long)Get(surface, "_lastCaptureSourceTicks")!);
                }
                displayed = Get(surface, frameField);
                scene = new GlassBackdropScene(original with { Top = 20 });
                surface.RequestBackdropRefresh();
                global::Program.Pump(TimeSpan.FromMilliseconds(60));

                Assert.True(fresh == scene.Equals(Get(surface, sceneField)), "Only a new source timestamp may validate the changed scene.");
                Assert.Same(displayed, Get(surface, frameField));
                typeof(LiquidGlassSurface).GetField("_materialRetentionUntil", Private)!.SetValue(surface, 1L);
                surface.RequestBackdropRefresh();
                Assert.Equal(fresh, Get(surface, frameField) is not null);
                surface.Measure(new Size(200, 100));
                surface.Arrange(new Rect(0, 0, 200, 100));
                surface.UpdateLayout();
                var image = new RenderTargetBitmap(200, 100, 96, 96, PixelFormats.Pbgra32);
                image.Render(surface);
                var center = new byte[4];
                image.CopyPixels(new Int32Rect(100, 50, 1, 1), center, 4, 0);
                if (fresh) Assert.True(center[1] > 100, "Fresh unchanged capture must keep complete glass.");
            }
            finally { owner.Close(); }
        });
    }

    private static object? Get(LiquidGlassSurface surface, string name)
        => typeof(LiquidGlassSurface).GetField(name, Private)!.GetValue(surface);

    private sealed class StaticSceneWindow : GlassWindow;
}
