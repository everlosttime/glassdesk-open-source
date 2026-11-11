using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using GlassDesk.Core.Models.Settings;
using GlassDesk.UI.Theming;

namespace GlassDesk.Settings;

public partial class SettingsWindow
{
    private bool _loadingNaiwaControls;
    private int _lastAudibleNaiwaVolume = 100;

    public Func<Func<string?>, Task<string?>>? PrepareNaiwaAsync { get; set; }
    public Func<string, string?>? RunNaiwaAction { get; set; }
    public event EventHandler? NaiwaSettingsChanged;

    public void ShowNaiwaStatus(string? status) => NaiwaStatus.Text = status ?? string.Empty;

    private void LoadNaiwaControls()
    {
        if (_dataStore is not { } data) return;
        _loadingNaiwaControls = true;
        try
        {
            var current = data.Settings.Current;
            var options = current.NaiwaOptions ?? new NaiwaOptions();
            NaiwaEnabledCheck.IsChecked = current.Appearance.NaiwaEnabled;
            NaiwaScaleSlider.Value = options.Scale;
            NaiwaVolumeSlider.Value = options.Volume;
            NaiwaSoundCheck.IsChecked = options.Sound;
            NaiwaRecycleCheck.IsChecked = options.Recycle;
            NaiwaAutoHideCheck.IsChecked = options.AutoHide;
            RefreshNaiwaMuteButton();
        }
        finally
        {
            _loadingNaiwaControls = false;
        }
    }

    private async void OnNaiwaEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingNaiwaControls || _dataStore is not { } data) return;

        var enabled = NaiwaEnabledCheck.IsChecked == true;
        if (enabled == data.Settings.Current.Appearance.NaiwaEnabled) return;
        if (enabled)
        {
            SettingsScroll.IsEnabled = SettingsPageList.IsEnabled = ResetPageButton.IsEnabled = false;
            string? error;
            try
            {
                // Let automation and input callers return before opening the modal file picker.
                await System.Windows.Threading.Dispatcher.Yield();
                if (!ReferenceEquals(_dataStore, data) || Dispatcher.HasShutdownStarted) return;
                error = PrepareNaiwaAsync is { } prepare ? await prepare(SelectNaiwaArchive) : "奶蛙组件不可用。";
            }
            finally
            {
                if (ReferenceEquals(_dataStore, data))
                {
                    SettingsScroll.IsEnabled = SettingsPageList.IsEnabled = true;
                    UpdateResetPageButton();
                }
            }
            if (!ReferenceEquals(_dataStore, data) || Dispatcher.HasShutdownStarted) return;
            if (error is not null)
            {
                LoadNaiwaControls();
                ShowNaiwaStatus(error);
                return;
            }
        }

        var snapshot = SettingsPageEditor.Clone(data.Settings.Current);
        data.Settings.Current.Appearance.NaiwaEnabled = enabled;
        SaveNaiwaPageSnapshot(snapshot);
    }

    private void OnNaiwaOptionsChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingNaiwaControls || _dataStore is not { } data) return;
        var current = data.Settings.Current;
        var snapshot = SettingsPageEditor.Clone(current);
        current.NaiwaOptions = new NaiwaOptions
        {
            Scale = (int)Math.Round(NaiwaScaleSlider.Value),
            Volume = (int)Math.Round(NaiwaVolumeSlider.Value),
            Sound = NaiwaSoundCheck.IsChecked == true,
            Recycle = NaiwaRecycleCheck.IsChecked == true,
            AutoHide = NaiwaAutoHideCheck.IsChecked == true,
        };
        SaveNaiwaPageSnapshot(snapshot);
        RefreshNaiwaMuteButton();
    }

    private void SaveNaiwaPageSnapshot(GlassDeskSettings snapshot)
    {
        SavePageSnapshot(snapshot, "回收站", NaiwaSettingsStatus);
        SyncNaiwaAppearance();
    }

    private void SyncNaiwaAppearance()
    {
        // Reset and rollback can replace the Appearance object.
        var appearance = _dataStore!.Settings.Current.Appearance;
        if (ThemeService.Current is { } theme && !ReferenceEquals(theme.Appearance, appearance))
            theme.Update(appearance);
    }

    private void RefreshNaiwaMuteButton()
    {
        var volume = (int)Math.Round(NaiwaVolumeSlider.Value);
        if (volume > 0) _lastAudibleNaiwaVolume = volume;
        NaiwaMuteButton.Content = volume == 0 ? "取消静音" : "静音";
    }

    private void OnNaiwaMuteClick(object sender, RoutedEventArgs e)
    {
        NaiwaVolumeSlider.Value = NaiwaVolumeSlider.Value == 0 ? _lastAudibleNaiwaVolume : 0;
    }

    private void OnNaiwaActionClick(object sender, RoutedEventArgs e)
    {
        if (_dataStore is null || sender is not Button { Tag: string action }) return;
        NaiwaSettingsStatus.Text = RunNaiwaAction is { } run
            ? run(action) ?? string.Empty
            : "回收站运行服务未连接。";
    }

    private string? SelectNaiwaArchive()
    {
        ShowNaiwaStatus("首次启用需要导入官方 naiwa-pet-v2.5.1-portable.zip。");
        var dialog = new OpenFileDialog
        {
            Title = "导入奶蛙 v2.5.1 官方素材包",
            Filter = "ZIP 压缩包 (*.zip)|*.zip",
            FileName = "naiwa-pet-v2.5.1-portable.zip",
            CheckFileExists = true,
        };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }
}
