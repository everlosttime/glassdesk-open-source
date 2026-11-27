using System.Windows;

namespace GlassDesk.Settings;

public partial class SettingsWindow
{
    private bool _loadingStageManager = true;
    public Func<bool, string?>? StageManagerApply { get; set; }
    public Action? StageWindowSelectionRequested { get; set; }

    private void OnSelectStageWindowClick(object sender, RoutedEventArgs e) => StageWindowSelectionRequested?.Invoke();

    public void LoadStageManagerControl(string? status = null)
    {
        _loadingStageManager = true;
        try
        {
            StageManagerCheck.IsChecked = _dataStore?.Settings.Current.StageManagerEnabled == true;
            StageManagerCheck.IsEnabled = _dataStore is not null;
            StageManagerStatus.Text = status ?? "";
        }
        finally { _loadingStageManager = false; }
    }

    private void OnStageManagerChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingStageManager || _dataStore is null || StageManagerApply is null) return;
        LoadStageManagerControl(StageManagerApply(StageManagerCheck.IsChecked == true));
    }
}
