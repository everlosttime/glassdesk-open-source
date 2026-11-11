using System.IO;
using System.Windows;
using GlassDesk.Core.Models.Settings;
using GlassDesk.UI.Settings;
using Microsoft.Win32;

namespace GlassDesk.Settings;

public partial class SettingsWindow
{
    public Func<NearbySettings, Task<string?>>? NearbySettingsApply { get; set; }
    public Func<Task>? BeforeDataRestore { get; set; }
    public Func<Task>? AfterDataRestoreFailure { get; set; }
    public Action? OpenNearbyPanel { get; set; }
    public void ReloadNearbySettings() => LoadNearbyControls();
    public void UpdateNearbyStatus(string status) => NearbyServiceStatus.Text = status;

    private void LoadNearbyControls()
    {
        if (_dataStore is null) return;
        var current = _dataStore.Settings.Current;
        NearbyEnabledCheck.IsChecked = current.Nearby.Enabled;
        NearbyDeviceNameBox.Text = current.Nearby.DeviceName;
        NearbyReceiveDirectoryBox.Text = current.Nearby.ReceiveDirectory ?? "";
        NearbySettingsStatus.Text = current.Nearby.Enabled ? "已设置开启；运行状态由后台服务显示。" : "未开启；不监听网络。";
    }

    private void OnOpenNearbyClick(object sender, RoutedEventArgs e) => OpenNearbyPanel?.Invoke();
    private void OnChooseNearbyDirectory(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "选择附近传输接收目录" };
        if (picker.ShowDialog(this) == true) NearbyReceiveDirectoryBox.Text = picker.FolderName;
    }

    private async void OnSaveNearbyClick(object sender, RoutedEventArgs e)
    {
        await SaveNearbyAsync(new NearbySettings { Enabled = NearbyEnabledCheck.IsChecked == true,
            DeviceName = NearbyDeviceNameBox.Text, ReceiveDirectory = NearbyReceiveDirectoryBox.Text });
    }

    private async Task SaveNearbyAsync(NearbySettings next)
    {
        if (_dataStore is null || !SaveNearbyButton.IsEnabled) return;
        if (NearbySettingsApply is null) { NearbySettingsStatus.Text = "后台服务尚未连接。"; return; }
        next.Normalize();
        if (next.ReceiveDirectory is { } path && !Path.IsPathFullyQualified(path))
        { NearbySettingsStatus.Text = "接收目录需要完整路径。"; return; }
        var store = _dataStore;
        var old = SettingsPageEditor.Clone(store.Settings.Current);
        SaveNearbyButton.IsEnabled = false;
        try
        {
            var error = await NearbySettingsApply(next);
            if (error is not null) { NearbySettingsStatus.Text = error; return; }
            if (_dataStore is null) return;
            store.Settings.Current.Nearby = next;
            try { store.Settings.Save(); }
            catch
            {
                store.Settings.Current.Nearby = old.Nearby;
                await NearbySettingsApply(old.Nearby);
                throw;
            }
            DockSettingsHost.Current?.Update(store.Settings.Current.Dock);
            LoadDockControls(); LoadNearbyControls();
            NearbySettingsStatus.Text = next.Enabled ? "已开启，关闭面板后继续待命。" : "已关闭，活动任务已取消并停止监听。";
        }
        catch (Exception error) { NearbySettingsStatus.Text = "保存失败，原设置已保留：" + error.Message; }
        finally { SaveNearbyButton.IsEnabled = _dataStore is not null; }
    }
}
