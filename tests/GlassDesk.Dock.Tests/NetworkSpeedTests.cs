using GlassDesk.Platform;

namespace GlassDesk.Dock.Tests;

public sealed class NetworkSpeedTests
{
    [Fact]
    public void NetworkReadoutDoesNotOverlapMusic()
    {
        StaTest.Run(() =>
        {
            var window = new StatusBarWindow();
            try
            {
                window.Show(); window.UpdateLayout();
                var root = (System.Windows.FrameworkElement)window.Content;
                var music = (System.Windows.FrameworkElement)window.FindName("MusicSummary");
                var network = (System.Windows.FrameworkElement)window.FindName("NetworkSpeedPanel");
                var musicBounds = music.TransformToAncestor(root).TransformBounds(new System.Windows.Rect(music.RenderSize));
                var networkBounds = network.TransformToAncestor(root).TransformBounds(new System.Windows.Rect(network.RenderSize));
                Assert.False(musicBounds.IntersectsWith(networkBounds), "Music must not cover download/upload values.");
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void UsesCounterDifferencesAndActualElapsedTimeForBothDirections()
    {
        var reader = new NetworkSpeedReader();
        Assert.Null(reader.Sample(new("a", "Ethernet", 100, 200), TimeSpan.Zero).DownloadBytesPerSecond);
        var speed = reader.Sample(new("a", "Ethernet", 2148, 1224), TimeSpan.FromSeconds(2));
        Assert.Equal(1024, speed.DownloadBytesPerSecond);
        Assert.Equal(512, speed.UploadBytesPerSecond);
    }

    [Fact]
    public void SwitchingAdaptersResettingCountersAndResumingDoNotProduceSpikes()
    {
        var reader = new NetworkSpeedReader();
        reader.Sample(new("a", "Wi-Fi", 1000, 1000), TimeSpan.Zero);
        Assert.Null(reader.Sample(new("b", "Ethernet", 99999999, 99999999), TimeSpan.FromSeconds(1)).DownloadBytesPerSecond);
        Assert.Null(reader.Sample(new("b", "Ethernet", 10, 10), TimeSpan.FromSeconds(2)).DownloadBytesPerSecond);
        Assert.Null(reader.Sample(new("b", "Ethernet", 1000, 1000), TimeSpan.FromSeconds(30)).DownloadBytesPerSecond);
        Assert.False(reader.Sample(null, TimeSpan.FromSeconds(31)).Connected);
        Assert.Null(reader.Sample(new("b", "Ethernet", 2000, 2000), TimeSpan.FromSeconds(32)).DownloadBytesPerSecond);
        reader.Reset();
        Assert.Null(reader.Sample(new("b", "Ethernet", 3000, 3000), TimeSpan.FromSeconds(33)).DownloadBytesPerSecond);
    }

    [Theory]
    [InlineData(0, "0 B/s")]
    [InlineData(512, "512 B/s")]
    [InlineData(1024, "1.0 KB/s")]
    [InlineData(1048576, "1.0 MB/s")]
    public void AutomaticallyFormatsRates(double rate, string expected)
    {
        Assert.Equal(expected.Replace('.', System.Globalization.CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator[0]), NetworkSpeedReader.FormatRate(rate));
        Assert.Equal("--", NetworkSpeedReader.FormatRate(null));
    }
}
