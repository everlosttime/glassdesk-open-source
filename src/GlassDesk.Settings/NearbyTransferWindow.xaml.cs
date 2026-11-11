using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Windows.Interop;
using GlassDesk.Core.Contracts;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Nearby;
using GlassDesk.UI.Controls;
using GlassDesk.UI.Theming;
using GlassDesk.Platform.Displays;
using Microsoft.Win32;

namespace GlassDesk.Settings;

public partial class NearbyTransferWindow : GlassWindow
{
    private readonly INearbyTransferService _service;
    private readonly Func<Task> _enable;
    private readonly Action _settings;
    private readonly ObservableCollection<NearbyFileRow> _rows = [];
    private readonly HashSet<string> _sources = new(StringComparer.OrdinalIgnoreCase);
    private bool _shutdown, _adding, _positioning;
    private volatile bool _refreshSuppressed;
    private int _refreshPending;
    protected override bool UseOpaquePanel => true;
    protected override double ResolveCornerRadius(AppearanceSettings appearance) => 32;

    public NearbyTransferWindow(INearbyTransferService service, Func<Task> enable, Action settings)
    {
        _service = service; _enable = enable; _settings = settings;
        InitializeComponent(); FilesBox.ItemsSource = _rows;
        _service.Changed += OnServiceChanged;
        Closing += (_, args) => { if (!_shutdown) { args.Cancel = true; Hide(); } };
        Closed += (_, _) => _service.Changed -= OnServiceChanged;
        IsVisibleChanged += (_, _) => { if (IsVisible) { PositionPanel(); RefreshState(); } else ResetReveal(); };
        RefreshState();
    }

    public void CloseForShutdown() { _shutdown = true; Close(); }

    public bool ShowPanel(bool toggle = false)
    {
        if (_shutdown) return false;
        if (toggle && IsVisible) { Hide(); return false; }
        if (PrimaryDisplaySession is { IsAvailable: false }) return false;
        var opening = !IsVisible;
        var animate = opening && (ThemeService.Current?.AnimationsEnabled ?? true);
        if (opening) { ResetReveal(); Opacity = animate ? 0 : 1; }
        PositionPanel();
        Show(); Activate();
        if (animate)
        {
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
            Opacity = 1;
            var reveal = new DoubleAnimation(.965, 1, TimeSpan.FromMilliseconds(200))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };
            RevealScale.BeginAnimation(ScaleTransform.ScaleXProperty, reveal);
            RevealScale.BeginAnimation(ScaleTransform.ScaleYProperty, reveal);
        }
        return true;
    }

    private void ResetReveal()
    {
        BeginAnimation(OpacityProperty, null); Opacity = 1;
        RevealScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        RevealScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        RevealScale.ScaleX = RevealScale.ScaleY = 1;
    }

    private void PositionPanel()
    {
        if (_positioning || _shutdown) return;
        var display = PrimaryDisplaySession is { IsAvailable: true } session ? session.Display : DisplayService.Primary();
        if (display is null) return;
        _positioning = true;
        try
        {
            var work = display.WorkArea;
            var margin = Math.Min(display.ToPixels(16), Math.Max(0, (Math.Min(work.Width, work.Height) - 1) / 2));
            var width = Math.Max(1, Math.Min(display.ToPixels(660), work.Width - margin * 2));
            var height = Math.Max(1, Math.Min(display.ToPixels(700), work.Height - margin * 2));
            Width = display.ToDips(width); Height = display.ToDips(height);
            var handle = new WindowInteropHelper(this).EnsureHandle();
            _ = DisplayService.MoveWindowToPixelRect(handle, new(work.X + margin, work.Y + margin, width, height));
        }
        finally { _positioning = false; }
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        if (IsVisible) PositionPanel();
    }
    public Func<Task<string>>? RepairNetwork { get; set; }
    public void UpdateNetworkStatus(string message) => NetworkStatusText.Text = message;
    private async void OnRepairConnectionClick(object sender, RoutedEventArgs e)
    {
        RepairConnectionButton.IsEnabled = false;
        try
        {
            NetworkStatusText.Text = "正在检查接收连接；如 Windows 请求权限，请确认授权。";
            NetworkStatusText.Text = RepairNetwork is null ? "请从 GlassDesk 主程序打开面板后修复。" : await RepairNetwork();
        }
        catch (OperationCanceledException) { NetworkStatusText.Text = "检查已取消。"; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        { NetworkStatusText.Text = "检查失败：" + error.Message; }
        finally { RepairConnectionButton.IsEnabled = true; }
    }
    public void SetRefreshSuppressed(bool suppressed)
    {
        if (_refreshSuppressed == suppressed) return;
        _refreshSuppressed = suppressed;
        if (!suppressed && IsVisible) RefreshState();
    }
    public void ShowIncomingRequest() { if (_service.State.Incoming is not null) IncomingPanel.BringIntoView(); }
    public async Task AddSourcesAsync(IEnumerable<string> sources)
    {
        if (_adding) return;
        _adding = true;
        try
        {
            foreach (var source in sources) _sources.Add(source);
            var snapshot = _sources.ToArray();
            var selection = await Task.Run(() => FileSelection.Expand(snapshot));
            _rows.Clear();
            foreach (var file in selection.Files) _rows.Add(new(file));
            FileCountText.Text = $"{_rows.Count} 个文件 · {FormatSize(_rows.Sum(r => r.File.Size))}";
            PanelStatusText.Text = selection.Warnings.Count == 0 ? "原文件发送，不压缩、不转码。" : string.Join("\n", selection.Warnings.Take(4));
            foreach (var row in _rows.ToArray())
            {
                var thumbnail = await Task.Run(() => LoadThumbnail(row.File));
                if (thumbnail is not null) row.SetThumbnail(thumbnail);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { PanelStatusText.Text = "添加失败：" + error.Message; }
        finally { _adding = false; }
    }

    private static BitmapImage? LoadThumbnail(NearbyFile file)
    {
        if (!file.ContentType.StartsWith("image/", StringComparison.Ordinal) || file.LocalPath is null || file.Size > 64L * 1024 * 1024) return null;
        try
        {
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 64; image.DecodePixelHeight = 64; image.UriSource = new Uri(file.LocalPath); image.EndInit(); image.Freeze(); return image;
        }
        catch (Exception error) when (error is IOException or NotSupportedException or ArgumentException or System.Runtime.InteropServices.COMException or FileFormatException) { return null; }
    }

    private void OnServiceChanged(object? sender, EventArgs e)
    {
        if (_refreshSuppressed || Interlocked.Exchange(ref _refreshPending, 1) != 0 || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(() => { Interlocked.Exchange(ref _refreshPending, 0); if (!_shutdown && !_refreshSuppressed && IsVisible) RefreshState(); });
    }

    public void RefreshState()
    {
        var state = _service.State;
        IntroPanel.Visibility = state.Running ? Visibility.Collapsed : Visibility.Visible;
        DeviceNameText.Text = state.Running ? $"{state.DeviceName} · 后台待命" : "尚未开启 · 不监听网络";
        var selected = (DevicesBox.SelectedItem as NearbyDevice)?.Fingerprint;
        if (!DevicesBox.Items.Cast<NearbyDevice>().SequenceEqual(state.Devices))
        {
            DevicesBox.ItemsSource = state.Devices;
            DevicesBox.SelectedItem = state.Devices.FirstOrDefault(d => d.Fingerprint == selected) ?? (state.Devices.Count > 0 ? state.Devices[0] : null);
        }
        NoDevicesText.Visibility = state.Devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var sending = state.Sending?.State is NearbyTaskState.Waiting or NearbyTaskState.Transferring;
        SendFilesButton.IsEnabled = state.Running && !sending;
        RetryFilesButton.IsEnabled = state.Running && !sending && state.Sending?.FailedFiles.Count > 0;
        CancelSendButton.IsEnabled = sending;
        CancelReceiveButton.IsEnabled = state.Receiving?.State is NearbyTaskState.Waiting or NearbyTaskState.Transferring;
        SendingText.Text = Describe(state.Sending, "发送"); ReceivingText.Text = Describe(state.Receiving, "接收");
        SendingProgress.Value = Percent(state.Sending); ReceivingProgress.Value = Percent(state.Receiving);
        OpenReceiveFolderButton.IsEnabled = state.Receiving?.Directory is not null && state.Receiving.State != NearbyTaskState.Waiting;
        IncomingPanel.Visibility = state.Incoming is null ? Visibility.Collapsed : Visibility.Visible;
        if (state.Incoming is { } incoming)
            IncomingText.Text = $"{incoming.DeviceName} · {incoming.Files.Count} 个文件 · {FormatSize(incoming.Files.Sum(f => f.Size))}\n保存到：{incoming.Directory}\n两分钟未处理将拒收。";
        if (state.Error is not null) PanelStatusText.Text = state.Error;
    }

    private static double Percent(NearbyTask? task) => task?.State == NearbyTaskState.Completed ? 100 : task is null || task.Total == 0 ? 0 : Math.Clamp(100d * task.Transferred / task.Total, 0, 100);
    private static string Describe(NearbyTask? task, string direction) => task is null ? $"暂无{direction}任务" :
        $"{direction} · {task.DeviceName} · {StateText(task.State)}\n{task.CurrentFile}\n{FormatSize(task.Transferred)} / {FormatSize(task.Total)} · {FormatSize((long)task.BytesPerSecond)}/s" + (task.Error is null ? "" : "\n" + task.Error);
    private static string StateText(NearbyTaskState state) => state switch { NearbyTaskState.Waiting => "等待确认", NearbyTaskState.Transferring => "传输中", NearbyTaskState.Completed => "已完成", NearbyTaskState.Cancelled => "已取消", _ => "失败" };
    public static string FormatSize(long size) => size switch { >= 1073741824 => $"{size / 1073741824d:0.##} GB", >= 1048576 => $"{size / 1048576d:0.##} MB", >= 1024 => $"{size / 1024d:0.##} KB", _ => $"{size} B" };

    private async void OnEnableClick(object sender, RoutedEventArgs e)
    {
        EnableNearbyButton.IsEnabled = false;
        try { await _enable(); RefreshState(); }
        catch (Exception error) { PanelStatusText.Text = "无法开启：" + error.Message; }
        finally { EnableNearbyButton.IsEnabled = true; }
    }
    private void OnHideClick(object sender, RoutedEventArgs e) => Hide();
    private void OnSettingsClick(object sender, RoutedEventArgs e) => _settings();
    private async void OnRefreshClick(object sender, RoutedEventArgs e) { try { await _service.RefreshAsync(); } catch (Exception error) { PanelStatusText.Text = error.Message; } }
    private async void OnManualIpClick(object sender, RoutedEventArgs e)
    {
        var input = NearbyTextPrompt.Ask(this, "手动添加设备", "输入对方局域网 IPv4 地址，可附加端口，例如 192.168.1.8:53317");
        if (input is null) return;
        try
        {
            var parts = input.Trim().Split(':');
            var port = parts.Length == 1 ? 53317 : int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
            if (parts.Length > 2) throw new ArgumentException("请输入 IPv4 地址。");
            await _service.AddDeviceAsync(parts[0], port);
        }
        catch (Exception error) { PanelStatusText.Text = "添加设备失败：" + error.Message; }
    }
    private void OnTroubleshootClick(object sender, RoutedEventArgs e) => MessageBox.Show(this,
        "1. 两端连接同一局域网，并开启附近传输 / LocalSend。\n2. 点击“修复接收连接”并允许 Windows 授权；规则自动适用于各本地子网和网络类型。\n3. 检查路由器是否开启访客隔离或 AP 隔离；这类隔离需要在路由器处理。\n4. VPN、多网卡可能影响发现，可刷新或手动输入对方 IP。\n5. 组织策略或安全软件也可能限制入站连接，需要管理员允许。\n6. 同机运行 LocalSend 时，请在添加 IP 时填写对方实际端口。", "附近传输 · 网络排查");
    private async void OnAddMediaClick(object sender, RoutedEventArgs e) => await ChooseFilesAsync("图片和视频|*.jpg;*.jpeg;*.png;*.heic;*.heif;*.gif;*.webp;*.bmp;*.tif;*.tiff;*.mp4;*.mov;*.m4v;*.mkv;*.avi;*.webm|所有文件|*.*");
    private async void OnAddFilesClick(object sender, RoutedEventArgs e) => await ChooseFilesAsync("所有文件|*.*");
    private async Task ChooseFilesAsync(string filter)
    {
        var picker = new OpenFileDialog { Multiselect = true, Filter = filter, Title = "添加待发送文件" };
        if (picker.ShowDialog(this) == true) await AddSourcesAsync(picker.FileNames);
    }
    private async void OnAddFolderClick(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Multiselect = true, Title = "添加待发送文件夹" };
        if (picker.ShowDialog(this) == true) await AddSourcesAsync(picker.FolderNames);
    }
    private void OnClearClick(object sender, RoutedEventArgs e) { if (_adding) return; _rows.Clear(); _sources.Clear(); FileCountText.Text = "拖入文件或文件夹，不压缩、不转码。"; }
    private void OnFilesDragOver(object sender, DragEventArgs e) { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    private async void OnFilesDrop(object sender, DragEventArgs e) { e.Handled = true; if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) await AddSourcesAsync(paths); }
    private async void OnSendClick(object sender, RoutedEventArgs e) => await SendFilesAsync(_rows.Select(r => r.File).ToArray());
    private async void OnRetryClick(object sender, RoutedEventArgs e) => await SendFilesAsync(_service.State.Sending?.FailedFiles ?? []);
    private async Task SendFilesAsync(IReadOnlyList<NearbyFile> files)
    {
        if (DevicesBox.SelectedItem is not NearbyDevice device) { PanelStatusText.Text = "请先选择设备。"; return; }
        try { await _service.SendAsync(device, files, RequestPinAsync); }
        catch (Exception error) { PanelStatusText.Text = error.Message; }
    }
    private async Task<string?> RequestPinAsync(CancellationToken cancellationToken)
        => await Dispatcher.InvokeAsync(() => NearbyTextPrompt.Ask(this, "对方要求 PIN", "输入对方设备显示的 PIN", cancellationToken)).Task.WaitAsync(cancellationToken);
    private async void OnAcceptClick(object sender, RoutedEventArgs e) { if (_service.State.Incoming is { } request) await _service.DecideAsync(request.Id, true); }
    private async void OnRejectClick(object sender, RoutedEventArgs e) { if (_service.State.Incoming is { } request) await _service.DecideAsync(request.Id, false); }
    private async void OnCancelSendClick(object sender, RoutedEventArgs e) => await _service.CancelAsync(false);
    private async void OnCancelReceiveClick(object sender, RoutedEventArgs e) => await _service.CancelAsync(true);
    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        if (_service.State.Receiving?.Directory is not { } path || !Directory.Exists(path)) return;
        try { using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception error) { PanelStatusText.Text = "无法打开文件夹：" + error.Message; }
    }

    private sealed class NearbyFileRow(NearbyFile file) : INotifyPropertyChanged
    {
        public NearbyFile File { get; } = file;
        public string Name => File.Name;
        public string Detail => File.ContentType + " · " + FormatSize(File.Size);
        public string Glyph => File.ContentType.StartsWith("image/", StringComparison.Ordinal) ? "▧" : File.ContentType.StartsWith("video/", StringComparison.Ordinal) ? "▶" : "▤";
        public BitmapSource? Thumbnail { get; private set; }
        public event PropertyChangedEventHandler? PropertyChanged;
        public void SetThumbnail(BitmapSource thumbnail) { Thumbnail = thumbnail; PropertyChanged?.Invoke(this, new(nameof(Thumbnail))); }
    }
}
