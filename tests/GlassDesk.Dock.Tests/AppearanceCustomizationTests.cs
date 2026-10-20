using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using GlassDesk.Core.Models;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Storage;
using GlassDesk.AppServices;
using GlassDesk.Settings;
using GlassDesk.UI.Controls;
using GlassDesk.UI.Settings;
using GlassDesk.UI.Theming;

namespace GlassDesk.Dock.Tests;

public sealed class AppearanceCustomizationTests
{
    [Fact]
    public void OpenPlacementMenuHasAnOpaqueBodyAndMatchesTheSelectionWidth()
    {
        StaTest.Run(() =>
        {
            using var theme = ThemeService.Initialize(Dispatcher.CurrentDispatcher);
            var window = new SettingsWindow();
            window.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/GlassDesk.UI;component/Theming/LiquidGlassTheme.xaml")
            });
            try
            {
                window.Show(); window.UpdateLayout();
                var combo = Assert.IsType<ComboBox>(window.FindName("DockPlacementCombo"));
                combo.BringIntoView(); window.UpdateLayout();
                combo.IsDropDownOpen = true;
                window.UpdateLayout();
                var popup = Assert.IsType<Popup>(combo.Template.FindName("PART_Popup", combo));
                var body = Assert.IsType<PhoneBorder>(popup.Child);
                body.UpdateLayout();
                Assert.InRange(Assert.IsType<SolidColorBrush>(body.Background).Color.A, 250, 255);
                Assert.True(body.ActualWidth >= combo.ActualWidth - 1,
                    $"Menu {body.ActualWidth}, selection {combo.ActualWidth}");
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void SizeSliderUpdatesLiveDockAndPersistsAcrossOtherDockChanges()
    {
        StaTest.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "GlassDesk-customize-" + Guid.NewGuid().ToString("N"));
            var data = new GlassDeskDataStore(new AppPaths(root));
            data.LoadAll();
            var host = DockSettingsHost.Initialize(Dispatcher.CurrentDispatcher);
            host.Update(data.Settings.Current.Dock);
            var settings = new SettingsWindow();
            var dock = new DockWindow();
            using var services = new AppServiceHost(data);
            try
            {
                settings.AttachDataStore(data, null);
                dock.AttachServices(services, host, () => { });
                dock.Show(); dock.UpdateLayout();
                var originalHeight = dock.ActualHeight;
                var slider = Assert.IsType<Slider>(settings.FindName("DockScaleSlider"));
                slider.Value = .75;
                dock.UpdateLayout();
                Assert.Equal(36, Assert.IsType<Button>(dock.FindName("LauncherButton")).Width);
                Assert.InRange(dock.ActualHeight / originalHeight, .72, .78);
                Assert.IsType<Slider>(settings.FindName("DockOffsetXSlider")).Value = 20;
                var reload = new SettingsStore(data.Paths.SettingsFile); reload.Load();
                Assert.Equal(.75, typeof(DockSettings).GetProperty("SizeScale")?.GetValue(reload.Current.Dock));
                Assert.Equal(20, reload.Current.Dock.OffsetX);
                Assert.Equal(36, Assert.IsType<Button>(dock.FindName("LauncherButton")).Width);
            }
            finally
            {
                dock.Close(); settings.Close();
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        });
    }

    [Fact]
    public void MaterialSelectorUpdatesThemeAndSavesFrostedChoice()
    {
        StaTest.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "GlassDesk-customize-" + Guid.NewGuid().ToString("N"));
            using var theme = ThemeService.Initialize(Dispatcher.CurrentDispatcher);
            var data = new GlassDeskDataStore(new AppPaths(root)); data.LoadAll();
            var window = new SettingsWindow();
            try
            {
                window.AttachDataStore(data, null);
                Assert.IsType<ComboBox>(window.FindName("AppearanceMaterialCombo")).SelectedIndex = 1;
                Assert.Equal((GlassMaterialMode)2, theme.Appearance.GlassMaterial);
                var reload = new SettingsStore(data.Paths.SettingsFile); reload.Load();
                Assert.Equal((GlassMaterialMode)2, reload.Current.Appearance.GlassMaterial);
                Assert.IsType<CheckBox>(window.FindName("ReduceMotionCheck")).IsChecked = true;
                Assert.Equal((GlassMaterialMode)2, theme.Appearance.GlassMaterial);
            }
            finally { window.Close(); if (Directory.Exists(root)) Directory.Delete(root, true); }
        });
    }

    [Fact]
    public void FrostedMaterialBlursFineDetailWithoutIntroducingColorSeparation()
    {
        const int width = 128, height = 96;
        var source = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = (y * width + x) * 4;
            source[i] = source[i + 1] = source[i + 2] = x % 8 < 4 ? (byte)0 : (byte)255;
            source[i + 3] = 255;
        }
        var frame = LiquidGlassFrameComposer.Compose(new LiquidGlassOptics(), source, width, height,
            new Size(width, height), 24, true, 1, 0, LiquidGlassDebugView.None,
            LiquidGlassSamplingParameters.Default, LiquidGlassWeightParameters.Default,
            new Rect(8, 8, 112, 80), LiquidGlassLodParameters.Default, LiquidGlassFinishingParameters.Default,
            LiquidGlassBrdfParameters.Default, LiquidGlassDispersionParameters.Default,
            material: (GlassMaterialMode)2, backgroundClarity: 1);
        var a = (48 * width + 64) * 4; var b = a + 4 * 4;
        Assert.InRange(Math.Abs(frame[a] - frame[b]), 0, 25);
        for (var x = 12; x < 116; x++)
        {
            var i = (48 * width + x) * 4;
            Assert.Equal(frame[i], frame[i + 1]); Assert.Equal(frame[i + 1], frame[i + 2]);
            Assert.Equal(255, frame[i + 3]);
        }
        Assert.Equal(0, frame[3]);
    }
}
