using System.Reflection;
using System.Windows.Media.Imaging;
using GlassDesk.Launcher;
using GlassDesk.Platform.Displays;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock.Tests;

public sealed class LauncherRevealMaterialTests
{
    [Fact]
    public void BeforeRevealCaptureRejectsOverlappingProductWindowsWithoutChangingTheirAffinity()
    {
        StaTest.Run(() =>
        {
            var window = new System.Windows.Window { Left = 20, Top = 20, Width = 100, Height = 100 };
            try
            {
                window.Show();
                var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                var region = GlassDesk.Platform.Displays.DisplayService.GetWindowPixelBounds(handle)!.Value;
                using var sampler = new GlassDesk.Platform.DesktopRegionSampler();
                Assert.False(sampler.TryCaptureBeforeReveal(region, 20, 20, out _));
                window.Hide();
                Assert.True(sampler.TryCaptureBeforeReveal(region, 20, 20, out var pixels));
                Assert.Equal(1600, pixels!.Length);
                Assert.Equal(255, pixels[3]);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnavailableOrBlackSnapshotKeepsAmbientGlassAndDoesNotStartWallpaperReplacement(bool black)
    {
        StaTest.Run(() =>
        {
            var window = new LauncherWindow();
            var surface = (LiquidGlassSurface)window.FindName("GlassSurface")!;
            surface.UseLaunchpadOptics = true;
            var scene = new GlassBackdropScene(); surface.ReadBackdropScene = () => scene;
            surface.CaptureRevealBackdrop = (_, width, height) => black ? new byte[width * height * 4] : null;
            try
            {
                window.ShowWithTransition(null);
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                Assert.NotNull(typeof(LiquidGlassSurface).GetField("_compatibilityBackdrop", flags)!.GetValue(surface));
                Assert.False((bool)typeof(LiquidGlassSurface).GetField("_compatibilityIsDesktopSnapshot", flags)!.GetValue(surface)!);
                Assert.False((bool)typeof(LiquidGlassSurface).GetField("_wallpaperPreparationPending", flags)!.GetValue(surface)!);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void ASceneChangeDiscardsThePreparedDesktopAndDeepSleepReacquiresIt()
    {
        StaTest.Run(() =>
        {
            var window = new LauncherWindow();
            var surface = (LiquidGlassSurface)window.FindName("GlassSurface")!;
            surface.UseLaunchpadOptics = true;
            var scene = new GlassBackdropScene(); surface.ReadBackdropScene = () => scene;
            var calls = 0;
            surface.CaptureRevealBackdrop = (_, width, height) =>
            {
                calls++; var pixels = new byte[width * height * 4];
                for (var i = 0; i < pixels.Length; i += 4) { pixels[i + 1] = 200; pixels[i + 3] = 255; }
                return pixels;
            };
            try
            {
                window.ShowWithTransition(null);
                scene = new(new GlassBackdropScene.SceneWindow((nint)500, 321, 0, 0, 10, 10));
                surface.RequestBackdropRefresh();
                Assert.Equal("desktop scene changed", surface.RevealMaterialInvalidationReason);
                window.Hide(); surface.SetPerformanceSleeping(true); surface.SetPerformanceSleeping(false);
                window.ShowWithTransition(null);
                Assert.Equal(2, calls);
                Assert.Null(surface.RevealMaterialInvalidationReason);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void HiddenLauncherUsesCurrentDesktopThroughTheProductionGlassFilterBeforeShow()
    {
        StaTest.Run(() =>
        {
            var window = new LauncherWindow();
            var surface = (LiquidGlassSurface)window.FindName("GlassSurface")!;
            surface.UseLaunchpadOptics = true;
            var scene = new GlassBackdropScene();
            surface.ReadBackdropScene = () => scene;
            var seam = typeof(LiquidGlassSurface).GetProperty("CaptureRevealBackdrop", BindingFlags.Instance | BindingFlags.NonPublic);
            try
            {
                Assert.NotNull(seam);
                var calls = 0;
                seam.SetValue(surface, (Func<PixelRect, int, int, byte[]?>)((region, width, height) =>
                {
                    Assert.False(window.IsVisible, "A desktop snapshot after Show would recapture the launcher itself.");
                    calls++;
                    var pixels = new byte[width * height * 4];
                    for (var i = 0; i < pixels.Length; i += 4)
                    { pixels[i] = 200; pixels[i + 1] = 100; pixels[i + 2] = 50; pixels[i + 3] = 255; }
                    return pixels;
                }));
                window.ShowWithTransition(null);
                Assert.Equal(1, calls);
                Assert.True((bool)typeof(LiquidGlassSurface).GetField("_compatibilityIsDesktopSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface)!, surface.RevealMaterialInvalidationReason);
                var bitmap = (BitmapSource?)typeof(LiquidGlassSurface).GetField("_compatibilityBackdrop", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface);
                Assert.NotNull(bitmap);
                var actual = new byte[4];
                bitmap.CopyPixels(new System.Windows.Int32Rect(bitmap.PixelWidth / 2, bitmap.PixelHeight / 2, 1, 1), actual, 4, 0);
                var expected = AndroidLiquidGlassRenderer.RenderTransitionBackdrop([200, 100, 50, 255], 1, 1,
                    new System.Windows.Size(200, 100), true, 1, launchpadOptics: true);
                Assert.Equal(expected, actual);
                Assert.NotEqual(new byte[] { 200, 100, 50, 255 }, actual);
                window.ShowWithTransition(null);
                Assert.Equal(1, calls);
            }
            finally { window.Close(); }
        });
    }
}
