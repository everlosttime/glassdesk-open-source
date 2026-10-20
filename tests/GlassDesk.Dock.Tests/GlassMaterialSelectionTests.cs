using System.IO;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Interop;
using System.Windows.Media;
using GlassDesk.Core.Storage;
using GlassDesk.Settings;
using GlassDesk.UI.Controls;
using GlassDesk.UI.Theming;
using GlassDesk.Core.Models;

namespace GlassDesk.Dock.Tests;

public sealed class GlassMaterialSelectionTests
{
    [Fact]
    public void SimplifiedAppearanceKeepsDarkLiquidGlassAndSavesRemainingControls()
    {
        StaTest.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "GlassDesk-material-" + Guid.NewGuid().ToString("N"));
            SettingsWindow? window = null;
            using var theme = ThemeService.Initialize(Dispatcher.CurrentDispatcher);
            try
            {
                var data = new GlassDeskDataStore(new AppPaths(root));
                data.LoadAll();
                theme.Update(data.Settings.Current.Appearance);
                window = new SettingsWindow();
                window.AttachDataStore(data, null);
                new WindowInteropHelper(window).EnsureHandle();
                window.Show();
                window.UpdateLayout();
                Assert.Null(window.FindName("ReduceTransparencyCheck"));
                Assert.Equal(GlassSurfaceMode.SelfDrawn, window.SurfaceMode);
                Assert.Equal(255, Assert.IsType<SolidColorBrush>(window.PanelBackgroundBrush).Color.A);
                Assert.Same(window.PanelBackgroundBrush, Assert.IsType<Border>(window.Content).Background);
                Assert.False(theme.Appearance.ReduceTransparency);
                Assert.Null(window.FindName("AppearanceThemeCombo"));
                Assert.Equal(0, Assert.IsType<ComboBox>(window.FindName("AppearanceMaterialCombo")).SelectedIndex);
                Assert.Null(window.FindName("GlassDebugViewCombo"));
                Assert.Null(window.FindName("GlassPerformanceOverlayCheck"));
                Assert.Null(window.FindName("GlassFaceScaleSlider"));
                var dockTransparency = Assert.IsType<Slider>(window.FindName("DockTransparencySlider"));
                var launcherTransparency = Assert.IsType<Slider>(window.FindName("LauncherTransparencySlider"));
                Assert.Equal(.9, dockTransparency.Value);
                Assert.Equal(.2, launcherTransparency.Value);
                dockTransparency.Value = .8;
                Assert.Equal(.2, theme.Appearance.LauncherGlassTransparency);
                launcherTransparency.Value = .3;
                Assert.IsType<CheckBox>(window.FindName("ReduceMotionCheck")).IsChecked = true;
                Assert.True(theme.IsDark);
                Assert.Equal(GlassMaterialMode.AndroidLiquidGlass, theme.Appearance.GlassMaterial);
                Assert.Equal(.8, theme.Appearance.DockGlassTransparency);
                Assert.Equal(.3, theme.Appearance.LauncherGlassTransparency);
                Assert.True(theme.Appearance.ReduceMotion);
                Assert.Equal(GlassSurfaceMode.SelfDrawn, window.SurfaceMode);
                Assert.Equal(255, Assert.IsType<SolidColorBrush>(window.PanelBackgroundBrush).Color.A);
                var reloaded = new GlassDeskDataStore(new AppPaths(root));
                reloaded.LoadAll();
                Assert.Equal(ThemeMode.Dark, reloaded.Settings.Current.Appearance.Theme);
                Assert.Equal(GlassMaterialMode.AndroidLiquidGlass, reloaded.Settings.Current.Appearance.GlassMaterial);
                Assert.Equal(.8, reloaded.Settings.Current.Appearance.DockGlassTransparency);
                Assert.Equal(.3, reloaded.Settings.Current.Appearance.LauncherGlassTransparency);
                Assert.True(reloaded.Settings.Current.Appearance.ReduceMotion);
                Assert.False(new LiquidGlassSurface().IsHitTestVisible);
            }
            finally
            {
                window?.Close();
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        });
    }
}
