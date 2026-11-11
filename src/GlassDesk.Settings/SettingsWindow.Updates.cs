using System.Windows;

namespace GlassDesk.Settings;

public partial class SettingsWindow
{
    public event EventHandler? CheckForUpdatesRequested;
    public event EventHandler? CloudDownloadsRequested;

    public void ShowUpdateStatus(string version, string status, bool checking = false)
    {
        ProductVersionText.Text = "GlassDesk " + version;
        UpdateStatusText.Text = status;
        CheckUpdateButton.IsEnabled = !checking && !_userDataDeletionPending;
        CloudDownloadButton.IsEnabled = !_userDataDeletionPending;
    }

    private void OnCheckUpdateClick(object sender, RoutedEventArgs e) =>
        CheckForUpdatesRequested?.Invoke(this, EventArgs.Empty);

    private void OnCloudDownloadClick(object sender, RoutedEventArgs e) =>
        CloudDownloadsRequested?.Invoke(this, EventArgs.Empty);
}
