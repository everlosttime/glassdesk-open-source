using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Serialization;
using GlassDesk.Core.Storage;
using GlassDesk.Dock;
using GlassDesk.Settings;
using GlassDesk.Platform.Displays;
using GlassDesk.Platform;
using GlassDesk.UI.Controls;
using GlassDesk.UI.Settings;

namespace GlassDesk.Dock.Tests;

public class StatusBarTests
{
    private static readonly DateTime T0 = new(2026, 10, 18, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void OldConfigurationEnablesStatusBarAndExplicitFalseSurvivesCloneAndReload()
    {
        var property = typeof(DockSettings).GetProperty("ShowStatusBar");
        Assert.NotNull(property);
        var settings = JsonSerializer.Deserialize("{\"dock\":{\"autoHide\":true}}", GlassDeskJsonContext.Default.GlassDeskSettings)!;
        settings.Normalize();
        Assert.Equal(true, property.GetValue(settings.Dock));
        property.SetValue(settings.Dock, false);
        var clone = SettingsPageEditor.Clone(settings);
        Assert.Equal(false, property.GetValue(clone.Dock));
        var json = JsonSerializer.Serialize(clone, GlassDeskJsonContext.Default.GlassDeskSettings);
        Assert.Equal(false, property.GetValue(JsonSerializer.Deserialize(json, GlassDeskJsonContext.Default.GlassDeskSettings)!.Dock));
        Assert.Equal(true, property.GetValue(SettingsPageEditor.ResetPage(clone, "Dock").Dock));
    }

    [Fact]
    public void AutoHideReentryCancelsTheSharedLeaveDelay()
    {
        var policy = CreatePolicy();
        var settings = new DockSettings { AutoHide = true, AutoHideLeaveDelayMs = 300 };
        Assert.Equal(AutoHideState.Shown, Update(policy, settings, 0, pointer: true));
        Assert.Equal(AutoHideState.WaitingToHide, Update(policy, settings, 10));
        Assert.Equal(AutoHideState.Shown, Update(policy, settings, 290, pointer: true));
        Assert.Equal(AutoHideState.WaitingToHide, Update(policy, settings, 300));
        Assert.Equal(AutoHideState.WaitingToHide, Update(policy, settings, 599));
        Assert.Equal(AutoHideState.Hidden, Update(policy, settings, 600));
    }

    [Fact]
    public void CoverageDoesNotRevealAgainForAPointerThatWasAlreadyInside()
    {
        var policy = CreatePolicy();
        var settings = new DockSettings();
        Assert.Equal(AutoHideState.Shown, Update(policy, settings, 0, pointer: true));
        Assert.Equal(AutoHideState.Hidden, Update(policy, settings, 10, pointer: true, covered: true));
        foreach (var time in new[] { 110, 210, 310, 410 })
            Assert.Equal(AutoHideState.Hidden, Update(policy, settings, time, pointer: true, covered: true));
        Assert.Equal(AutoHideState.Hidden, Update(policy, settings, 510, covered: true));
        Assert.Equal(AutoHideState.Shown, Update(policy, settings, 610, pointer: true, covered: true));
    }

    [Fact]
    public void ClosingLauncherUnderCoverageDoesNotBounceBackForAStationaryPointer()
    {
        var policy = CreatePolicy();
        var settings = new DockSettings();
        Assert.Equal(AutoHideState.Shown, Update(policy, settings, 0, pointer: true, covered: true, launcher: true));
        Assert.Equal(AutoHideState.Hidden, Update(policy, settings, 100, pointer: true, covered: true));
        Assert.Equal(AutoHideState.Hidden, Update(policy, settings, 200, pointer: true, covered: true));
    }

    [Fact]
    public void ExplicitHoverRevealSurvivesAMomentaryUncoveredObservation()
    {
        var policy = CreatePolicy();
        var settings = new DockSettings();
        Assert.Equal(AutoHideState.Hidden, Update(policy, settings, 0, covered: true));
        Assert.Equal(AutoHideState.Shown, Update(policy, settings, 100, covered: true, pointer: true));
        Assert.Equal(AutoHideState.Shown, Update(policy, settings, 200, pointer: true));
        Assert.Equal(AutoHideState.Shown, Update(policy, settings, 300, covered: true, pointer: true));
    }

    [Fact]
    public void CoverageRequiresLocalPointerIntentAndRevealLockWins()
    {
        var policy = CreatePolicy();
        var settings = new DockSettings();
        Assert.Equal(AutoHideState.Hidden, Update(policy, settings, 0, covered: true));
        Assert.Equal(AutoHideState.Shown, Update(policy, settings, 100, covered: true, pointer: true));
        Assert.Equal(AutoHideState.WaitingToHide, Update(policy, settings, 200, covered: true));
        Assert.Equal(AutoHideState.Hidden, Update(policy, settings, 500, covered: true));
        Assert.Equal(AutoHideState.Hidden, Update(policy, settings, 600, covered: true, pointer: true, locked: true));
        Assert.Equal(AutoHideState.Shown, Update(policy, settings, 700, covered: true, launcher: true, locked: true));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void DisabledOrSleepingWinsOverLauncherAndPointer(bool enabled, bool sleeping)
    {
        var policy = CreatePolicy();
        Assert.Equal(AutoHideState.Hidden, Update(policy, new DockSettings(), 0, pointer: true,
            launcher: true, enabled: enabled, sleeping: sleeping));
        Assert.Equal(AutoHideState.Shown, Update(policy, new DockSettings(), 1));
    }

    [Fact]
    public void FullscreenMatchesCoverageAndConstantModeReturnsWhenUncovered()
    {
        var policy = CreatePolicy();
        Assert.Equal(AutoHideState.Hidden, Update(policy, new DockSettings(), 0, fullscreen: true));
        Assert.Equal(AutoHideState.Shown, Update(policy, new DockSettings(), 1));
    }

    [Fact]
    public void SettingsSwitchImmediatelyClosesAllStatusPopoversAndPersistsBothChoices()
    {
        StaTest.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var root = Path.Combine(Path.GetTempPath(), "GlassDesk.StatusToggle", Guid.NewGuid().ToString("N"));
            var data = new GlassDeskDataStore(new AppPaths(root));
            data.LoadAll();
            var host = DockSettingsHost.Initialize(Dispatcher.CurrentDispatcher);
            host.Update(data.Settings.Current.Dock);
            var settings = new SettingsWindow();
            var bar = new StatusBarWindow { IsLauncherActive = () => true };
            bar.BindPrimaryDisplay(new PrimaryDisplaySession(DisplayService.Primary()!));
            bar.AttachSettings(host);
            settings.AttachDataStore(data, null);
            try
            {
                bar.Start();
                ((Button)bar.FindName("ControlCenterArrowButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var center = (ControlCenterWindow)typeof(StatusBarWindow).GetField("_controlCenter", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(bar)!;
                ((Button)center.FindName("OutputDeviceArrowButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var picker = (Window)typeof(ControlCenterWindow).GetField("_devicePicker", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(center)!;
                Assert.True(center.IsVisible && picker.IsVisible);
                var check = (CheckBox)settings.FindName("ShowStatusBarCheckBox");
                check.IsChecked = false;
                Assert.False(bar.IsVisible || center.IsVisible || picker.IsVisible);
                Assert.False(bar.IsPerformanceCaptureRequested);
                var reload = new SettingsStore(data.Settings.FilePath);
                reload.Load(); Assert.False(reload.Current.Dock.ShowStatusBar);
                check.IsChecked = true;
                Assert.True(host.Settings.ShowStatusBar);
                Assert.True(bar.IsVisible);
                Assert.False(center.IsVisible || picker.IsVisible);
                reload.Load(); Assert.True(reload.Current.Dock.ShowStatusBar);
            }
            finally
            {
                bar.Close(); settings.Close();
                Directory.Delete(root, true);
            }
        });
    }

    [Fact]
    public void SettingsCheckboxPersistsAndOtherDockEditsPreserveTheChoice()
    {
        StaTest.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "GlassDesk.StatusBarTests", Guid.NewGuid().ToString("N"));
            var data = new GlassDeskDataStore(new AppPaths(root));
            data.LoadAll();
            var window = new SettingsWindow();
            try
            {
                window.AttachDataStore(data, null);
                var check = Assert.IsType<CheckBox>(window.FindName("ShowStatusBarCheckBox"));
                Assert.True(check.IsChecked);
                check.IsChecked = false;
                Assert.IsType<CheckBox>(window.FindName("ShowThisPcEntryCheckBox")).IsChecked = false;
                var reload = new SettingsStore(data.Settings.FilePath);
                reload.Load();
                Assert.Equal(false, typeof(DockSettings).GetProperty("ShowStatusBar")!.GetValue(reload.Current.Dock));
            }
            finally { window.Close(); if (Directory.Exists(root)) Directory.Delete(root, true); }
        });
    }

    private static object CreatePolicy()
    {
        var type = typeof(DockWindow).Assembly.GetType("GlassDesk.Dock.StatusBarVisibilityController");
        Assert.NotNull(type);
        return Activator.CreateInstance(type)!;
    }

    [Fact]
    public void StatusWindowStopsTimersWhenDisabledSleepingOrDisplayUnavailableAndDoesNotResurrect()
    {
        StaTest.Run(() =>
        {
            var type = typeof(DockWindow).Assembly.GetType("GlassDesk.Dock.StatusBarWindow");
            Assert.NotNull(type);
            var window = Assert.IsAssignableFrom<GlassWindow>(Activator.CreateInstance(type));
            var settings = new DockSettingsHost(Dispatcher.CurrentDispatcher);
            window.BindPrimaryDisplay(new PrimaryDisplaySession(DisplayService.Primary()!));
            type.GetMethod("AttachSettings")!.Invoke(window, [settings]);
            try
            {
                type.GetMethod("Start")!.Invoke(window, null);
                type.GetMethod("SetPerformanceSleeping")!.Invoke(window, [true]);
                Assert.False(window.IsVisible);
                foreach (var name in new[] { "_clockTimer", "_visibilityTimer" })
                    Assert.False(((DispatcherTimer)type.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!).IsEnabled);
                settings.Update(DisabledSettings());
                type.GetMethod("SetPerformanceSleeping")!.Invoke(window, [false]);
                Assert.False(window.IsVisible);
                type.GetMethod("StopForPrimaryDisplayChange")!.Invoke(window, null);
                settings.Update(new DockSettings());
                Assert.False(window.IsVisible);
            }
            finally { window.Close(); }
        });
    }

    private static DockSettings DisabledSettings()
    {
        var settings = new DockSettings();
        typeof(DockSettings).GetProperty("ShowStatusBar")!.SetValue(settings, false);
        return settings;
    }

    [Theory]
    [InlineData(96, 18)]
    [InlineData(120, 22)]
    [InlineData(144, 27)]
    [InlineData(192, 36)]
    public void PlacementUsesPhysicalPixelsAtEachDpiAndFanDoesNotResizeTheBar(uint dpi, int gap)
    {
        StaTest.Run(() =>
        {
            var display = new DisplayInfo("status-layout", true, new(-2560, 0, 2560, 1440), new(-2560, 40, 2560, 1400), dpi);
            var window = new StatusBarWindow();
            window.BindPrimaryDisplay(new PrimaryDisplaySession(display));
            try
            {
                var settings = new GlassDesk.UI.Settings.DockSettingsHost(System.Windows.Threading.Dispatcher.CurrentDispatcher);
                settings.Update(new() { StatusBarSecondaryItems = ["cpu", "memory", "fan"] });
                window.AttachSettings(settings);
                window.RefreshDisplayPlacement();
                var rect = (PixelRect)typeof(StatusBarWindow).GetField("_restingRect", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
                Assert.Equal(display.ToPixels(window.Width), rect.Width);
                Assert.InRange(window.Width, 260, 410);
                var compactWidth = window.Width;
                Assert.Equal(-gap, rect.Right);
                Assert.Equal(40 + gap, rect.Y);
                typeof(StatusBarWindow).GetMethod("ApplyFanStatus", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [30000]);
                Assert.Equal(compactWidth, window.Width);
                var panel = (FrameworkElement)window.FindName("FanPanel")!;
                Assert.Equal(Visibility.Visible, panel.Visibility);
                var content = (FrameworkElement)window.Content;
                content.Measure(new Size(window.Width, window.Height));
                content.Arrange(new Rect(0, 0, window.Width, window.Height));
                var metrics = (FrameworkElement)window.FindName("StatusMetrics")!;
                var fan = (FrameworkElement)window.FindName("FanText")!;
                var end = fan.TranslatePoint(new Point(fan.ActualWidth, fan.ActualHeight), metrics);
                Assert.True(end.X <= metrics.ActualWidth && end.Y <= metrics.ActualHeight, $"Fan is clipped at {end}");
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void NarrowWorkAreaClampsTheWindowAndScalesTheContent()
    {
        StaTest.Run(() =>
        {
            var window = new StatusBarWindow();
            window.BindPrimaryDisplay(new PrimaryDisplaySession(new("small", true, new(0, 0, 300, 400), new(0, 0, 300, 360), 96)));
            try
            {
                window.RefreshDisplayPlacement();
                Assert.Equal(300, window.Width);
                var content = (FrameworkElement)window.Content;
                content.Measure(new Size(window.Width, window.Height));
                content.Arrange(new Rect(0, 0, window.Width, window.Height));
                var text = (FrameworkElement)window.FindName("VolumeText")!;
                var end = text.TranslatePoint(new Point(text.ActualWidth, text.ActualHeight), content);
                Assert.True(end.X <= 300 && end.Y <= 54);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void UnavailableFanSensorsAreHiddenAndNativeReadingsStayWithinPercentBounds()
    {
        var status = SystemStatusReader.Read();
        var performance = SystemStatusReader.ReadPerformance();
        foreach (var value in new[] { status.BatteryPercent, status.VolumePercent, performance.CpuPercent, performance.MemoryPercent })
            if (value is { } available) Assert.InRange(available, 0, 100);
        Assert.False(FanSpeedReader.IsPlausibleRpm(double.NaN));
        Assert.False(FanSpeedReader.IsPlausibleRpm(0));
        Assert.False(FanSpeedReader.IsPlausibleRpm(100000));
        StaTest.Run(() =>
        {
            var window = new StatusBarWindow();
            try
            {
                var apply = typeof(StatusBarWindow).GetMethod("ApplyFanStatus", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var content = (FrameworkElement)window.FindName("StatusContent")!;
                content.Measure(new Size(double.PositiveInfinity, 44));
                var compactWidth = content.DesiredSize.Width;
                apply.Invoke(window, [1200]);
                apply.Invoke(window, [null]);
                Assert.Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("FanPanel")!).Visibility);
                content.Measure(new Size(double.PositiveInfinity, 44));
                Assert.Equal(compactWidth, content.DesiredSize.Width);
            }
            finally { window.Close(); }
        });
    }

    private static AutoHideState Update(object policy, DockSettings settings, int milliseconds,
        bool pointer = false, bool covered = false, bool fullscreen = false,
        bool launcher = false, bool locked = false, bool enabled = true, bool sleeping = false)
        => (AutoHideState)policy.GetType().GetMethod("Update")!.Invoke(policy,
            [settings, T0.AddMilliseconds(milliseconds), pointer, covered, fullscreen, launcher, locked, enabled, sleeping, false])!;
}
