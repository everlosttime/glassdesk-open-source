using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using GlassDesk.Platform;

namespace GlassDesk.Dock.Tests;

public sealed class ApplicationMixerTests
{
    [Fact]
    public void NativeSessionReadUsesTheManager2Interface()
    {
        var contract = typeof(ApplicationVolumeController).GetNestedType("IAudioSessionManager2", BindingFlags.NonPublic);
        Assert.NotNull(contract);
        Assert.Equal(new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), contract.GUID);
        var snapshot = ApplicationVolumeController.Read();
        var devices = AudioOutputController.GetDevices();
        Assert.True(devices.Any(device => device.IsDefault) == snapshot.Available,
            $"Session availability={snapshot.Available}; endpoints={devices.Count}; {AudioOutputController.LastDiagnostic}");
        Assert.NotNull(snapshot.Applications);
    }

    private static ApplicationVolume Sample => new("test", "测试应用", null, "missing-endpoint", ["missing-session"], 65, false);

    [Fact]
    public void MissingSessionsAndInvalidLevelsCannotChangeOtherApplications()
    {
        Assert.False(ApplicationVolumeController.SetVolume(Sample, double.NaN));
        Assert.False(ApplicationVolumeController.SetVolume(Sample, double.PositiveInfinity));
        Assert.False(ApplicationVolumeController.SetVolume(Sample, 30));
        Assert.False(ApplicationVolumeController.SetMuted(Sample, true));
    }

    [Fact]
    public void ExpandingMixerLeavesTheVolumeAndGameButtonsInSeparateRows()
    {
        StaTest.Run(() =>
        {
            var window = new ControlCenterWindow();
            try
            {
                var originalHeight = window.Height;
                var toggle = window.GetType().GetMethod("SetMixerExpanded", BindingFlags.Instance | BindingFlags.NonPublic)!;
                toggle.Invoke(window, [true]);
                Assert.Equal(Visibility.Visible, ((Border)window.FindName("ApplicationMixerPanel")).Visibility);
                Assert.True(window.Height > originalHeight);
                Assert.Equal(4, Grid.GetRow((Button)window.FindName("GameSleepButton")));
                toggle.Invoke(window, [false]);
                Assert.Equal(originalHeight, window.Height);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void ApplicationRowsShowIndependentSliderAndMuteControl()
    {
        StaTest.Run(() =>
        {
            var window = new ControlCenterWindow();
            try
            {
                var create = window.GetType().GetMethod("CreateMixerRow", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var row = create.Invoke(window, [Sample])!;
                var root = (Grid)row.GetType().GetProperty("Root")!.GetValue(row)!;
                Assert.Equal(65, Assert.Single(root.Children.OfType<Slider>()).Value);
                Assert.Single(root.Children.OfType<Button>());
                Assert.Contains(root.Children.OfType<TextBlock>(), label => label.Text == "测试应用");
            }
            finally { window.Close(); }
        });
    }
}
