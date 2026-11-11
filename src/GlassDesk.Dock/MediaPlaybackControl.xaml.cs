using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GlassDesk.Platform;

namespace GlassDesk.Dock;

public partial class MediaPlaybackControl : UserControl, IDisposable
{
    private readonly MediaSessionController _controller = new();
    private readonly DispatcherTimer _timer;
    private bool _running, _refreshing, _closed;
    private long _generation;
    public event EventHandler<MediaSessionSnapshot>? SnapshotChanged;

    public MediaPlaybackControl()
    {
        InitializeComponent();
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += async (_, _) => await RefreshAsync();
    }

    public void Start()
    {
        if (_running || _closed) return;
        _running = true;
        ++_generation;
        _timer.Start();
        _ = RefreshAsync();
    }

    public void Stop()
    {
        _running = false;
        ++_generation;
        _timer.Stop();
    }

    private async Task RefreshAsync()
    {
        if (!_running || _closed || _refreshing) return;
        _refreshing = true;
        var generation = _generation;
        try
        {
            var snapshot = await _controller.ReadAsync();
            if (_closed || Dispatcher.HasShutdownStarted) return;
            await Dispatcher.InvokeAsync(() =>
            {
                if (_running && !_closed && generation == _generation) ApplySnapshot(snapshot);
            });
        }
        finally { _refreshing = false; }
    }

    public void ApplySnapshot(MediaSessionSnapshot snapshot)
    {
        TitleText.Text = snapshot.Available ? snapshot.Title : "暂无媒体";
        ArtistText.Text = snapshot.Available ? snapshot.Artist : "播放音乐或视频后显示";
        PlayPauseGlyph.Text = snapshot.IsPlaying ? "\uE769" : "\uE768";
        PreviousButton.IsEnabled = snapshot.Available && snapshot.CanGoPrevious;
        PlayPauseButton.IsEnabled = snapshot.Available && snapshot.CanTogglePlayPause;
        NextButton.IsEnabled = snapshot.Available && snapshot.CanGoNext;
        BitmapImage? bitmap = null;
        if (snapshot.Artwork is { Length: > 0 } bytes)
        {
            try
            {
                using var stream = new MemoryStream(bytes, false);
                bitmap = new BitmapImage();
                bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream;
                bitmap.EndInit(); bitmap.Freeze();
            }
            catch (Exception ex) when (ex is NotSupportedException or IOException or ArgumentException) { bitmap = null; }
        }
        ArtworkImage.Source = bitmap;
        ArtworkImage.Visibility = bitmap is null ? Visibility.Collapsed : Visibility.Visible;
        ArtworkPlaceholder.Visibility = bitmap is null ? Visibility.Visible : Visibility.Collapsed;
        SnapshotChanged?.Invoke(this, snapshot);
    }

    private async void OnPreviousClick(object sender, RoutedEventArgs e) { await _controller.PreviousAsync(); await RefreshAsync(); }
    private async void OnPlayPauseClick(object sender, RoutedEventArgs e) { await _controller.TogglePlayPauseAsync(); await RefreshAsync(); }
    private async void OnNextClick(object sender, RoutedEventArgs e) { await _controller.NextAsync(); await RefreshAsync(); }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        Stop();
        _controller.Dispose();
        GC.SuppressFinalize(this);
    }
}
