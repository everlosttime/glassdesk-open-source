using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassDesk.Settings;

namespace GlassDesk.Dock.Tests;

public sealed class DownloadEntryTests
{
    [Fact]
    public void DownloadEntryRemainsAvailableWhileUpdateChecksArePendingOrFail()
    {
        StaTest.Run(() =>
        {
            var settings = new SettingsWindow();
            try
            {
                var button = Assert.IsType<Button>(settings.FindName("CloudDownloadButton"));
                Assert.Equal("下载入口", button.Content);
                Assert.Equal("下载入口", AutomationProperties.GetName(button));
                var requested = 0;
                settings.CloudDownloadsRequested += (_, _) => requested++;
                settings.ShowUpdateStatus("1.5.12", "正在检查更新", checking: true);
                Assert.True(button.IsEnabled);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                settings.ShowUpdateStatus("1.5.12", "无法检查更新");
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(2, requested);
            }
            finally { settings.Close(); }
        });
    }

    [Fact]
    public void DownloadDialogContainsOnlyOneLocallyBundledQrImage()
    {
        StaTest.Run(() =>
        {
            var type = typeof(SettingsWindow).Assembly.GetType("GlassDesk.Settings.DownloadEntryWindow");
            Assert.NotNull(type);
            var window = Assert.IsAssignableFrom<Window>(Activator.CreateInstance(type));
            try
            {
                var content = Assert.IsType<Border>(window.Content);
                var image = Assert.IsType<Image>(content.Child);
                Assert.Equal("GlassDesk 官网二维码", AutomationProperties.GetName(image));
                var bitmap = Assert.IsAssignableFrom<BitmapSource>(image.Source);
                Assert.True(bitmap.IsFrozen);
                Assert.Equal(370, bitmap.PixelWidth);
                Assert.Equal(370, bitmap.PixelHeight);
                Assert.Equal(BitmapScalingMode.NearestNeighbor, RenderOptions.GetBitmapScalingMode(image));
                content.Measure(new Size(window.Width, window.Height));
                content.Arrange(new Rect(0, 0, window.Width, window.Height));
                content.UpdateLayout();
                Assert.Equal(image.ActualWidth, image.ActualHeight);
            }
            finally { window.Close(); }
        });
    }
}
