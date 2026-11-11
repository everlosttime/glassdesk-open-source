using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using GlassDesk.Core.Models;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Storage;
using GlassDesk.AppServices;
using GlassDesk.Launcher;
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
    public void MaterialSelectorsUpdateOnlyTheirOwnSettingsAndPersist()
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
                var launcher = Assert.IsType<ComboBox>(window.FindName("LauncherMaterialCombo"));
                var dock = Assert.IsType<ComboBox>(window.FindName("DockMaterialCombo"));
                var statusBar = Assert.IsType<ComboBox>(window.FindName("StatusBarMaterialCombo"));
                var controlCenter = Assert.IsType<ComboBox>(window.FindName("ControlCenterMaterialCombo"));
                Assert.Equal(1, controlCenter.SelectedIndex);

                var panels = new GlassWindow[]
                {
                    new LauncherWindow(), new DockWindow(), new StatusBarWindow(), new ControlCenterWindow(), new DateNotificationWindow(),
                };
                try
                {
                    var material = typeof(LiquidGlassSurface).GetProperty("Material",
                        BindingFlags.Instance | BindingFlags.NonPublic)!;
                    var picker = (DevicePickerWindow)typeof(ControlCenterWindow)
                        .GetField("_devicePicker", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panels[3])!;
                    var pickerSurface = picker.FindName("GlassSurface");
                    var selectors = new[] { launcher, dock, statusBar, controlCenter };
                    var modes = new[] { GlassMaterialMode.AndroidLiquidGlass, GlassMaterialMode.FrostedGlass, GlassMaterialMode.PlainGlass };
                    var expected = new[] { modes[0], modes[0], modes[0], modes[1], modes[1] };
                    for (var target = 0; target < selectors.Length; target++)
                    {
                        for (var index = 0; index < modes.Length; index++)
                        {
                            selectors[target].SelectedIndex = index;
                            expected[target] = modes[index];
                            expected[4] = expected[3];
                            Assert.Equal(expected, panels.Select(panel =>
                                (GlassMaterialMode)material.GetValue(panel.FindName("GlassSurface"))!));
                            Assert.Equal(expected[3], material.GetValue(pickerSurface));
                        }
                        selectors[target].SelectedIndex = target == 3 ? 1 : 0;
                        expected[target] = target == 3 ? modes[1] : modes[0];
                        expected[4] = expected[3];
                    }
                    var clarity = typeof(LiquidGlassSurface).GetProperty("BackgroundClarity",
                        BindingFlags.Instance | BindingFlags.NonPublic)!;
                    Assert.IsType<Slider>(window.FindName("StatusBarTransparencySlider")).Value = .62;
                    Assert.IsType<Slider>(window.FindName("ControlCenterTransparencySlider")).Value = .48;
                    Assert.Equal(new[] { .2, .9, .62, .48, .48 }, panels.Select(panel =>
                        (double)clarity.GetValue(panel.FindName("GlassSurface"))!));
                    Assert.Equal(.48, clarity.GetValue(pickerSurface));
                }
                finally
                {
                    foreach (var panel in panels) panel.Close();
                }

                launcher.SelectedIndex = 1;
                Assert.Equal(GlassMaterialMode.FrostedGlass, theme.Appearance.LauncherGlassMaterial);
                Assert.Equal(GlassMaterialMode.AndroidLiquidGlass, theme.Appearance.GetGlassMaterial(GlassMaterialTarget.Dock));
                Assert.Equal(GlassMaterialMode.AndroidLiquidGlass, theme.Appearance.GetGlassMaterial(GlassMaterialTarget.StatusBar));
                Assert.Equal(GlassMaterialMode.FrostedGlass, theme.Appearance.ControlCenterGlassMaterial);

                dock.SelectedIndex = 2;
                statusBar.SelectedIndex = 1;
                controlCenter.SelectedIndex = 0;
                Assert.IsType<CheckBox>(window.FindName("ReduceMotionCheck")).IsChecked = true;

                var reload = new SettingsStore(data.Paths.SettingsFile); reload.Load();
                Assert.Equal(GlassMaterialMode.FrostedGlass, reload.Current.Appearance.LauncherGlassMaterial);
                Assert.Equal(GlassMaterialMode.PlainGlass, reload.Current.Appearance.DockGlassMaterial);
                Assert.Equal(GlassMaterialMode.FrostedGlass, reload.Current.Appearance.StatusBarGlassMaterial);
                Assert.Equal(GlassMaterialMode.AndroidLiquidGlass, reload.Current.Appearance.ControlCenterGlassMaterial);
                Assert.Equal(.62, reload.Current.Appearance.StatusBarGlassTransparency);
                Assert.Equal(.48, reload.Current.Appearance.ControlCenterGlassTransparency);
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
