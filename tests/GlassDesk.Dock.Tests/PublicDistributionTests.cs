using GlassDesk.Launcher;
using GlassDesk.Settings;

namespace GlassDesk.Dock.Tests;

public sealed class PublicDistributionTests
{
    [Theory]
    [InlineData("StatusBarWindow")]
    [InlineData("ControlCenterWindow")]
    [InlineData("DateNotificationWindow")]
    [InlineData("DevicePickerWindow")]
    [InlineData("MediaCapsuleWindow")]
    [InlineData("MediaPlaybackControl")]
    public void PublicAssemblyDoesNotContainInformationBarWindows(string name)
        => Assert.Null(typeof(DockWindow).Assembly.GetType("GlassDesk.Dock." + name));

    [Fact]
    public void SettingsExposePublicFeaturesWithoutAnInformationBarPage()
    {
        StaTest.Run(() =>
        {
            var window = new SettingsWindow();
            try
            {
                Assert.DoesNotContain("信息栏", window.PlannedPages);
                Assert.Null(window.FindName("InformationBarSectionHeading"));
                Assert.Contains("Dock", window.PlannedPages);
                Assert.Contains("启动台", window.PlannedPages);
                Assert.Contains("关于", window.PlannedPages);
                Assert.NotNull(window.FindName("CloudDownloadButton"));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void PublicOnboardingDoesNotPresentAnInformationBar()
    {
        StaTest.Run(() =>
        {
            var store = new GlassDesk.Core.Storage.SettingsStore(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "GlassDesk-public-tour-" + Guid.NewGuid().ToString("N"), "settings.json"));
            var window = new OnboardingWindow(store);
            try { Assert.Null(window.FindName("MockInfoBar")); Assert.Null(window.FindName("MockControlCenter")); }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void EditingPublicDockSettingsPreservesReservedProfileValues()
    {
        StaTest.Run(() =>
        {
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "GlassDesk-public-profile-" + Guid.NewGuid().ToString("N"));
            var data = new GlassDesk.Core.Storage.GlassDeskDataStore(new GlassDesk.Core.Storage.AppPaths(root));
            data.LoadAll();
            data.Settings.Current.Dock.CalendarShowLunar = true;
            data.Settings.Current.Dock.ShowStatusBar = false;
            data.Settings.Current.Dock.StatusBarSizeScale = 1.25;
            var window = new SettingsWindow();
            try
            {
                window.AttachDataStore(data, null);
                ((System.Windows.Controls.Slider)window.FindName("DockScaleSlider")).Value = 1.1;
                Assert.Equal(1.1, data.Settings.Current.Dock.SizeScale);
                Assert.True(data.Settings.Current.Dock.CalendarShowLunar);
                Assert.False(data.Settings.Current.Dock.ShowStatusBar);
                Assert.Equal(1.25, data.Settings.Current.Dock.StatusBarSizeScale);
            }
            finally { window.Close(); System.IO.Directory.Delete(root, true); }
        });
    }
}
