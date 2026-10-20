using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GlassDesk.AppServices;
using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;
using GlassDesk.Launcher;
using GlassDesk.Settings;
using GlassDesk.UI.Controls;
using GlassDesk.UI.Theming;

namespace GlassDesk.Dock.Tests;

public sealed class MonochromeAppearanceTests
{
    [Theory]
    [InlineData(96)]
    [InlineData(192)]
    public void MonochromeLaunchpadSquaresHaveSolidCentersAndTransparentGaps(double dpi)
    {
        StaTest.Run(() =>
        {
            var glyph = new LaunchpadGlyph { Width = 48, Height = 48, OutlineBrush = Brushes.White };
            glyph.Measure(new Size(48, 48)); glyph.Arrange(new Rect(0, 0, 48, 48));
            var width = (int)(48 * dpi / 96);
            var bitmap = new RenderTargetBitmap(width, width, dpi, dpi, PixelFormats.Pbgra32); bitmap.Render(glyph);
            var pixels = new byte[width * width * 4]; bitmap.CopyPixels(pixels, width * 4, 0);
            for (var y = 0; y < 3; y++)
            for (var x = 0; x < 3; x++)
            {
                var pixelX = (int)((10 + x * 14) * dpi / 96);
                var pixelY = (int)((10 + y * 14) * dpi / 96);
                Assert.Equal(255, pixels[(pixelY * width + pixelX) * 4 + 3]);
            }
            var gapX = (int)(17 * dpi / 96); var gapY = (int)(10 * dpi / 96);
            Assert.Equal(0, pixels[(gapY * width + gapX) * 4 + 3]);
        });
    }

    [Fact]
    public void SlidersUpdateOnlyBuiltinIconsAndLauncherTextAndPersistAcrossOtherAppearanceChanges()
    {
        StaTest.Run(() =>
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "GlassDesk-monochrome-" + Guid.NewGuid().ToString("N")));
            using var theme = ThemeService.Initialize(Dispatcher.CurrentDispatcher);
            var data = new GlassDeskDataStore(new AppPaths(root)); data.LoadAll();
            data.Library.Add(new AppEntry { Id = "sample", Target = "missing", DisplayName = "测试应用", IsTargetMissing = true });
            data.Library.Save();
            Assert.True(data.Layouts.CommitLauncher(LauncherLayoutEditor.AddShortcuts(data.Layouts.Launcher, ["sample"], 35), data.Layouts.LauncherVersion));
            using var services = new AppServiceHost(data);
            var settings = new SettingsWindow(); var dock = new DockWindow(); var launcher = new LauncherWindow();
            try
            {
                launcher.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri("pack://application:,,,/GlassDesk.UI;component/Theming/LiquidGlassTheme.xaml") });
                settings.AttachDataStore(data, null); launcher.AttachServices(services, true);
                _ = new WindowInteropHelper(settings).EnsureHandle();
                _ = new WindowInteropHelper(dock).EnsureHandle();
                _ = new WindowInteropHelper(launcher).EnsureHandle();
                // Exercise the production renderer without showing a window on the user's desktop.
                launcher.PositionOnWorkingArea();
                typeof(LauncherWindow).GetMethod("RenderTiles", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .Invoke(launcher, [false, false]);
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.SystemIdle);
                var settingsColor = settings.Foreground;
                var dockColor = dock.Foreground;
                var panelColor = dock.PanelBackgroundBrush;
                var icons = Assert.IsType<Slider>(settings.FindName("DockBuiltinIconBrightnessSlider"));
                var text = Assert.IsType<Slider>(settings.FindName("LauncherTextBrightnessSlider"));
                icons.Value = 0; text.Value = .5;
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.SystemIdle);

                var glyph = Assert.IsType<LaunchpadGlyph>(Assert.IsType<PhoneBorder>(dock.FindName("LaunchpadTile")).Child);
                AssertColor(Colors.Black, glyph.OutlineBrush);
                AssertColor(Colors.Black, ((System.Windows.Shapes.Path)((Canvas)((Viewbox)dock.FindName("ThisPcTile")).Child).Children[0]).Stroke);
                foreach (System.Windows.Shapes.Path path in ((Canvas)((Viewbox)dock.FindName("DesktopIconsTile")).Child).Children)
                    AssertColor(Colors.Black, path.Stroke ?? path.Fill);
                var tiles = (UniformGrid)launcher.FindName("TileGrid");
                Assert.NotEmpty(tiles.Children.Cast<UIElement>());
                var tile = Assert.IsAssignableFrom<Button>(tiles.Children[0]);
                var title = (TextBlock)((StackPanel)tile.Content).Children[1];
                AssertColor(Color.FromRgb(128, 128, 128), title.Foreground);
                AssertColor(Color.FromRgb(128, 128, 128), ((TextBox)launcher.FindName("SearchBox")).Foreground);
                AssertColor(Color.FromRgb(128, 128, 128), ((TextBlock)launcher.FindName("GroupTitle")).Foreground);
                AssertColor(Color.FromRgb(128, 128, 128), ((TextBlock)launcher.FindName("PageText")).Foreground);
                AssertColor(Colors.Black, ((LaunchpadGlyph)launcher.FindName("CollapsedGlyph")).OutlineBrush);
                AssertColor(((SolidColorBrush)settingsColor).Color, settings.Foreground);
                AssertColor(((SolidColorBrush)dockColor).Color, dock.Foreground);
                Assert.Equal(((LinearGradientBrush)panelColor!).GradientStops.Select(stop => (stop.Offset, stop.Color)),
                    ((LinearGradientBrush)dock.PanelBackgroundBrush!).GradientStops.Select(stop => (stop.Offset, stop.Color)));

                ((ComboBox)settings.FindName("AppearanceMaterialCombo")).SelectedIndex = 1;
                ((Slider)settings.FindName("DockTransparencySlider")).Value = .64;
                var reloaded = new GlassDeskDataStore(data.Paths); reloaded.LoadAll();
                settings.AttachDataStore(reloaded, null);
                Assert.Equal(0, icons.Value); Assert.Equal(.5, text.Value);
                Assert.Equal(.64, reloaded.Settings.Current.Appearance.DockGlassTransparency);
                Assert.Equal(GlassMaterialMode.FrostedGlass, reloaded.Settings.Current.Appearance.GlassMaterial);
                text.Value = 0; icons.Value = 1;
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.SystemIdle);
                AssertColor(Colors.Black, title.Foreground); AssertColor(Colors.White, glyph.OutlineBrush);

                var previewRoot = Environment.GetEnvironmentVariable("GLASSDESK_MONOCHROME_PREVIEW");
                if (!string.IsNullOrWhiteSpace(previewRoot))
                {
                    Directory.CreateDirectory(previewRoot);
                    var row = (StackPanel)((Button)dock.FindName("LauncherButton")).Parent;
                    row.Measure(new Size(168, 64)); row.Arrange(new Rect(0, 0, 168, 64)); row.UpdateLayout();
                    ExportPreview(row, Color.FromRgb(38, 40, 46), Path.Combine(previewRoot, "dock-white.png"));
                    icons.Value = 0;
                    Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.SystemIdle);
                    ExportPreview(row, Color.FromRgb(236, 238, 242), Path.Combine(previewRoot, "dock-black.png"));

                    settings.Resources.MergedDictionaries.Add(new ResourceDictionary
                    { Source = new Uri("pack://application:,,,/GlassDesk.UI;component/Theming/LiquidGlassTheme.xaml") });
                    var content = (FrameworkElement)settings.Content;
                    content.Measure(new Size(760, 560)); content.Arrange(new Rect(0, 0, 760, 560)); content.UpdateLayout();
                    var scroll = (ScrollViewer)settings.FindName("SettingsScroll");
                    scroll.ScrollToVerticalOffset(230); content.UpdateLayout();
                    ExportPreview(content, Color.FromRgb(25, 27, 32), Path.Combine(previewRoot, "appearance-settings.png"));
                }
            }
            catch (Exception error) { throw new InvalidOperationException(error.ToString(), error); }
            finally
            {
                settings.Close(); dock.Close(); launcher.Close();
                if (Path.GetDirectoryName(root) != Path.TrimEndingDirectorySeparator(Path.GetTempPath())) throw new IOException("Unexpected cleanup boundary.");
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        });
    }

    [Fact]
    public void FailedBrightnessSaveRestoresControlsThemeAndStoredSettings()
    {
        StaTest.Run(() =>
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "GlassDesk-monochrome-rollback-" + Guid.NewGuid().ToString("N")));
            using var theme = ThemeService.Initialize(Dispatcher.CurrentDispatcher);
            var data = new GlassDeskDataStore(new AppPaths(root)); data.LoadAll(); data.Settings.Save();
            var before = File.ReadAllBytes(data.Paths.SettingsFile);
            var settings = new SettingsWindow();
            try
            {
                settings.AttachDataStore(data, null);
                theme.Update(data.Settings.Current.Appearance);
                File.SetAttributes(data.Paths.SettingsFile, FileAttributes.ReadOnly);
                var icons = Assert.IsType<Slider>(settings.FindName("DockBuiltinIconBrightnessSlider"));
                var text = Assert.IsType<Slider>(settings.FindName("LauncherTextBrightnessSlider"));
                icons.Value = 0; text.Value = .5;
                Assert.Equal(1, icons.Value); Assert.Equal(1, text.Value);
                Assert.Equal(1, theme.Appearance.DockBuiltinIconBrightness);
                Assert.Equal(1, theme.Appearance.LauncherTextBrightness);
                Assert.Contains("保存失败", ((TextBlock)settings.FindName("AppearanceStatus")).Text);
                Assert.Equal(before, File.ReadAllBytes(data.Paths.SettingsFile));
            }
            finally
            {
                settings.Close(); File.SetAttributes(data.Paths.SettingsFile, FileAttributes.Normal);
                if (Path.GetDirectoryName(root) != Path.TrimEndingDirectorySeparator(Path.GetTempPath())) throw new IOException("Unexpected cleanup boundary.");
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        });
    }

    private static void ExportPreview(FrameworkElement content, Color background, string path)
    {
        content.UpdateLayout();
        var size = content.RenderSize;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(background), null, new Rect(size));
            dc.DrawRectangle(new VisualBrush(content), null, new Rect(size));
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width * 2), (int)Math.Ceiling(size.Height * 2), 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }

    private static void AssertColor(Color expected, Brush? brush,
        [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(brush))] string? expression = null)
    {
        Assert.True(brush is SolidColorBrush, "Missing color on " + expression);
        Assert.Equal(expected, ((SolidColorBrush)brush).Color);
    }
}
