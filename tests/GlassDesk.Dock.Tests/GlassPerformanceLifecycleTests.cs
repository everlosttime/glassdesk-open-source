using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GlassDesk.UI.Controls;
using GlassDesk.UI;
using GlassDesk.UI.Theming;
using GlassDesk.Core.Models.Settings;

namespace GlassDesk.Dock.Tests;

public sealed class GlassPerformanceLifecycleTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void ScreenEdgeExtensionDropsTheFourthFrameAndDoesNotOverwriteOwnedPixels()
    {
        using var background = new GlassBackgroundLayer();
        var extend = typeof(GlassBackgroundLayer).GetMethod("ExtendCapturedFrame", Private);
        Assert.NotNull(extend);
        GlassBackgroundFrame? Extend(byte value)
        {
            using var input = GlassBackgroundFrame.FromOwnedPixels(new byte[] { value, 0, 0, 255 }, 1, 1, GlassBackdropSource.LiveDesktop);
            return (GlassBackgroundFrame?)extend.Invoke(background,
                [input, new GlassDesk.Platform.Displays.PixelRect(0, 0, 1, 1), new GlassDesk.Platform.Displays.PixelRect(-1, -1, 3, 3), 3, 3]);
        }
        using var first = Extend(10)!; using var second = Extend(20)!; using var third = Extend(30)!;
        Assert.Null(Extend(40)); Assert.Equal(10, first.Pixels[0]);
        var reusedPixels = first.Pixels; first.Dispose();
        using var next = Extend(40)!;
        Assert.Same(reusedPixels, next.Pixels); Assert.Equal(20, second.Pixels[0]);
        background.SetPerformanceSleeping(true);
        Assert.Equal(40, next.Pixels[0]);
    }

    [Fact]
    public void AppearanceRefreshWithUnchangedValuesRejectsOldMaterialEvenDuringSleep()
    {
        StaTest.Run(() =>
        {
            var scene = new GlassBackdropScene();
            using var surface = new LiquidGlassSurface { ReadBackdropScene = () => scene };
            surface.SetPanelShape(new(0, 0, 200, 100), 20); Render(surface); SeedLive(surface, scene);
            surface.SetPerformanceSleeping(true);
            typeof(LiquidGlassSurface).GetMethod("OnAppearanceChanged", Private)!.Invoke(surface, [null, EventArgs.Empty]);
            Assert.Null(Get(surface, "_frame"));
            Assert.Null(Get(surface, "_transitionBackdrop"));
            surface.SetPerformanceSleeping(false);
            var pixels = Render(surface);
            Assert.Equal(255, pixels[(50 * 200 + 100) * 4 + 3]);
            Assert.NotEqual(240, pixels[(50 * 200 + 100) * 4 + 1]);
        });
    }

    [Fact]
    public void AThemeChangeWithTheSameTransparencyCannotReusePreviouslyShadedPixels()
    {
        StaTest.Run(() =>
        {
            using var theme = ThemeService.Initialize(Dispatcher.CurrentDispatcher);
            theme.Update(new AppearanceSettings { Theme = GlassDesk.Core.Models.ThemeMode.Dark });
            var scene = new GlassBackdropScene();
            using var surface = new LiquidGlassSurface { ReadBackdropScene = () => scene };
            surface.SetPanelShape(new(0, 0, 200, 100), 20); Render(surface); SeedLive(surface, scene);
            Assert.Equal(240, Render(surface)[(50 * 200 + 100) * 4 + 1]);
            theme.Update(new AppearanceSettings { Theme = GlassDesk.Core.Models.ThemeMode.Light });
            surface.InvalidateVisual();
            var pixels = Render(surface);
            Assert.Equal(255, pixels[(50 * 200 + 100) * 4 + 3]);
            Assert.NotEqual(240, pixels[(50 * 200 + 100) * 4 + 1]);
            Assert.Null(Get(surface, "_frame"));
        });
    }

    [Fact]
    public void RecoveryAfterDeepSleepCannotRevealThePreviousApplicationTexture()
    {
        StaTest.Run(() =>
        {
            var scene = new GlassBackdropScene(new GlassBackdropScene.SceneWindow((nint)101, 42, 0, 0, 800, 600));
            using var surface = new LiquidGlassSurface { ReadBackdropScene = () => scene };
            surface.SetPanelShape(new(0, 0, 200, 100), 20); Render(surface); SeedLive(surface, scene);
            surface.SetPerformanceSleeping(true); BackdropRecovery.RetryBackground();
            scene = new(new GlassBackdropScene.SceneWindow((nint)101, 42, 0, 0, 800, 600));
            surface.SetPerformanceSleeping(false);
            var pixels = Render(surface);
            Assert.Equal(255, pixels[(50 * 200 + 100) * 4 + 3]);
            Assert.NotEqual(240, pixels[(50 * 200 + 100) * 4 + 1]);
        });
    }

    [Fact]
    public void LiveHandoffBlendsTheCompatibleMaterialAndFinishesWithinOneHundredMilliseconds()
    {
        StaTest.Run(() =>
        {
            var scene = new GlassBackdropScene();
            using var surface = new LiquidGlassSurface { ReadBackdropScene = () => scene };
            surface.SetPanelShape(new(0, 0, 200, 100), 20);
            var first = Render(surface)[(50 * 200 + 100) * 4 + 1];
            SeedLive(surface, scene);
            Set(surface, "_handoffStarted", System.Diagnostics.Stopwatch.GetTimestamp() - System.Diagnostics.Stopwatch.Frequency / 20);
            surface.InvalidateVisual();
            var middle = Render(surface);
            Assert.Equal(255, middle[(50 * 200 + 100) * 4 + 3]);
            Assert.InRange(middle[(50 * 200 + 100) * 4 + 1], first + 10, 230);
            Set(surface, "_handoffStarted", System.Diagnostics.Stopwatch.GetTimestamp() - System.Diagnostics.Stopwatch.Frequency * 11 / 100);
            surface.InvalidateVisual();
            var completed = Render(surface);
            Assert.Equal(240, completed[(50 * 200 + 100) * 4 + 1]);
            Assert.Null(Get(surface, "_compatibilityBackdrop"));
        });
    }

    [Fact]
    public void OpaquePreferenceSuppressesBothOpticalAndCompatibleTextures()
    {
        StaTest.Run(() =>
        {
            using var theme = ThemeService.Initialize(Dispatcher.CurrentDispatcher);
            theme.Update(new AppearanceSettings { ReduceTransparency = true });
            using var surface = new LiquidGlassSurface { ReadBackdropScene = () => new() };
            surface.SetPanelShape(new(0, 0, 200, 100), 20);
            var pixels = Render(surface);
            Assert.Equal(new byte[] { 34, 29, 28, 255 }, pixels.AsSpan((50 * 200 + 100) * 4, 4).ToArray());
            Assert.Null(Get(surface, "_compatibilityBackdrop"));
        });
    }

    [Fact]
    public void FirstSynchronousRenderDoesNotNeedASecondLayoutToFillTheMaterial()
    {
        StaTest.Run(() =>
        {
            using var surface = new LiquidGlassSurface { ReadBackdropScene = () => new() };
            surface.SetPanelShape(new Rect(20, 10, 160, 80), 20);
            surface.Measure(new Size(200, 100)); surface.Arrange(new Rect(0, 0, 200, 100));
            var rendered = new RenderTargetBitmap(200, 100, 96, 96, PixelFormats.Pbgra32); rendered.Render(surface);
            var pixel = new byte[4]; rendered.CopyPixels(new Int32Rect(100, 50, 1, 1), pixel, 4, 0);
            Assert.Equal(255, pixel[3]);
        });
    }

    [Fact]
    public void ColdFirstPaintHasAnOpaqueCompatibleTextureInsideTheGlassMask()
    {
        StaTest.Run(() =>
        {
            using var surface = new LiquidGlassSurface { ReadBackdropScene = () => new() };
            surface.SetPanelShape(new Rect(20, 10, 160, 80), 20);
            var pixels = Render(surface);
            Assert.Equal(255, pixels[(50 * 200 + 100) * 4 + 3]);
            Assert.Equal(0, pixels[3]);
            Assert.True(pixels[(50 * 200 + 100) * 4 + 1] > 0,
                "A pending native capture must have a complete material, including on dynamic-wallpaper systems.");
        });
    }

    [Fact]
    public void ChangedSceneFirstPaintHasCompleteMaterialWithoutThePreviousApplicationPixels()
    {
        StaTest.Run(() =>
        {
            var scene = new GlassBackdropScene(new GlassBackdropScene.SceneWindow((nint)101, 42, 0, 0, 800, 600));
            using var surface = new LiquidGlassSurface { ReadBackdropScene = () => scene };
            surface.RequestBackdropRefresh();
            var old = new WriteableBitmap(1, 1, 96, 96, PixelFormats.Bgra32, null);
            old.WritePixels(new(0, 0, 1, 1), new byte[] { 0, 240, 0, 255 }, 4, 0);
            Set(surface, "_frame", old); Set(surface, "_frameUsesAndroidGlass", true);
            Set(surface, "_sampledPanelBounds", new Rect(0, 0, 200, 100));
            Set(surface, "_sampledGlassPanelBounds", new Rect(0, 0, 200, 100));
            Set(surface, "_sampledGlassPanelRadius", 20d);
            surface.SetPanelShape(new(0, 0, 200, 100), 20);
            scene = new(new GlassBackdropScene.SceneWindow((nint)202, 42, 0, 0, 800, 600));
            var pixels = Render(surface);
            Assert.Equal(255, pixels[(50 * 200 + 100) * 4 + 3]);
            Assert.NotEqual(240, pixels[(50 * 200 + 100) * 4 + 1]);
        });
    }

    [Fact]
    public void PerformanceSleepCancelsQueuedRenderingAndDoesNotRestartOnRefresh()
    {
        StaTest.Run(() =>
        {
            using var surface = new LiquidGlassSurface();
            typeof(LiquidGlassSurface).GetMethod("OnRenderingFrame", Private)!.Invoke(surface, [null, EventArgs.Empty]);
            Assert.True((bool)Get(surface, "_renderFrameQueued")!);
            var sleep = typeof(LiquidGlassSurface).GetMethod("SetPerformanceSleeping");
            Assert.NotNull(sleep);
            sleep.Invoke(surface, [true]);
            surface.RequestBackdropRefresh(revealing: true);
            Assert.False((bool)Get(surface, "_renderFrameQueued")!);
            Assert.False(((DispatcherTimer)Get(surface, "_timer")!).IsEnabled);
            Assert.False((bool)Get(surface, "_refreshPending")!);
            var pixels = Render(surface);
            Assert.Equal(255, pixels[(50 * 200 + 100) * 4 + 3]);
            Dispatcher.CurrentDispatcher.InvokeShutdown();
        });
    }

    [Fact]
    public void CaptureFailureKeepsCompatibleMaterialUntilActualShaderFailure()
    {
        StaTest.Run(() =>
        {
            BackdropRecovery.RetryBackground();
            using var surface = new LiquidGlassSurface { ReadBackdropScene = () => new() };
            surface.SetPanelShape(new Rect(20, 10, 160, 80), 20);
            Render(surface);
            var background = (GlassBackgroundLayer)Get(surface, "_background")!;
            typeof(GlassBackgroundLayer).GetField("<RequiresBasicBackground>k__BackingField", Private)!.SetValue(background, true);
            typeof(LiquidGlassSurface).GetMethod("UpdateTransitionLayer", Private)!.Invoke(surface, [new Size(200, 100)]);
            Assert.NotNull(Get(surface, "_compatibilityBackdrop"));
            Assert.Equal(1, ((FrameworkElement)Get(surface, "_compatibilityLayer")!).Opacity);
        });
    }

    private static byte[] Render(LiquidGlassSurface surface)
    {
        surface.Measure(new Size(200, 100)); surface.Arrange(new Rect(0, 0, 200, 100)); surface.UpdateLayout();
        var rendered = new RenderTargetBitmap(200, 100, 96, 96, PixelFormats.Pbgra32); rendered.Render(surface);
        var pixels = new byte[200 * 100 * 4]; rendered.CopyPixels(pixels, 800, 0); return pixels;
    }
    private static object? Get(LiquidGlassSurface surface, string field) => typeof(LiquidGlassSurface).GetField(field, Private)!.GetValue(surface);
    private static void Set(LiquidGlassSurface surface, string field, object value) => typeof(LiquidGlassSurface).GetField(field, Private)!.SetValue(surface, value);

    private static void SeedLive(LiquidGlassSurface surface, GlassBackdropScene scene)
    {
        var frame = new WriteableBitmap(1, 1, 96, 96, PixelFormats.Bgra32, null);
        frame.WritePixels(new(0, 0, 1, 1), new byte[] { 0, 240, 0, 255 }, 4, 0);
        Set(surface, "_frame", frame); Set(surface, "_frameScene", scene); Set(surface, "_frameUsesAndroidGlass", true);
        Set(surface, "_sampledPanelBounds", new Rect(0, 0, 200, 100));
        Set(surface, "_sampledGlassPanelBounds", new Rect(0, 0, 200, 100)); Set(surface, "_sampledGlassPanelRadius", 20d);
        Set(surface, "_materialStamp", typeof(LiquidGlassSurface).GetMethod("ReadMaterialStamp", Private)!.Invoke(surface, null)!);
        surface.InvalidateVisual();
    }
}
