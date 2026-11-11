using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GlassDesk.Core.Models;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Storage;
using GlassDesk.Platform.Displays;
using GlassDesk.Settings;
using GlassDesk.UI.Controls;
using GlassDesk.UI.Theming;

namespace GlassDesk.Dock.Tests;

public sealed class PlainGlassTests
{
    [Fact]
    public void PlainWindowSkipsShadowBuffersAndMaterialSwitchDisposesThePreviousNativeShadow()
    {
        StaTest.Run(() =>
        {
            using var theme = ThemeService.Initialize(Dispatcher.CurrentDispatcher);
            theme.Update(new AppearanceSettings { GlassMaterial = GlassMaterialMode.PlainGlass });
            var window = new TestGlassWindow { Width = 240, Height = 100, ShowActivated = false };
            GlassShadow? Shadow() => (GlassShadow?)typeof(GlassWindow).GetField("_shadow",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window);
            try
            {
                window.Show(); window.UpdateLayout(); Pump();
                Assert.Null(Shadow());
                theme.Update(new AppearanceSettings { GlassMaterial = GlassMaterialMode.FrostedGlass });
                Pump();
                var normalShadow = Assert.IsType<GlassShadow>(Shadow());
                Assert.NotEqual(nint.Zero, normalShadow.Handle);
                Assert.True(normalShadow.IsVisible);
                theme.Update(new AppearanceSettings { GlassMaterial = GlassMaterialMode.PlainGlass });
                Pump();
                Assert.Null(Shadow());
                Assert.Equal(nint.Zero, normalShadow.Handle);
                theme.Update(new AppearanceSettings());
                Pump();
                Assert.True(Assert.IsType<GlassShadow>(Shadow()).IsVisible);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void AnUnusedTextureLayerAllocatesItsLensOnlyWhenARealMaterialNeedsIt()
    {
        StaTest.Run(() =>
        {
            var layer = new TransitionGlassLayer();
            Assert.False(layer.UsesGpuLens);
            var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 20, 20, 20, 255 }, 4);
            layer.SetBackdrop(bitmap);
            layer.SetPanel(new Rect(0, 0, 100, 100), new Rect(0, 0, 100, 100), 20, 1);
            if (RenderCapability.IsPixelShaderVersionSupported(3, 0) && !GlassDesk.UI.BackdropRecovery.RequiresBasicRendering)
                Assert.True(layer.UsesGpuLens);
            layer.SetPerformanceSleeping(true);
            Assert.False(layer.UsesGpuLens);
        });
    }

    [Fact]
    public void AHiddenPlainSurfaceRetainsNoShaderOrCaptureResourcesBeforeItsFirstReveal()
    {
        StaTest.Run(() =>
        {
            using var theme = ThemeService.Initialize(Dispatcher.CurrentDispatcher);
            theme.Update(new AppearanceSettings { GlassMaterial = GlassMaterialMode.PlainGlass });
            using var surface = new LiquidGlassSurface();
            var state = Inspect(surface);
            Assert.False(state.GetProperty("gpuLensActive").GetBoolean());
            Assert.False(state.GetProperty("watchdogEnabled").GetBoolean());
            Assert.Equal(0, state.GetProperty("retainedPixelBytes").GetInt64());
        });
    }

    [Fact]
    public void PlainFolderGlassUsesUniformTransparencyWithoutAHighlightLayer()
    {
        StaTest.Run(() =>
        {
            using var theme = ThemeService.Initialize(Dispatcher.CurrentDispatcher);
            theme.Update(new AppearanceSettings { GlassMaterial = GlassMaterialMode.PlainGlass, LauncherGlassTransparency = .4 });
            var pixels = Render(new SharedGlassLens { Radius = 20, Highlight = 1 });
            Assert.Equal(153, pixels[(50 * 200 + 100) * 4 + 3]);
            Assert.Equal(pixels.AsSpan((50 * 200 + 100) * 4, 4).ToArray(), pixels.AsSpan((30 * 200 + 35) * 4, 4).ToArray());
        });
    }

    [Theory]
    [InlineData(false, 0, 255)]
    [InlineData(false, .4, 153)]
    [InlineData(false, 1, 0)]
    [InlineData(true, 0, 255)]
    [InlineData(true, .4, 153)]
    [InlineData(true, 1, 0)]
    public void PlainGlassDrawsOnlyUniformBodyAlphaUsingTheCorrectTransparency(bool launcher, double transparency, int alpha)
    {
        StaTest.Run(() =>
        {
            using var theme = ThemeService.Initialize(Dispatcher.CurrentDispatcher);
            theme.Update(new AppearanceSettings
            {
                GlassMaterial = GlassMaterialMode.PlainGlass,
                DockGlassTransparency = launcher ? .12 : transparency,
                LauncherGlassTransparency = launcher ? transparency : .12,
            });
            using var surface = new LiquidGlassSurface { UseLauncherTransparency = launcher, TransparentWhileLoading = true };
            surface.SetPanelShape(new Rect(0, 0, 200, 100), 20);
            var pixels = Render(surface);
            Assert.Equal(alpha, pixels[(50 * 200 + 100) * 4 + 3]);
            Assert.Equal(pixels.AsSpan((50 * 200 + 100) * 4, 4).ToArray(), pixels.AsSpan((30 * 200 + 35) * 4, 4).ToArray());
            Assert.Equal(0, pixels[3]);
            Assert.True(surface.IsCurrentRevealMaterialReady);
            var state = Inspect(surface);
            Assert.Equal(0, state.GetProperty("retainedPixelBytes").GetInt64());
            Assert.False(state.GetProperty("gpuLensActive").GetBoolean());
        });
    }

    [Theory]
    [InlineData(GlassMaterialTarget.Dock)]
    [InlineData(GlassMaterialTarget.StatusBar)]
    public void FullyTransparentPlainBarKeepsInputAlphaInsideTheRoundedPanel(GlassMaterialTarget target)
    {
        StaTest.Run(() =>
        {
            using var theme = ThemeService.Initialize(Dispatcher.CurrentDispatcher);
            theme.Update(new AppearanceSettings
            {
                GlassMaterial = GlassMaterialMode.PlainGlass,
                DockGlassTransparency = 1,
                StatusBarGlassTransparency = 1,
            });
            using var surface = new LiquidGlassSurface();
            var window = new TestGlassWindow { MaterialTarget = target, Content = surface };
            try
            {
                surface.SetPanelShape(new Rect(0, 0, 200, 100), 20);
                var pixels = Render(surface);
                Assert.Equal(1, pixels[(50 * 200 + 100) * 4 + 3]);
                Assert.Equal(0, pixels[3]);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void PlainGlassNeverQueriesOrCapturesBackdropAcrossRevealAnimationAndSleep()
    {
        StaTest.Run(() =>
        {
            using var theme = ThemeService.Initialize(Dispatcher.CurrentDispatcher);
            theme.Update(new AppearanceSettings { GlassMaterial = GlassMaterialMode.PlainGlass });
            using var surface = new LiquidGlassSurface { UseFrostedMaterial = true, BackgroundClarityOverride = .35 };
            var sceneQueries = 0;
            var captures = 0;
            surface.ReadBackdropScene = () => { ++sceneQueries; return new(); };
            surface.CaptureRevealBackdrop = (_, _, _) => { ++captures; return null; };
            var window = new TestGlassWindow { Width = 200, Height = 100, Content = surface, ShowActivated = false };
            try
            {
                window.Show();
                window.UpdateLayout();
                for (var i = 0; i < 3; i++)
                {
                    surface.RequestBackdropRefresh(revealing: true);
                    surface.SetContainerAnimationActive(true);
                    surface.SetContainerTransitionBounds(new Rect(0, 0, 200, 100));
                    surface.SetTransitionFrame(new Rect(0, 0, 200, 100), 20,
                        new RectangleGeometry(new Rect(0, 0, 200, 100), 20, 20), .4, 4);
                    surface.PrepareRevealMaterial(new PixelRect(0, 0, 200, 100), new PixelRect(0, 0, 200, 100), 96);
                    surface.SetPerformanceSleeping(true);
                    surface.SetPerformanceSleeping(false);
                    theme.Refresh();
                    Pump();
                    Assert.True(surface.IsCurrentRevealMaterialReady);
                }
                Assert.Equal(0, sceneQueries);
                Assert.Equal(0, captures);
                Assert.Equal(GlassSurfaceMode.SelfDrawn, window.SurfaceMode);
                var state = Inspect(surface);
                Assert.False(state.GetProperty("renderClockAttached").GetBoolean());
                Assert.False(state.GetProperty("watchdogEnabled").GetBoolean());
                Assert.False(state.GetProperty("refreshQueued").GetBoolean());
                Assert.False(state.GetProperty("renderQueued").GetBoolean());
                Assert.False(state.GetProperty("opticsBusy").GetBoolean());
                Assert.Equal(0, state.GetProperty("retainedPixelBytes").GetInt64());
                Assert.Equal(0, state.GetProperty("preparedDesktopSnapshots").GetInt32());
                foreach (var name in new[] { "live", "wallpaper", "desktopOnly" })
                {
                    var capture = state.GetProperty("capture").GetProperty(name);
                    Assert.Equal(0, capture.GetProperty("captureAcquisitions").GetInt64());
                    Assert.Equal(0, capture.GetProperty("activeCaptureOwner").GetInt32());
                    Assert.False(capture.GetProperty("workerAlive").GetBoolean());
                }
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void SwitchingToPlainRetiresPixelsAndSwitchingBackRestartsTheMaterialClock()
    {
        StaTest.Run(() =>
        {
            using var theme = ThemeService.Initialize(Dispatcher.CurrentDispatcher);
            using var surface = new LiquidGlassSurface();
            var window = new TestGlassWindow { Width = 200, Height = 100, Content = surface, ShowActivated = false };
            try
            {
                window.Show(); window.UpdateLayout(); Pump();
                theme.Update(new AppearanceSettings { GlassMaterial = GlassMaterialMode.PlainGlass });
                Pump();
                var plain = Inspect(surface);
                Assert.Equal(0, plain.GetProperty("retainedPixelBytes").GetInt64());
                Assert.False(plain.GetProperty("renderClockAttached").GetBoolean());
                Assert.False(plain.GetProperty("watchdogEnabled").GetBoolean());
                theme.Update(new AppearanceSettings { GlassMaterial = GlassMaterialMode.FrostedGlass });
                Assert.True(Inspect(surface).GetProperty("watchdogEnabled").GetBoolean());
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void SettingsOffersPlainGlassAndPersistsSelectionAndIndependentTransparency()
    {
        StaTest.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "GlassDesk-plain-" + Guid.NewGuid().ToString("N"));
            using var theme = ThemeService.Initialize(Dispatcher.CurrentDispatcher);
            SettingsWindow? window = null;
            try
            {
                var data = new GlassDeskDataStore(new AppPaths(root)); data.LoadAll();
                theme.Update(data.Settings.Current.Appearance);
                window = new SettingsWindow(); window.AttachDataStore(data, null);
                window.Show(); window.UpdateLayout();
                var combo = Assert.IsType<ComboBox>(window.FindName("DockMaterialCombo"));
                Assert.Equal(3, combo.Items.Count);
                combo.SelectedIndex = 2;
                Assert.Equal(GlassMaterialMode.PlainGlass, theme.Appearance.DockGlassMaterial);
                Assert.IsType<Slider>(window.FindName("DockTransparencySlider")).Value = .71;
                Assert.IsType<Slider>(window.FindName("LauncherTransparencySlider")).Value = .36;
                var reload = new GlassDeskDataStore(new AppPaths(root)); reload.LoadAll();
                Assert.Equal(GlassMaterialMode.PlainGlass, reload.Settings.Current.Appearance.DockGlassMaterial);
                Assert.Equal(.71, reload.Settings.Current.Appearance.DockGlassTransparency);
                Assert.Equal(.36, reload.Settings.Current.Appearance.LauncherGlassTransparency);
                Assert.Equal(255, Assert.IsType<SolidColorBrush>(window.PanelBackgroundBrush).Color.A);
            }
            finally { window?.Close(); if (Directory.Exists(root)) Directory.Delete(root, true); }
        });
    }

    private sealed class TestGlassWindow : GlassWindow;
    private static JsonElement Inspect(LiquidGlassSurface surface) => JsonSerializer.SerializeToElement(surface.InspectPerformance());
    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static byte[] Render(FrameworkElement surface)
    {
        surface.Measure(new Size(200, 100)); surface.Arrange(new Rect(0, 0, 200, 100)); surface.UpdateLayout();
        var image = new RenderTargetBitmap(200, 100, 96, 96, PixelFormats.Pbgra32); image.Render(surface);
        var pixels = new byte[200 * 100 * 4]; image.CopyPixels(pixels, 800, 0); return pixels;
    }
}
