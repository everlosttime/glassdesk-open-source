using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Input;
using GlassDesk.Dock;
using GlassDesk.Platform;
using GlassDesk.Platform.Displays;

namespace GlassDesk.Dock.Tests;

public sealed class ControlCenterLayoutTests
{
    [Theory]
    [InlineData(96)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(192)]
    public void InformationBarKeepsTheSameWidthAndRightEdgeAcrossStatusAndTrackChanges(uint dpi)
    {
        StaTest.Run(() =>
        {
            var bar = new StatusBarWindow();
            bar.BindPrimaryDisplay(new PrimaryDisplaySession(new("fixed", true, new(0, 0, 2560, 1440), new(0, 0, 2560, 1400), dpi)));
            try
            {
                bar.RefreshDisplayPlacement();
                var rect = typeof(StatusBarWindow).GetField("_restingRect", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var initial = (PixelRect)rect.GetValue(bar)!;
                var apply = typeof(StatusBarWindow).GetMethod("ApplySystemStatus", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var fan = typeof(StatusBarWindow).GetMethod("ApplyFanStatus", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var center = (ControlCenterWindow)typeof(StatusBarWindow).GetField("_controlCenter", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bar)!;
                var player = (MediaPlaybackControl)center.FindName("MediaPlayer");
                foreach (var percent in new[] { 1, 96, 100 })
                foreach (var title in new[] { "", "歌", "这是一首名字很长的正常歌曲，不能影响信息栏的宽度" })
                {
                    apply.Invoke(bar, [new SystemStatusSnapshot(percent, percent == 100, "Wi-Fi", percent, false)]);
                    ((TextBlock)bar.FindName("CpuText")).Text = percent + "%";
                    ((TextBlock)bar.FindName("MemoryText")).Text = percent + "%";
                    player.ApplySnapshot(new(title.Length > 0, title, "歌手", false, true, true, true, null));
                    fan.Invoke(bar, [percent == 100 ? 30000 : null]);
                    Assert.Equal(initial, (PixelRect)rect.GetValue(bar)!);
                }
            }
            finally { bar.Close(); }
        });
    }

    [Fact]
    public void DevicePickerDoesNotActivateOnNativeMouseClick()
    {
        StaTest.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var center = new ControlCenterWindow();
            center.BindPrimaryDisplay(new PrimaryDisplaySession(DisplayService.Primary()!));
            try
            {
                center.ShowNear(new PixelRect(1400, 40, 500, 54));
                Click(center, "OutputDeviceArrowButton");
                var picker = Picker(center);
                var result = SendMouseActivation(new System.Windows.Interop.WindowInteropHelper(picker).Handle,
                    0x21, new System.Windows.Interop.WindowInteropHelper(center).Handle, (nint)((0x201 << 16) | 1));
                Assert.Equal((nint)3, result); // MA_NOACTIVATE, without discarding the click.
                Assert.True(center.IsVisible);
            }
            finally { center.Close(); }
        });
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMouseActivation(nint hwnd, uint message, nint wParam, nint lParam);

    [Fact]
    public void InformationLabelsAreReadOnlyAndCompact()
    {
        StaTest.Run(() =>
        {
            var bar = new StatusBarWindow();
            bar.BindPrimaryDisplay(new PrimaryDisplaySession(DisplayService.Primary()!));
            try
            {
                bar.RefreshDisplayPlacement();
                foreach (var name in new[] { "NetworkText", "VolumeText", "MusicTitleText" })
                    Assert.Null(AncestorButton((DependencyObject)bar.FindName(name)));
                Assert.InRange(bar.Width, 260, 410);
                var content = (FrameworkElement)bar.Content;
                content.Measure(new Size(bar.Width, bar.Height));
                content.Arrange(new Rect(0, 0, bar.Width, bar.Height));
                var memory = (FrameworkElement)bar.FindName("MemoryPanel");
                var music = (FrameworkElement)bar.FindName("MusicSummary");
                var gap = music.TranslatePoint(new Point(0, 0), content).X
                    - memory.TranslatePoint(new Point(memory.ActualWidth, 0), content).X;
                Assert.InRange(gap, 7.99, 8.01);
            }
            finally { bar.Close(); }
        });
    }

    [Theory]
    [InlineData("WifiIconBubble", "WifiStateText", "WifiArrowGlyph")]
    [InlineData("BluetoothIconBubble", "BluetoothStateText", "BluetoothArrowGlyph")]
    [InlineData("OutputDeviceNameText", "OutputDeviceArrowGlyph", "OutputDeviceArrowGlyph")]
    public void DeviceEntryHasOneButtonForIconLabelAndArrow(string iconName, string labelName, string arrowName)
    {
        StaTest.Run(() =>
        {
            var center = new ControlCenterWindow();
            try
            {
                var button = AncestorButton((DependencyObject)center.FindName(iconName));
                Assert.NotNull(button);
                Assert.Same(button, AncestorButton((DependencyObject)center.FindName(labelName)));
                Assert.Same(button, AncestorButton((DependencyObject)center.FindName(arrowName)));
            }
            finally { center.Close(); }
        });
    }

    [Fact]
    public void OutputRowClickWorksWithoutKeyboardFocusAndRestoresOriginalOutput()
    {
        if (Environment.GetEnvironmentVariable("GLASSDESK_AUDIO_SWITCH_ACCEPTANCE") != "1") return;
        StaTest.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var original = Assert.Single(AudioOutputController.GetDevices(), device => device.IsDefault);
            var alternate = AudioOutputController.GetDevices().First(device => !device.IsDefault);
            var center = new ControlCenterWindow();
            center.BindPrimaryDisplay(new PrimaryDisplaySession(DisplayService.Primary()!));
            try
            {
                center.ShowNear(new PixelRect(1400, 40, 500, 54));
                Click(center, "OutputDeviceArrowButton");
                var list = (ListBox)Picker(center).FindName("SelectorList");
                PumpUntil(() => list.Items.Cast<object>().Any(item =>
                    item.GetType().GetProperty("Value")!.GetValue(item) is AudioOutputDevice device && device.Id == alternate.Id));
                var item = list.Items.Cast<object>().Single(item =>
                    item.GetType().GetProperty("Value")!.GetValue(item) is AudioOutputDevice device && device.Id == alternate.Id);
                list.UpdateLayout();
                var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(item);
                row.Focusable = false; // WS_EX_NOACTIVATE cannot give the row keyboard focus.
                row.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                    { RoutedEvent = Mouse.PreviewMouseDownEvent });
                PumpUntil(() => AudioOutputController.GetDevices().Any(device => device.Id == alternate.Id && device.IsDefault));
                Assert.True(center.IsVisible);
                Assert.True(Picker(center).IsVisible);
            }
            finally
            {
                center.Close();
                Assert.True(AudioOutputController.SetDefault(original.Id), AudioOutputController.LastDiagnostic);
            }
        });
    }

    private static void PumpUntil(Func<bool> ready)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!ready() && DateTime.UtcNow < until)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(10);
        }
        Assert.True(ready(), "Timed out waiting for the real output/list to update.");
    }

    private static Button? AncestorButton(DependencyObject child)
    {
        for (DependencyObject? item = child; item is not null; item = VisualTreeHelper.GetParent(item) ?? LogicalTreeHelper.GetParent(item))
            if (item is Button button) return button;
        return null;
    }

    [Theory]
    [InlineData("WifiButton", "WifiArrowGlyph")]
    [InlineData("BluetoothButton", "BluetoothArrowGlyph")]
    [InlineData("OutputDeviceArrowButton", "OutputDeviceArrowGlyph")]
    public void EachArrowOpensAndClosesItsListAndReversesDirection(string buttonName, string glyphName)
    {
        StaTest.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var center = new ControlCenterWindow();
            center.BindPrimaryDisplay(new PrimaryDisplaySession(DisplayService.Primary()!));
            try
            {
                center.ShowNear(new PixelRect(1400, 40, 500, 54));
                var button = Assert.IsType<Button>(center.FindName(buttonName));
                var glyph = Assert.IsType<TextBlock>(center.FindName(glyphName));
                Assert.Equal("\uE70D", glyph.Text);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var picker = Picker(center);
                Assert.True(picker.IsVisible);
                Assert.Equal("\uE70E", glyph.Text);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(picker.IsVisible);
                Assert.Equal("\uE70D", glyph.Text);
            }
            finally { center.Close(); }
        });
    }

    [Fact]
    public void SwitchingListsResetsTheOtherArrowAndHidingClosesTheChild()
    {
        StaTest.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var center = new ControlCenterWindow();
            center.BindPrimaryDisplay(new PrimaryDisplaySession(DisplayService.Primary()!));
            try
            {
                center.ShowNear(new PixelRect(1400, 40, 500, 54));
                Click(center, "WifiButton");
                Click(center, "BluetoothButton");
                Assert.Equal("\uE70D", ((TextBlock)center.FindName("WifiArrowGlyph")).Text);
                Assert.Equal("\uE70E", ((TextBlock)center.FindName("BluetoothArrowGlyph")).Text);
                Assert.True(Picker(center).IsVisible);
                center.Hide();
                Assert.False(Picker(center).IsVisible);
                Assert.Equal("\uE70D", ((TextBlock)center.FindName("BluetoothArrowGlyph")).Text);
                center.Show();
                Assert.False(Picker(center).IsVisible);
            }
            finally { center.Close(); }
        });
    }

    [Fact]
    public void TrackSummaryUpdatesInTheBarAndPlayerLivesBelowVolume()
    {
        StaTest.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var bar = new StatusBarWindow();
            try
            {
                var title = Assert.IsType<TextBlock>(bar.FindName("MusicTitleText"));
                var center = (ControlCenterWindow)typeof(StatusBarWindow).GetField("_controlCenter",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bar)!;
                var player = center.FindName("MediaPlayer") as FrameworkElement;
                Assert.NotNull(player);
                var apply = player.GetType().GetMethod("ApplySnapshot");
                Assert.NotNull(apply);
                apply.Invoke(player, [new MediaSessionSnapshot(true, "测试歌曲", "歌手", true, true, true, true, null)]);
                Assert.Equal("测试歌曲", title.Text);
                apply.Invoke(player, [new MediaSessionSnapshot(false, "", "", false, false, false, false, null)]);
                Assert.Equal("暂无媒体", title.Text);
                Assert.True(Grid.GetRow(player) > Grid.GetRow((UIElement)center.FindName("VolumePanel")));
                Assert.Null(center.FindName("ResizeButton"));
            }
            finally { bar.Close(); }
        });
    }

    private static Window Picker(ControlCenterWindow center)
    {
        var field = typeof(ControlCenterWindow).GetField("_devicePicker", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsAssignableFrom<Window>(field.GetValue(center));
    }

    private static void Click(ControlCenterWindow center, string name)
        => Assert.IsType<Button>(center.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
}
