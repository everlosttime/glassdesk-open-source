using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GlassDesk.Core.Models.Settings;
using GlassDesk.UI.Settings;

namespace GlassDesk.Dock.Tests;

public sealed class StatusBarSecondaryLayoutTests
{
    private static readonly string[] Keys = ["cpu", "memory", "fan", "mouseBattery", "headsetBattery", "keyboardBattery", "music"];
    private static readonly string[] Panels = ["CpuPanel", "MemoryPanel", "FanPanel", "MouseBatteryPanel", "HeadsetBatteryPanel", "KeyboardBatteryPanel", "MusicSummary"];

    [Fact]
    public void FanReadingCannotInsertAnUnselectedItem()
    {
        StaTest.Run(() =>
        {
            var bar = new StatusBarWindow();
            try
            {
                typeof(StatusBarWindow).GetMethod("ApplyFanStatus", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(bar, [30_000]);
                Assert.Equal(Visibility.Collapsed, ((FrameworkElement)bar.FindName("FanPanel")).Visibility);
            }
            finally { bar.Close(); }
        });
    }

    [Theory]
    [InlineData(.75)]
    [InlineData(1)]
    [InlineData(1.5)]
    public void EveryZeroToThreeItemSelectionFitsActualWpfLayout(double scale)
    {
        StaTest.Run(() =>
        {
            var host = new DockSettingsHost(Dispatcher.CurrentDispatcher);
            var bar = new StatusBarWindow();
            try
            {
                bar.AttachSettings(host);
                bar.ConfigureSizeScale(() => scale);
                var row = Assert.IsType<Grid>(bar.FindName("SecondaryItemsRow"));
                var count = 0;
                for (var bits = 0; bits < 128; ++bits)
                {
                    var selected = Keys.Where((_, i) => (bits & (1 << i)) != 0).ToArray();
                    if (selected.Length > 3) continue;
                    ++count;
                    var settings = new DockSettings();
                    typeof(DockSettings).GetProperty("StatusBarSecondaryItems")!.SetValue(settings, selected.Reverse().ToArray());
                    host.Update(settings);
                    foreach (var name in new[] { "CpuText", "MemoryText", "MouseBatteryText", "HeadsetBatteryText", "KeyboardBatteryText" })
                        Assert.IsType<TextBlock>(bar.FindName(name)).Text = "100%";
                    ((TextBlock)bar.FindName("FanText")).Text = "30,000";
                    ((TextBlock)bar.FindName("MusicTitleText")).Text = new string('歌', 120);
                    var content = (FrameworkElement)bar.Content;
                    content.Measure(new Size(bar.Width, bar.Height));
                    content.Arrange(new Rect(0, 0, bar.Width, bar.Height));
                    content.UpdateLayout();
                    Assert.Equal(Math.Round(380 * scale, MidpointRounding.AwayFromZero), bar.Width);
                    Assert.Equal(Math.Round(54 * scale, MidpointRounding.AwayFromZero), bar.Height);
                    double previousRight = -8;
                    for (var i = 0; i < Keys.Length; ++i)
                    {
                        var panel = Assert.IsAssignableFrom<FrameworkElement>(bar.FindName(Panels[i]));
                        Assert.Equal(selected.Contains(Keys[i]) ? Visibility.Visible : Visibility.Collapsed, panel.Visibility);
                        if (panel.Visibility != Visibility.Visible) continue;
                        var left = panel.TranslatePoint(new Point(), row).X;
                        Assert.InRange(left - previousRight, 7.99, 8.01);
                        Assert.InRange(left + panel.ActualWidth, 0, row.ActualWidth + .01);
                        Assert.True(panel.ActualHeight > 0);
                        if (Keys[i] != "music")
                        {
                            Assert.Equal(64, panel.ActualWidth);
                            foreach (var text in TextChildren(panel))
                            {
                                text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                                Assert.True(text.DesiredSize.Width - text.Margin.Left - text.Margin.Right <= text.ActualWidth + .01, $"{Keys[i]} text clips: {text.Text}");
                            }
                        }
                        previousRight = left + panel.ActualWidth;
                    }
                }
                Assert.Equal(64, count);
            }
            finally { bar.Close(); }
        });
    }

    private static IEnumerable<TextBlock> TextChildren(DependencyObject parent)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); ++i)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is TextBlock text) yield return text;
            foreach (var descendant in TextChildren(child)) yield return descendant;
        }
    }
}
