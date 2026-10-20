using System.Windows;
using GlassDesk.Core.Models.Settings;

namespace GlassDesk.Settings;

public partial class SettingsWindow
{
    private bool _loadingPerformanceControls = true;
    public event EventHandler? PerformanceSettingsChanged;

    private void LoadPerformanceControls()
    {
        _loadingPerformanceControls = true;
        try
        {
            AutoSuspendWhenFullscreenCheck.IsChecked = _dataStore?.Settings.Current.Performance.AutoSuspendWhenFullscreen ?? true;
            AutoSuspendWhenFullscreenCheck.IsEnabled = _dataStore is not null;
        }
        finally { _loadingPerformanceControls = false; }
    }

    private void OnPerformanceControlChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingPerformanceControls || _dataStore is null) return;
        var current = _dataStore.Settings.Current;
        var previous = current.Performance;
        try
        {
            current.Performance = new PerformanceSettings { AutoSuspendWhenFullscreen = AutoSuspendWhenFullscreenCheck.IsChecked == true };
            _dataStore.Settings.Save();
        }
        catch (Exception error)
        {
            current.Performance = previous;
            LoadPerformanceControls();
            PerformanceSettingsStatus.Text = "保存失败，已恢复原设置：" + error.Message;
            return;
        }
        PerformanceSettingsChanged?.Invoke(this, EventArgs.Empty);
        PerformanceSettingsStatus.Text = "已保存并应用。窗口化游戏可在托盘手动启用游戏休眠。";
    }
}
