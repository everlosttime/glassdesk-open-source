using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GlassDesk.AppServices;
using GlassDesk.Core.Models;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Storage;
using GlassDesk.Settings;
using GlassDesk.UI.Settings;

namespace GlassDesk.Dock.Tests;

public sealed class DockIconSpacingTests
{
    [Fact]
    public void SliderAppliesSpacingImmediatelyAndPreservesItAcrossOtherEditsAndReload()
    {
        StaTest.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "GlassDesk-spacing-" + Guid.NewGuid().ToString("N"));
            var window = new SettingsWindow();
            try
            {
                var data = new GlassDeskDataStore(new AppPaths(root));
                data.LoadAll();
                var host = DockSettingsHost.Initialize(Dispatcher.CurrentDispatcher);
                window.AttachDataStore(data, null);
                var slider = Assert.IsType<Slider>(window.FindName("DockSpacingSlider"));
                Assert.Equal(8, slider.Value);
                slider.Value = 16;
                Assert.Equal(16, host.Settings.IconSpacing);
                Assert.Contains("16", Assert.IsType<TextBlock>(window.FindName("DockSpacingReadout")).Text);
                Assert.IsType<Slider>(window.FindName("DockOffsetXSlider")).Value = 25;
                var loaded = new GlassDeskDataStore(data.Paths);
                loaded.LoadAll();
                Assert.Equal(16, loaded.Settings.Current.Dock.IconSpacing);
                Assert.Equal(25, loaded.Settings.Current.Dock.OffsetX);
                window.AttachDataStore(loaded, null);
                Assert.Equal(16, slider.Value);

                foreach (var spacing in new[] { 0d, 24d })
                {
                    slider.Value = spacing;
                    var reloaded = new SettingsStore(data.Paths.SettingsFile);
                    reloaded.Load();
                    Assert.Equal(spacing, reloaded.Current.Dock.IconSpacing);
                    Assert.Equal(spacing, host.Settings.IconSpacing);
                }
            }
            finally
            {
                window.Close();
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        });
    }

    [Theory]
    [InlineData(DockPlacement.BottomCenter, 1)]
    [InlineData(DockPlacement.TopCenter, 1.5)]
    [InlineData(DockPlacement.LeftCenter, 1)]
    [InlineData(DockPlacement.RightCenter, 1.5)]
    public void LiveSpacingChangesMoveBuiltinAndApplicationIconsAlongDockAxis(DockPlacement placement, double scale)
    {
        StaTest.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "GlassDesk-spacing-layout-" + Guid.NewGuid().ToString("N"));
            var dock = new DockWindow();
            try
            {
                var data = new GlassDeskDataStore(new AppPaths(root));
                data.LoadAll();
                foreach (var id in new[] { "app-a", "app-b", "app-c" })
                {
                    Assert.Equal(AddEntryResult.Added, data.Library.Add(new AppEntry
                    {
                        Id = id, DisplayName = id, Kind = AppItemKind.Program,
                        Target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                        Arguments = id,
                    }));
                    data.Layouts.Dock.PinnedItemIds.Add(id);
                }
                using var services = new AppServiceHost(data);
                var host = new DockSettingsHost(dock.Dispatcher);
                dock.AttachServices(services, host, () => { });
                dock.Show();
                var row = Assert.IsType<StackPanel>(dock.FindName("DockEntryRow"));
                var strip = Assert.IsType<DockTileStrip>(Assert.IsType<ContentControl>(dock.FindName("TileStripHost")).Content);
                var vertical = placement is DockPlacement.LeftCenter or DockPlacement.RightCenter;
                foreach (var spacing in new[] { 0d, 16d, 24d })
                {
                    host.Update(new DockSettings { Placement = placement, SizeScale = scale, IconSpacing = spacing });
                    dock.UpdateLayout();
                    var first = Assert.IsType<Button>(dock.FindName("LauncherButton"));
                    var second = Assert.IsType<Button>(dock.FindName("ThisPcButton"));
                    var firstPoint = first.TranslatePoint(new Point(), row);
                    var secondPoint = second.TranslatePoint(new Point(), row);
                    var pitch = (48 + spacing) * scale;
                    Assert.Equal(pitch, vertical ? secondPoint.Y - firstPoint.Y : secondPoint.X - firstPoint.X, 5);
                    Assert.Equal(pitch, strip.PaintedGeometry[1].Center - strip.PaintedGeometry[0].Center, 5);
                    Assert.Equal(pitch, strip.PaintedGeometry[2].Center - strip.PaintedGeometry[1].Center, 5);
                    Assert.Equal(new[] { "app-a", "app-b", "app-c" }, data.Layouts.Dock.PinnedItemIds);
                }
            }
            finally
            {
                dock.Close();
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        });
    }
}
