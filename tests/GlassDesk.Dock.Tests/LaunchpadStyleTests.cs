using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GlassDesk.Core.Storage;
using GlassDesk.Launcher;
using GlassDesk.Settings;
using GlassDesk.UI.Controls;
using GlassDesk.UI.Theming;

namespace GlassDesk.Dock.Tests;

public sealed class LaunchpadStyleTests
{
    [Fact]
    public void StyleChoiceUpdatesBothGlyphsPersistsAndRollsBackOnSaveFailure()
    {
        StaTest.Run(() =>
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "GlassDesk-launchpad-style-" + Guid.NewGuid().ToString("N")));
            using var theme = ThemeService.Initialize(Dispatcher.CurrentDispatcher);
            var data = new GlassDeskDataStore(new AppPaths(root)); data.LoadAll();
            var settings = new SettingsWindow(); var dock = new DockWindow(); var launcher = new LauncherWindow();
            try
            {
                settings.AttachDataStore(data, null);
                _ = new WindowInteropHelper(dock).EnsureHandle();
                _ = new WindowInteropHelper(launcher).EnsureHandle();
                FlushBindings();
                var choice = Assert.IsType<ComboBox>(settings.FindName("LaunchpadIconStyleCombo"));
                var glyph = (LaunchpadGlyph)((PhoneBorder)dock.FindName("LaunchpadTile")).Child;
                var transitionGlyph = (LaunchpadGlyph)launcher.FindName("CollapsedGlyph");
                Assert.Equal(0, choice.SelectedIndex);
                AssertGlyph(glyph, filled: true);

                ((Slider)settings.FindName("DockBuiltinIconBrightnessSlider")).Value = .35;
                ((Slider)settings.FindName("LauncherTextBrightnessSlider")).Value = .65;
                choice.SelectedIndex = 1; FlushBindings();
                AssertGlyph(glyph, filled: false);
                Assert.False((bool)typeof(LaunchpadGlyph).GetProperty("IsFilled")!.GetValue(transitionGlyph)!);
                ((CheckBox)settings.FindName("ReduceMotionCheck")).IsChecked = true;
                var reloaded = new GlassDeskDataStore(data.Paths); reloaded.LoadAll();
                settings.AttachDataStore(reloaded, null);
                Assert.Equal(1, choice.SelectedIndex);
                Assert.Equal(.35, ((Slider)settings.FindName("DockBuiltinIconBrightnessSlider")).Value);
                Assert.Equal(.65, ((Slider)settings.FindName("LauncherTextBrightnessSlider")).Value);

                var before = File.ReadAllBytes(data.Paths.SettingsFile);
                File.SetAttributes(data.Paths.SettingsFile, FileAttributes.ReadOnly);
                choice.SelectedIndex = 0; theme.Refresh(); FlushBindings();
                Assert.Equal(1, choice.SelectedIndex);
                AssertGlyph(glyph, filled: false);
                Assert.Equal(before, File.ReadAllBytes(data.Paths.SettingsFile));
                File.SetAttributes(data.Paths.SettingsFile, FileAttributes.Normal);
                choice.SelectedIndex = 0; FlushBindings();
                AssertGlyph(glyph, filled: true);
                Assert.True((bool)typeof(LaunchpadGlyph).GetProperty("IsFilled")!.GetValue(transitionGlyph)!);

                var preview = Environment.GetEnvironmentVariable("GLASSDESK_LAUNCHPAD_STYLE_PREVIEW");
                if (!string.IsNullOrWhiteSpace(preview))
                {
                    Directory.CreateDirectory(preview);
                    ExportGlyph(glyph, Path.Combine(preview, "filled.png"));
                    choice.SelectedIndex = 1; FlushBindings();
                    ExportGlyph(glyph, Path.Combine(preview, "outline.png"));
                }
            }
            catch (Exception error) { throw new InvalidOperationException(error.ToString(), error); }
            finally
            {
                settings.Close(); dock.Close(); launcher.Close();
                if (File.Exists(data.Paths.SettingsFile)) File.SetAttributes(data.Paths.SettingsFile, FileAttributes.Normal);
                if (Path.GetDirectoryName(root) != Path.TrimEndingDirectorySeparator(Path.GetTempPath())) throw new IOException("Unexpected cleanup boundary.");
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        });
    }

    private static void FlushBindings() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.SystemIdle);

    private static void AssertGlyph(LaunchpadGlyph glyph, bool filled)
    {
        foreach (var scale in new[] { 1, 2 })
        {
            var bitmap = RenderGlyph(glyph, scale);
            var width = bitmap.PixelWidth;
            var pixels = new byte[width * bitmap.PixelHeight * 4]; bitmap.CopyPixels(pixels, width * 4, 0);
            for (var y = 0; y < 3; y++)
            for (var x = 0; x < 3; x++)
                Assert.Equal(filled ? 255 : 0, pixels[(((10 + y * 14) * scale) * width + (10 + x * 14) * scale) * 4 + 3]);
            Assert.Equal(255, pixels[((10 * scale) * width + 5 * scale) * 4 + 3]);
            Assert.Equal(0, pixels[((10 * scale) * width + 17 * scale) * 4 + 3]);
        }
    }

    private static RenderTargetBitmap RenderGlyph(LaunchpadGlyph glyph, int scale)
    {
        glyph.Measure(new Size(48, 48)); glyph.Arrange(new Rect(0, 0, 48, 48)); glyph.UpdateLayout();
        var bitmap = new RenderTargetBitmap(48 * scale, 48 * scale, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(glyph); return bitmap;
    }

    private static void ExportGlyph(LaunchpadGlyph glyph, string path)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(RenderGlyph(glyph, 2)));
        using var file = File.Create(path); encoder.Save(file);
    }
}
