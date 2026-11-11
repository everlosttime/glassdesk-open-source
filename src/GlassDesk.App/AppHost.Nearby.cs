// Public 1.6.0 adaptation: information-bar-specific implementation is excluded.
using System.IO;
using System.Net.NetworkInformation;
using System.Windows.Threading;
using GlassDesk.Core.Contracts;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Platform;
using GlassDesk.Platform.Network;
using GlassDesk.Settings;

namespace GlassDesk.App;

internal sealed partial class AppHost
{
    private NearbyTransferWindow? _nearbyPanel;
    private NearbyIncomingNotice? _nearbyNotice;
    private readonly SemaphoreSlim _nearbyControl = new(1, 1);
    private CancellationTokenSource _nearbyHostLifetime = new();
    private string? _noticeRequestId, _lastNearbyLog;
    private int _nearbyUpdatePending;
    private readonly NearbyFirewall _nearbyFirewall = new(Environment.ProcessPath!);
    private string _nearbyNetworkStatus = "";
    private int _nearbyNetworkRevision;

    private void InitializeNearby()
    {
        if (_services is null || _dock is null) return;
        _services.Nearby.Changed += OnNearbyChanged;
        NetworkChange.NetworkAddressChanged += OnNearbyNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNearbyNetworkChanged;
        if (_data!.Settings.Current.Nearby.Enabled)
            _application.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
            {
                var error = await ApplyNearbySettingsCoreAsync(_data.Settings.Current.Nearby, false);
                if (error is not null && !_shuttingDown) _settings?.UpdateNearbyStatus(error);
            }));
    }

    private Task<string?> ApplyNearbySettingsAsync(NearbySettings next) => ApplyNearbySettingsCoreAsync(next, true);

    private async Task<string?> ApplyNearbySettingsCoreAsync(NearbySettings next, bool requestFirewallPermission)
    {
        if (_shuttingDown || _nearbyHostLifetime.IsCancellationRequested || _services is null) return "程序正在停止，未启动附近传输。";
        var service = _services.Nearby;
        var token = _nearbyHostLifetime.Token;
        try { await _nearbyControl.WaitAsync(token); }
        catch (OperationCanceledException) { return "程序正在停止。"; }
        try
        {
            if (_shuttingDown || _services is null) return "程序正在停止。";
            if (next.Enabled)
            {
                var access = await _nearbyFirewall.EnsureAsync(requestFirewallPermission, token);
                SetNearbyNetworkStatus(access.Message);
                if (!access.Ready && requestFirewallPermission) return access.Message;
            }
            var previous = _data!.Settings.Current.Nearby;
            if (service.State.Running && next.Enabled && previous.Enabled && next.DeviceName == previous.DeviceName && next.ReceiveDirectory == previous.ReceiveDirectory) return null;
            try
            {
                await service.StopAsync(token);
                if (next.Enabled) await service.StartAsync(next, token);
                token.ThrowIfCancellationRequested();
                return null;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException
                or System.ComponentModel.Win32Exception or OperationCanceledException or ArgumentException or InvalidOperationException)
            {
                if (!token.IsCancellationRequested && !_shuttingDown && previous.Enabled && !service.State.Running)
                {
                    try { await service.StartAsync(previous, token); }
                    catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException
                        or System.ComponentModel.Win32Exception or OperationCanceledException or ArgumentException or InvalidOperationException)
                    { return "无法应用或恢复后台服务，请检查接收目录和网络后重试。原配置已保留。"; }
                }
                return "附近传输未能启动：" + error.Message;
            }
        }
        finally { _nearbyControl.Release(); }
    }

    private async Task EnableNearbyAsync()
    {
        if (_data is null || _shuttingDown) return;
        var before = SettingsPageEditor.Clone(_data.Settings.Current);
        var next = SettingsPageEditor.Clone(before).Nearby; next.Enabled = true;
        var error = await ApplyNearbySettingsAsync(next);
        if (error is not null) throw new IOException(error);
        if (_shuttingDown || _nearbyHostLifetime.IsCancellationRequested) return;
        _data.Settings.Current.Nearby = next;
        try { _data.Settings.Save(); }
        catch
        {
            _data.Settings.Current.Nearby = before.Nearby;
            await ApplyNearbySettingsAsync(before.Nearby);
            throw;
        }
        _settings?.ReloadNearbySettings();
    }

    private async void ShowNearbyPanel(IReadOnlyList<string>? paths, bool toggle = false)
    {
        if (_services is null || _shuttingDown || _nearbyHostLifetime.IsCancellationRequested) return;
        if (_nearbyPanel is null)
        {
            _nearbyPanel = new NearbyTransferWindow(_services.Nearby, EnableNearbyAsync, ShowSettings) { Icon = CreateWindowIcon() };
            if (_primaryDisplaySession is not null) _nearbyPanel.BindPrimaryDisplay(_primaryDisplaySession);
        }
        _nearbyPanel.RepairNetwork = RepairNearbyConnectionAsync;
        _nearbyPanel.UpdateNetworkStatus(_nearbyNetworkStatus);
        _nearbyPanel.SetRefreshSuppressed(_performance?.IsSuppressionRequested == true);
        if (!_nearbyPanel.ShowPanel(toggle)) return;
        WindowActivation.TryBringToForeground(new System.Windows.Interop.WindowInteropHelper(_nearbyPanel).Handle);
        _nearbyPanel.ShowIncomingRequest();
        if (paths is not null) await _nearbyPanel.AddSourcesAsync(paths);
    }

    private void OnNearbyChanged(object? sender, EventArgs e)
    {
        if (_shuttingDown || Interlocked.Exchange(ref _nearbyUpdatePending, 1) != 0) return;
        _application.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            Interlocked.Exchange(ref _nearbyUpdatePending, 0);
            if (_shuttingDown || _services is null || _nearbyHostLifetime.IsCancellationRequested) return;
            var state = _services.Nearby.State;
            _settings?.UpdateNearbyStatus(NearbyStatusText());
            if (state.Incoming is { } incoming && incoming.Id != _noticeRequestId)
            {
                _noticeRequestId = incoming.Id;
                _nearbyNotice?.Close();
                _nearbyNotice = new NearbyIncomingNotice(incoming, () => ShowNearbyPanel(null));
                _nearbyNotice.Show();
            }
            else if (state.Incoming is null) { _nearbyNotice?.Close(); _nearbyNotice = null; _noticeRequestId = null; }
            var log = $"running={state.Running}; send={state.Sending?.State}; receive={state.Receiving?.State}";
            if (log != _lastNearbyLog) { _lastNearbyLog = log; AppendToLog($"Nearby {log}"); }
        }));
    }

    private string NearbyStatusText()
    {
        var state=_services!.Nearby.State;
        return (state.Running?$"正在后台待命 · {state.DeviceName} · 端口 {state.Port}":"已停止监听。关闭面板不会改变服务开关。")
            +(state.Error is null?"":"\n"+state.Error)+(_nearbyNetworkStatus.Length==0?"":"\n"+_nearbyNetworkStatus);
    }

    private async Task StopNearbyForRestoreAsync()
    {
        _nearbyHostLifetime.Cancel();
        _nearbyNotice?.Close(); _nearbyNotice = null;
        _nearbyPanel?.CloseForShutdown(); _nearbyPanel = null;
        if (_services is not null) await _services.Nearby.StopAsync();
    }

    private void OnNearbyNetworkChanged(object? sender, EventArgs e)
    {
        if (_shuttingDown || _nearbyHostLifetime.IsCancellationRequested) return;
        var revision = Interlocked.Increment(ref _nearbyNetworkRevision);
        _application.Dispatcher.BeginInvoke(new Action(async () =>
        {
            if (_services is null || _shuttingDown || _nearbyHostLifetime.IsCancellationRequested) return;
            try
            {
                var token = _nearbyHostLifetime.Token;
                await Task.Delay(1200, token);
                if (revision != Volatile.Read(ref _nearbyNetworkRevision) || !_services.Nearby.State.Running) return;
                var access = await _nearbyFirewall.EnsureAsync(false, token);
                SetNearbyNetworkStatus(access.Message);
                await _services.Nearby.RefreshAsync(token);
            }
            catch (OperationCanceledException) { }
        }));
    }

    private void SetNearbyNetworkStatus(string message)
    {
        if (_shuttingDown || _nearbyHostLifetime.IsCancellationRequested) return;
        _nearbyNetworkStatus = message;
        _nearbyPanel?.UpdateNetworkStatus(message);
        OnNearbyChanged(this, EventArgs.Empty);
    }

    private async Task<string> RepairNearbyConnectionAsync()
    {
        var token = _nearbyHostLifetime.Token;
        await _nearbyControl.WaitAsync(token);
        try
        {
            var access = await _nearbyFirewall.EnsureAsync(true, token);
            SetNearbyNetworkStatus(access.Message);
            if (_services is not null && _services.Nearby.State.Running) await _services.Nearby.RefreshAsync(token);
            return access.Message;
        }
        finally { _nearbyControl.Release(); }
    }

    private async Task ResumeNearbyAfterFailedRestoreAsync()
    {
        if (_shuttingDown || _data is null) return;
        _nearbyHostLifetime.Dispose();
        _nearbyHostLifetime = new CancellationTokenSource();
        var error = await ApplyNearbySettingsCoreAsync(_data.Settings.Current.Nearby, false);
        if (error is not null) _settings?.UpdateNearbyStatus(error);
    }

    private void DisposeNearbyUi()
    {
        _nearbyHostLifetime.Cancel();
        NetworkChange.NetworkAddressChanged -= OnNearbyNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNearbyNetworkChanged;
        if (_services is not null) _services.Nearby.Changed -= OnNearbyChanged;
        _nearbyNotice?.Close(); _nearbyNotice = null;
        _nearbyPanel?.CloseForShutdown(); _nearbyPanel = null;
    }
}
