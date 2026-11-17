using System.Windows;
using GlassDesk.Core.Models.Settings;

namespace GlassDesk.Settings;

public sealed record DesktopAutoAddEnableResult(bool Enabled, string? Message = null);

public partial class SettingsWindow
{
    private bool _launcherOrganizationBusy;
    public Func<Task<DesktopAutoAddEnableResult>>? EnableDesktopAutoAddAsync { get; set; }
    public Func<Task<string>>? OrganizeLauncherAppsAsync { get; set; }
    public Func<Task<string>>? ScanDesktopAppsAsync { get; set; }

    public void UpdateLauncherOrganizationStatus(string status)
    {
        if (!_launcherOrganizationBusy) LauncherSettingsStatus.Text = status;
    }

    private void UpdateLauncherOrganizationControls()
    {
        var groups = LauncherModeCombo.SelectedIndex == 1;
        LauncherAutoAddDesktopCheck.IsEnabled = !_launcherOrganizationBusy;
        LauncherSmartGroupingCheck.IsEnabled = groups && !_launcherOrganizationBusy;
        LauncherOrganizeExistingButton.IsEnabled = groups && !_launcherOrganizationBusy;
        LauncherScanDesktopButton.IsEnabled = !_launcherOrganizationBusy;
    }

    private async void OnLauncherAutoAddChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingLauncherControls || _dataStore is null || _launcherOrganizationBusy) return;
        var requested = LauncherAutoAddDesktopCheck.IsChecked == true;
        if (requested == _dataStore.Settings.Current.Launcher.AutoAddDesktopAppsEnabled) return;
        _launcherOrganizationBusy = true;
        UpdateLauncherOrganizationControls();
        try
        {
            string? completionMessage = null;
            if (requested)
            {
                var result = EnableDesktopAutoAddAsync is { } enable
                    ? await enable()
                    : new DesktopAutoAddEnableResult(false, "运行服务未连接，未开启桌面同步。");
                if (!result.Enabled)
                {
                    LoadLauncherControls();
                    LauncherSettingsStatus.Text = result.Message ?? "已取消，桌面同步保持关闭。";
                    return;
                }
                completionMessage = result.Message;
            }
            var current = _dataStore.Settings.Current;
            var old = current.Launcher;
            var next = SettingsPageEditor.Clone(current).Launcher;
            next.AutoAddDesktopAppsEnabled = requested;
            current.Launcher = next;
            try { _dataStore.Settings.Save(); }
            catch { current.Launcher = old; throw; }
            LauncherSettingsChanged?.Invoke(this, next);
            LoadLauncherControls();
            LauncherSettingsStatus.Text = requested
                ? (string.IsNullOrWhiteSpace(completionMessage) ? string.Empty : completionMessage + " ") + "桌面同步已开启；新增加入、改名更新，删除时移除对应自动管理入口。"
                : "桌面同步已关闭；当前启动台入口保留。";
        }
        catch (Exception error)
        {
            LoadLauncherControls();
            LauncherSettingsStatus.Text = "桌面同步设置未能完成：" + error.Message;
        }
        finally { _launcherOrganizationBusy = false; UpdateLauncherOrganizationControls(); }
    }

    private async void OnScanDesktopLauncherApps(object sender, RoutedEventArgs e)
    {
        if (_launcherOrganizationBusy || _dataStore is null) return;
        _launcherOrganizationBusy = true;
        UpdateLauncherOrganizationControls();
        try
        {
            LauncherSettingsStatus.Text = "正在扫描桌面项目…";
            LauncherSettingsStatus.Text = ScanDesktopAppsAsync is { } scan
                ? await scan() : "运行服务未连接，未扫描桌面项目。";
        }
        catch (Exception error) { LauncherSettingsStatus.Text = "桌面扫描未能完成：" + error.Message; }
        finally { _launcherOrganizationBusy = false; UpdateLauncherOrganizationControls(); }
    }

    private async void OnOrganizeExistingLauncherApps(object sender, RoutedEventArgs e)
    {
        if (_launcherOrganizationBusy || _dataStore is null || !_dataStore.Settings.Current.Launcher.GroupModeEnabled) return;
        _launcherOrganizationBusy = true;
        UpdateLauncherOrganizationControls();
        try
        {
            LauncherSettingsStatus.Text = "正在准备整理预览…";
            LauncherSettingsStatus.Text = OrganizeLauncherAppsAsync is { } organize
                ? await organize() : "运行服务未连接，未整理已有项目。";
        }
        catch (Exception error) { LauncherSettingsStatus.Text = "整理未能完成：" + error.Message; }
        finally { _launcherOrganizationBusy = false; UpdateLauncherOrganizationControls(); }
    }
}
