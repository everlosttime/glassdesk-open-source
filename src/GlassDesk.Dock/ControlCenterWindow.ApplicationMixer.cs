using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GlassDesk.Platform;
using GlassDesk.Platform.Shell;

namespace GlassDesk.Dock;

public partial class ControlCenterWindow
{
    private readonly DispatcherTimer _mixerRefreshTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _mixerWriteTimer = new() { Interval = TimeSpan.FromMilliseconds(70) };
    private readonly Dictionary<string, MixerRow> _mixerRows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MixerChange> _mixerChanges = new(StringComparer.OrdinalIgnoreCase);
    private bool _mixerExpanded;
    private bool _mixerReading;
    private bool _mixerWriting;
    private bool _mixerUpdatingControls;
    private bool _mixerClosed;
    private DateTimeOffset _lastMixerEdit;

    private void InitializeApplicationMixer()
    {
        _mixerRefreshTimer.Tick += (_, _) => _ = RefreshApplicationMixerAsync();
        _mixerWriteTimer.Tick += (_, _) => _ = WriteMixerChangesAsync();
    }

    private void OnApplicationMixerClick(object sender, RoutedEventArgs e)
    {
        SetMixerExpanded(!_mixerExpanded);
        if (_mixerExpanded) _ = RefreshApplicationMixerAsync();
    }

    private void SetMixerExpanded(bool expanded)
    {
        _mixerExpanded = expanded;
        ApplicationMixerPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        ApplicationMixerArrow.Text = expanded ? "\uE70E" : "\uE70D";
        ResizeForMixer();
        if (expanded && IsVisible) _mixerRefreshTimer.Start();
        else _mixerRefreshTimer.Stop();
    }

    private void ResizeForMixer()
    {
        const double baseHeight = 494;
        var available = PrimaryDisplaySession is { IsAvailable: true } session
            ? session.Display.WorkArea.Height * 96d / session.Display.Dpi - baseHeight - 32 : 180;
        var extra = _mixerExpanded ? Math.Clamp(available, 64, 180) : 0;
        ApplicationMixerRow.Height = new GridLength(extra);
        BeginAnimation(HeightProperty, null);
        Height = baseHeight + extra;
        PositionNearAnchor();
        if (IsVisible) GlassSurface.RequestBackdropRefresh(revealing: true);
    }

    private async Task RefreshApplicationMixerAsync()
    {
        if (_mixerClosed || !_mixerExpanded || !IsVisible || _mixerReading || _mixerWriting ||
            _mixerChanges.Count > 0 || ApplicationMixerPanel.IsMouseCaptureWithin ||
            DateTimeOffset.Now - _lastMixerEdit < TimeSpan.FromSeconds(1)) return;
        _mixerReading = true;
        var startedAt = DateTimeOffset.Now;
        try
        {
            var snapshot = await Task.Run(ApplicationVolumeController.Read);
            if (_mixerClosed || !_mixerExpanded || !IsVisible || _lastMixerEdit >= startedAt ||
                ApplicationMixerPanel.IsMouseCaptureWithin) return;
            _mixerUpdatingControls = true;
            try
            {
                var keys = snapshot.Applications.Select(app => app.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var key in _mixerRows.Keys.Where(key => !keys.Contains(key)).ToArray())
                {
                    ApplicationMixerRows.Children.Remove(_mixerRows[key].Root);
                    _mixerRows.Remove(key);
                }
                foreach (var app in snapshot.Applications)
                {
                    if (!_mixerRows.TryGetValue(app.Key, out var row))
                    {
                        row = CreateMixerRow(app);
                        _mixerRows.Add(app.Key, row);
                        ApplicationMixerRows.Children.Add(row.Root);
                    }
                    row.Application = app;
                    row.Slider.Value = app.VolumePercent;
                    UpdateMixerRow(row);
                }
                ApplicationMixerMessage.Text = !snapshot.Available ? "音频设备暂不可用" : "暂无应用音频，播放声音后会自动出现";
                ApplicationMixerMessage.Visibility = snapshot.Applications.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            finally { _mixerUpdatingControls = false; }
        }
        finally { _mixerReading = false; }
    }

    private MixerRow CreateMixerRow(ApplicationVolume app)
    {
        var grid = new Grid { Margin = new Thickness(3, 6, 3, 6) };
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(28) });
        grid.ColumnDefinitions.Add(new());
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(34) });
        grid.RowDefinitions.Add(new() { Height = new GridLength(19) });
        grid.RowDefinitions.Add(new() { Height = new GridLength(26) });
        var iconPanel = new Grid { Width = 23, Height = 23, ToolTip = app.Name };
        iconPanel.Children.Add(new TextBlock { Text = "\uE7F4", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            Foreground = ForegroundBrush, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center });
        Grid.SetRowSpan(iconPanel, 2);
        grid.Children.Add(iconPanel);
        var name = new TextBlock { Text = app.Name, FontSize = 10.5, Foreground = ForegroundBrush,
            TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = app.Name, Margin = new Thickness(7, 0, 0, 0) };
        Grid.SetColumn(name, 1); Grid.SetColumnSpan(name, 2); grid.Children.Add(name);
        var slider = new Slider { Minimum = 0, Maximum = 100, Value = app.VolumePercent,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 7, 0) };
        System.Windows.Automation.AutomationProperties.SetName(slider, $"{app.Name} 音量");
        Grid.SetRow(slider, 1); Grid.SetColumn(slider, 1); grid.Children.Add(slider);
        var label = new TextBlock { FontSize = 10, Foreground = ForegroundBrush, VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetColumn(label, 2); Grid.SetRow(label, 1); grid.Children.Add(label);
        var row = new MixerRow(app, grid, slider, label);
        // The icon doubles as a per-app mute toggle, matching the system mixer.
        grid.Children.Remove(iconPanel);
        var mute = new Button { Content = iconPanel, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand, ToolTip = $"{app.Name}：静音/取消静音",
            FocusVisualStyle = null };
        Grid.SetRowSpan(mute, 2); grid.Children.Add(mute);
        mute.Click += (_, _) =>
        {
            row.Application = row.Application with { IsMuted = !row.Application.IsMuted };
            QueueMixerChange(row, null);
        };
        slider.ValueChanged += (_, args) =>
        {
            if (_mixerUpdatingControls) return;
            row.Application = row.Application with { VolumePercent = args.NewValue,
                IsMuted = args.NewValue > 0 ? false : row.Application.IsMuted };
            QueueMixerChange(row, args.NewValue);
        };
        _ = LoadMixerIconAsync(iconPanel, app.ExecutablePath);
        return row;
    }

    private static async Task LoadMixerIconAsync(Grid panel, string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        var icon = await Task.Run(() => IconExtractor.Extract(path));
        if (icon is null) return;
        var image = BitmapSource.Create(icon.Width, icon.Height, 96, 96, PixelFormats.Bgra32, null, icon.Bgra, icon.Width * 4);
        image.Freeze();
        panel.Children.Clear();
        panel.Children.Add(new Image { Source = image, Width = 21, Height = 21 });
    }

    private static void UpdateMixerRow(MixerRow row)
    {
        row.Label.Text = row.Application.IsMuted ? "静音" : $"{row.Application.VolumePercent:0}%";
        row.Slider.Opacity = row.Application.IsMuted ? 0.55 : 1;
    }

    private void QueueMixerChange(MixerRow row, double? volume)
    {
        if (_mixerChanges.TryGetValue(row.Application.Key, out var prior)) volume ??= prior.Volume;
        _mixerChanges[row.Application.Key] = new(row.Application, volume);
        _lastMixerEdit = DateTimeOffset.Now;
        UpdateMixerRow(row);
        _mixerWriteTimer.Start();
    }

    private async Task WriteMixerChangesAsync()
    {
        if (_mixerClosed || _mixerWriting || _mixerChanges.Count == 0) return;
        _mixerWriteTimer.Stop();
        _mixerWriting = true;
        var pending = _mixerChanges.Values.ToArray();
        _mixerChanges.Clear();
        try
        {
            var success = await Task.Run(() =>
            {
                var result = true;
                foreach (var change in pending)
                {
                    if (change.Volume is { } volume) result &= ApplicationVolumeController.SetVolume(change.Application, volume);
                    result &= ApplicationVolumeController.SetMuted(change.Application, change.Application.IsMuted);
                }
                return result;
            });
            if (!_mixerClosed && !success)
            {
                ApplicationMixerMessage.Text = "应用已退出或输出设备已变化，正在刷新列表";
                ApplicationMixerMessage.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            _mixerWriting = false;
            if (!_mixerClosed && _mixerChanges.Count > 0) _mixerWriteTimer.Start();
        }
    }

    private sealed record MixerChange(ApplicationVolume Application, double? Volume);
    private sealed class MixerRow(ApplicationVolume application, Grid root, Slider slider, TextBlock label)
    {
        public ApplicationVolume Application { get; set; } = application;
        public Grid Root { get; } = root;
        public Slider Slider { get; } = slider;
        public TextBlock Label { get; } = label;
    }
}
