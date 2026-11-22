using System.Windows;
using System.Windows.Controls;
using GlassDesk.Core.Models.Settings;
using GlassDesk.UI.Theming;
using Microsoft.Win32;

namespace GlassDesk.Settings;

public partial class SettingsWindow
{
    private bool _loadingNaiwaControls;
    private int _lastAudibleNaiwaVolume = 100;

    public Func<Task<string?>>? PrepareNaiwaAsync { get; set; }
    public Func<bool>? CanEnableNaiwa { get; set; }
    public Func<string, Task<string?>>? ImportNaiwaAssetsAsync { get; set; }
    public Func<bool>? HasImportedNaiwaAssets { get; set; }
    public Func<Task<(bool AssetsReady, string Status)>>? RemoveNaiwaAssetsAsync { get; set; }
    public Func<string, string?>? RunNaiwaAction { get; set; }
    public event EventHandler? NaiwaSettingsChanged;

    public void ShowNaiwaStatus(string? status)
    {
        NaiwaStatus.Text = status ?? string.Empty;
        NaiwaStatus.Visibility = string.IsNullOrEmpty(status) ? Visibility.Collapsed : Visibility.Visible;
    }

    public void RefreshNaiwaAssetControls()
    {
        var canEnable = CanEnableNaiwa?.Invoke() == true;
        var wasLoading = _loadingNaiwaControls;
        _loadingNaiwaControls = true;
        try
        {
            NaiwaEnabledCheck.IsEnabled = canEnable;
            NaiwaEnabledCheck.IsChecked = canEnable && _dataStore?.Settings.Current.Appearance.NaiwaEnabled == true;
            NaiwaRemoveButton.IsEnabled = RemoveNaiwaAssetsAsync is not null && HasImportedNaiwaAssets?.Invoke() == true;
        }
        finally
        {
            _loadingNaiwaControls = wasLoading;
        }
    }

    private void SetNaiwaControlsBusy(bool busy)
    {
        SettingsScroll.IsEnabled = SettingsPageList.IsEnabled = !busy;
        if (busy) ResetPageButton.IsEnabled = false;
        else UpdateResetPageButton();
    }

    private void LoadNaiwaControls()
    {
        if (_dataStore is not { } data) return;
        _loadingNaiwaControls = true;
        try
        {
            var current = data.Settings.Current;
            var options = current.NaiwaOptions ?? new NaiwaOptions();
            RefreshNaiwaAssetControls();
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
            SetNaiwaControlsBusy(true);
            string? error;
            try
            {
                // Let input callers return before validating the installed component.
                await System.Windows.Threading.Dispatcher.Yield();
                if (!ReferenceEquals(_dataStore, data) || Dispatcher.HasShutdownStarted) return;
                error = PrepareNaiwaAsync is { } prepare ? await prepare() : "奶蛙组件不可用。";
            }
            finally
            {
                if (ReferenceEquals(_dataStore, data)) SetNaiwaControlsBusy(false);
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

    private async void OnNaiwaImportClick(object sender, RoutedEventArgs e)
    {
        if (_dataStore is not { } data || ImportNaiwaAssetsAsync is not { } import) return;
        var picker = new OpenFileDialog
        {
            Title = "选择官方奶蛙素材包",
            Filter = "奶蛙素材包 (*.zip)|*.zip",
        };
        if (picker.ShowDialog(this) != true) return;

        SetNaiwaControlsBusy(true);
        ShowNaiwaStatus("正在校验并导入素材包…");
        try
        {
            var status = await import(picker.FileName);
            if (!ReferenceEquals(_dataStore, data) || Dispatcher.HasShutdownStarted) return;
            ShowNaiwaStatus(status ?? (data.Settings.Current.Appearance.NaiwaEnabled
                ? "素材包已导入。" : "素材包已导入。现在可以启用黄色回收站。"));
        }
        finally
        {
            if (ReferenceEquals(_dataStore, data))
            {
                RefreshNaiwaAssetControls();
                SetNaiwaControlsBusy(false);
            }
        }
    }

    private async void OnNaiwaRemoveClick(object sender, RoutedEventArgs e)
    {
        if (_dataStore is not { } data || RemoveNaiwaAssetsAsync is not { } remove
            || HasImportedNaiwaAssets?.Invoke() != true) return;

        SetNaiwaControlsBusy(true);
        ShowNaiwaStatus("正在移除素材包…");
        try
        {
            var (assetsReady, status) = await remove();
            if (!ReferenceEquals(_dataStore, data) || Dispatcher.HasShutdownStarted) return;
            if (!assetsReady && data.Settings.Current.Appearance.NaiwaEnabled)
            {
                var snapshot = SettingsPageEditor.Clone(data.Settings.Current);
                data.Settings.Current.Appearance.NaiwaEnabled = false;
                SaveNaiwaPageSnapshot(snapshot);
                LoadNaiwaControls();
            }
            ShowNaiwaStatus(status);
        }
        finally
        {
            if (ReferenceEquals(_dataStore, data))
            {
                RefreshNaiwaAssetControls();
                SetNaiwaControlsBusy(false);
            }
        }
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

}
