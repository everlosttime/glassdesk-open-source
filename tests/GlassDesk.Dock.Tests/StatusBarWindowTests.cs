using System.IO;
using System.Windows.Controls;
using GlassDesk.Platform;

namespace GlassDesk.Dock.Tests;

public sealed class StatusBarWindowTests
{
    [Fact]
    public void SleepingOrDisablingStatusBarStopsMediaAndPendingReadsCannotRevealIt()
    {
        StaTest.Run(() =>
        {
            var window = new StatusBarWindow();
            var settings = new GlassDesk.UI.Settings.DockSettingsHost(System.Windows.Threading.Dispatcher.CurrentDispatcher);
            window.BindPrimaryDisplay(new GlassDesk.Platform.Displays.PrimaryDisplaySession(
                GlassDesk.Platform.Displays.DisplayService.Primary()!));
            try
            {
                var attach = typeof(StatusBarWindow).GetMethod("AttachSettings");
                Assert.NotNull(attach);
                attach.Invoke(window, [settings]);
                typeof(StatusBarWindow).GetMethod("Start")!.Invoke(window, null);
                var center = (ControlCenterWindow)typeof(StatusBarWindow).GetField("_controlCenter",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
                var media = (MediaPlaybackControl)center.FindName("MediaPlayer");
                var timer = (System.Windows.Threading.DispatcherTimer)typeof(MediaPlaybackControl).GetField("_timer",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(media)!;
                Assert.True(timer.IsEnabled);
                window.SetPerformanceSleeping(true);
                Assert.False(timer.IsEnabled);
                var disabled = new GlassDesk.Core.Models.Settings.DockSettings { ShowStatusBar = false };
                settings.Update(disabled);
                window.SetPerformanceSleeping(false);
                var frame = new System.Windows.Threading.DispatcherFrame();
                var deadline = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                deadline.Tick += (_, _) => { deadline.Stop(); frame.Continue = false; };
                deadline.Start();
                System.Windows.Threading.Dispatcher.PushFrame(frame);
                Assert.False(media.IsVisible);
                Assert.False(timer.IsEnabled);
                Assert.False(window.IsVisible);
                Assert.False(window.IsPerformanceCaptureRequested);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void SystemSnapshotReturnsBoundedValues()
    {
        var snapshot = SystemStatusReader.Read();
        if (snapshot.BatteryPercent is { } battery) Assert.InRange(battery, 0, 100);
        if (snapshot.VolumePercent is { } volume) Assert.InRange(volume, 0, 100);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.NetworkLabel));

        var first = SystemStatusReader.ReadPerformance();
        Thread.Sleep(20);
        var performance = SystemStatusReader.ReadPerformance();
        if (first.CpuPercent is { } firstCpu) Assert.InRange(firstCpu, 0, 100);
        if (performance.CpuPercent is { } cpu) Assert.InRange(cpu, 0, 100);
        if (performance.MemoryPercent is { } memory) Assert.InRange(memory, 0, 100);
        var fanRpm = FanSpeedReader.ReadRpm();
        if (fanRpm is { } rpm) Assert.True(FanSpeedReader.IsPlausibleRpm(rpm));
        Assert.False(FanSpeedReader.IsPlausibleRpm(0));
        Assert.False(FanSpeedReader.IsPlausibleRpm(99));
        Assert.False(FanSpeedReader.IsPlausibleRpm(30_001));
    }

    [Fact]
    public void FanHelperSnapshotRequiresFreshPlausibleData()
    {
        var now = DateTimeOffset.UtcNow;
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, $$"""{"version":1,"timestampUtc":"{{now:o}}","rpm":12300}""");
            Assert.Equal(12_300, FanSpeedReader.ReadHelperRpm(path, now));

            Assert.Null(FanSpeedReader.ReadHelperRpm(path, now.AddMinutes(1)));
            File.WriteAllText(path, $$"""{"version":1,"timestampUtc":"{{now:o}}","rpm":90000}""");
            Assert.Null(FanSpeedReader.ReadHelperRpm(path, now));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void StatusWindowBuildsSevenCoreFieldsAndOptionalFanField()
    {
        StaTest.Run(() =>
        {
            var window = new StatusBarWindow();
            try
            {
                Assert.IsType<TextBlock>(window.FindName("TimeText"));
                Assert.IsType<TextBlock>(window.FindName("DateText"));
                Assert.IsType<TextBlock>(window.FindName("BatteryText"));
                Assert.IsType<TextBlock>(window.FindName("NetworkText"));
                Assert.IsType<TextBlock>(window.FindName("VolumeText"));
                Assert.IsType<TextBlock>(window.FindName("CpuText"));
                Assert.IsType<TextBlock>(window.FindName("MemoryText"));
                Assert.IsType<TextBlock>(window.FindName("FanText"));
                Assert.IsType<StackPanel>(window.FindName("FanPanel"));
                Assert.Equal(System.Windows.Visibility.Collapsed,
                    ((StackPanel)window.FindName("FanPanel")).Visibility);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void GlassCompanionWindowsBuildTheirInteractiveControls()
    {
        StaTest.Run(() =>
        {
            var controlCenter = new ControlCenterWindow();
            var datePanel = new DateNotificationWindow();
            var media = new MediaCapsuleWindow();
            try
            {
                Assert.IsType<Slider>(controlCenter.FindName("VolumeSlider"));
                Assert.IsType<Button>(controlCenter.FindName("WifiButton"));
                Assert.IsType<Button>(controlCenter.FindName("BluetoothButton"));
                Assert.IsType<TextBlock>(controlCenter.FindName("WifiArrowGlyph"));
                Assert.IsType<TextBlock>(controlCenter.FindName("BluetoothArrowGlyph"));
                Assert.IsType<Button>(controlCenter.FindName("OutputDeviceArrowButton"));
                Assert.IsType<MediaPlaybackControl>(controlCenter.FindName("MediaPlayer"));
                var calendar = Assert.IsType<Calendar>(datePanel.FindName("MonthCalendar"));
                var calendarCard = Assert.IsType<Border>(calendar.Parent);
                var calendarBackground = Assert.IsType<System.Windows.Media.SolidColorBrush>(calendarCard.Background);
                Assert.Equal(System.Windows.SystemColors.WindowColor, calendarBackground.Color);
                Assert.IsType<TextBlock>(media.FindName("TitleText"));
                Assert.IsType<Button>(media.FindName("PlayPauseButton"));
                Assert.IsType<Button>(media.FindName("SideRevealButton"));
                Assert.IsType<Button>(media.FindName("ManualCollapseButton"));
            }
            finally
            {
                controlCenter.Close();
                datePanel.Close();
                media.Close();
            }
        });
    }

    [Fact]
    public async Task DevicePickersReturnSafeSnapshots()
    {
        var networks = await WifiNetworkController.ScanAndGetAvailableNetworksAsync()
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(networks.Count, networks.Select(network => network.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var network in networks)
        {
            Assert.False(string.IsNullOrWhiteSpace(network.Name));
            Assert.InRange(network.SignalQuality, 0, 100);
        }

        var devices = await BluetoothDeviceReader.GetPairedDevicesAsync()
            .WaitAsync(TimeSpan.FromSeconds(10));
        foreach (var device in devices)
        {
            Assert.False(string.IsNullOrWhiteSpace(device.Name));
            Assert.True(device.IsPaired);
        }
    }

    [Fact]
    public async Task MediaReaderSupportsSystemSessionsOrActiveAudioFallback()
    {
        using var controller = new MediaSessionController();
        var media = await controller.ReadAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(string.IsNullOrWhiteSpace(controller.LastDiagnostic));
        if (!media.Available) return;
        Assert.False(string.IsNullOrWhiteSpace(media.Title));
        Assert.False(string.IsNullOrWhiteSpace(media.Artist));
        Assert.True(media.CanTogglePlayPause);
    }
}
