using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.Win32;
using GlassDesk.Core.Input;
using GlassDesk.Core.Models;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Platform.Installation;
using GlassDesk.UI.Settings;
using GlassDesk.UI.Theming;

namespace GlassDesk.Settings;

public partial class SettingsWindow
{
    private bool _userDataDeletionPending;
    private InstallationInfo? _installationInfo;

    /// <summary>应用快捷操作和显示器设置并负责持久化。返回错误说明；null 表示成功。</summary>
    public Func<ShortcutSettings, DisplaySettings, string?>? InteractionSettingsApply { get; set; }

    /// <summary>恢复完整数据后通知宿主立即结束进程，避免旧的运行时状态覆盖恢复结果。</summary>
    public event EventHandler? DataRestored;

    /// <summary>用户确认删除全部 GlassDesk 数据后请求宿主停止所有写入并执行维护。</summary>
    public event EventHandler? UserDataDeletionRequested;

    /// <summary>用户确认启动经核验的卸载程序后通知宿主。</summary>
    public event EventHandler? UninstallRequested;

    /// <summary>连接只读核验结果；设置窗口本身不读取或执行卸载登记。</summary>
    public void AttachInstallation(InstallationInfo installationInfo)
    {
        ArgumentNullException.ThrowIfNull(installationInfo);
        _installationInfo = installationInfo;
        UninstallButton.IsEnabled = installationInfo.State == InstallationState.VerifiedInstalled;
        InstallationStatus.Text = installationInfo.State switch
        {
            InstallationState.Portable => "当前程序以便携方式运行。退出后可自行删除程序文件夹；用户数据需在“备份与恢复”页单独管理。",
            InstallationState.VerifiedInstalled => $"已核验此安装及卸载程序：{installationInfo.UninstallerPath}\n卸载程序不会删除 GlassDesk 用户数据。",
            _ => "安装登记不完整或与当前程序不匹配，已停用卸载入口。" + Environment.NewLine + installationInfo.Error,
        };
    }

    /// <summary>在设置页打开前重新读取安装登记，避免沿用启动时的过期状态。</summary>
    public void RefreshInstallationStatus(Func<InstallationInfo> inspect)
    {
        ArgumentNullException.ThrowIfNull(inspect);
        AttachInstallation(inspect());
    }

    /// <summary>报告启动卸载程序失败并允许用户稍后重试。</summary>
    public void ShowUninstallLaunchError(string message)
    {
        InstallationStatus.Text = "无法启动已核验的卸载程序：" + message;
        UninstallButton.IsEnabled = _installationInfo?.State == InstallationState.VerifiedInstalled;
    }

    /// <summary>禁用设置写入，供宿主在数据维护或退出流程开始时调用。</summary>
    public void PrepareForUserDataMaintenance()
    {
        SettingsScroll.IsEnabled = false;
        SettingsPageList.IsEnabled = false;
        ResetPageButton.IsEnabled = false;
        _dataStore = null;
    }

    /// <summary>展示运行服务发现的快捷操作状态，例如快捷键被其他程序占用。</summary>
    public void ShowInteractionStatus(string message) => InteractionSettingsStatus.Text = message ?? string.Empty;

    /// <summary>读取 Windows 桌面图标当前显示状态；null 表示不支持或不可用。</summary>
    public Func<bool?>? DesktopIconsRead { get; set; }

    /// <summary>请求 Windows 显示或隐藏桌面图标；返回错误说明，null 表示成功。</summary>
    public Func<bool, string?>? DesktopIconsSet { get; set; }

    private void LoadAppearanceControls()
    {
        if (_dataStore is null) return;
        _loadingAppearanceControls = true;
        try
        {
            var appearance = _dataStore.Settings.Current.Appearance;
            LauncherMaterialCombo.SelectedIndex = MaterialIndex(appearance.GetGlassMaterial(GlassMaterialTarget.Launcher));
            DockMaterialCombo.SelectedIndex = MaterialIndex(appearance.GetGlassMaterial(GlassMaterialTarget.Dock));
            StatusBarMaterialCombo.SelectedIndex = MaterialIndex(appearance.GetGlassMaterial(GlassMaterialTarget.StatusBar));
            ControlCenterMaterialCombo.SelectedIndex = MaterialIndex(appearance.GetGlassMaterial(GlassMaterialTarget.ControlCenter));
            DesktopLayerModeCombo.SelectedIndex = appearance.PinBarsToDesktop ? 1 : 0;
            LaunchpadIconStyleCombo.SelectedIndex = appearance.LaunchpadIconFilled ? 0 : 1;
            DockTransparencySlider.Value = appearance.DockGlassTransparency;
            LauncherTransparencySlider.Value = appearance.LauncherGlassTransparency;
            StatusBarTransparencySlider.Value = appearance.GetGlassTransparency(GlassMaterialTarget.StatusBar);
            ControlCenterTransparencySlider.Value = appearance.GetGlassTransparency(GlassMaterialTarget.ControlCenter);
            DockTransparencyValue.Text = appearance.DockGlassTransparency.ToString("P0", CultureInfo.CurrentCulture);
            LauncherTransparencyValue.Text = appearance.LauncherGlassTransparency.ToString("P0", CultureInfo.CurrentCulture);
            StatusBarTransparencyValue.Text = appearance.GetGlassTransparency(GlassMaterialTarget.StatusBar).ToString("P0", CultureInfo.CurrentCulture);
            ControlCenterTransparencyValue.Text = appearance.GetGlassTransparency(GlassMaterialTarget.ControlCenter).ToString("P0", CultureInfo.CurrentCulture);
            DockBuiltinIconBrightnessSlider.Value = appearance.DockBuiltinIconBrightness;
            LauncherTextBrightnessSlider.Value = appearance.LauncherTextBrightness;
            StatusBarTextBrightnessSlider.Value = appearance.StatusBarTextBrightness;
            StatusBarTextBrightnessValue.Text = appearance.StatusBarTextBrightness.ToString("P0", CultureInfo.CurrentCulture);
            DockBuiltinIconBrightnessValue.Text = appearance.DockBuiltinIconBrightness.ToString("P0", CultureInfo.CurrentCulture);
            LauncherTextBrightnessValue.Text = appearance.LauncherTextBrightness.ToString("P0", CultureInfo.CurrentCulture);
            ReduceMotionCheck.IsChecked = appearance.ReduceMotion;
            AppearanceStatus.Text = "设置面板固定使用深色不透明背景；各组件的玻璃透明度可单独调节。";
        }
        finally { _loadingAppearanceControls = false; }
    }

    private static int MaterialIndex(GlassMaterialMode material) => material switch
    {
        GlassMaterialMode.FrostedGlass => 1,
        GlassMaterialMode.PlainGlass => 2,
        _ => 0,
    };

    private static GlassMaterialMode MaterialFromIndex(int index) => index switch
    {
        1 => GlassMaterialMode.FrostedGlass,
        2 => GlassMaterialMode.PlainGlass,
        _ => GlassMaterialMode.AndroidLiquidGlass,
    };

    private void OnAppearanceControlChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingAppearanceControls || _dataStore is null) return;
        var current = _dataStore.Settings.Current;
        var snapshot = SettingsPageEditor.Clone(current);
        if (ReferenceEquals(sender, LauncherMaterialCombo))
            current.Appearance.LauncherGlassMaterial = MaterialFromIndex(LauncherMaterialCombo.SelectedIndex);
        else if (ReferenceEquals(sender, DockMaterialCombo))
            current.Appearance.DockGlassMaterial = MaterialFromIndex(DockMaterialCombo.SelectedIndex);
        else if (ReferenceEquals(sender, StatusBarMaterialCombo))
            current.Appearance.StatusBarGlassMaterial = MaterialFromIndex(StatusBarMaterialCombo.SelectedIndex);
        else if (ReferenceEquals(sender, ControlCenterMaterialCombo))
            current.Appearance.ControlCenterGlassMaterial = MaterialFromIndex(ControlCenterMaterialCombo.SelectedIndex);
        current.Appearance.ReduceMotion = ReduceMotionCheck.IsChecked == true;
        current.Appearance.PinBarsToDesktop = DesktopLayerModeCombo.SelectedIndex == 1;
        current.Appearance.LaunchpadIconFilled = LaunchpadIconStyleCombo.SelectedIndex != 1;
        SavePageSnapshot(snapshot, "外观", AppearanceStatus);
        // A failed save replaces Appearance; the live theme must follow the restored snapshot.
        if (ThemeService.Current is { } theme && !ReferenceEquals(theme.Appearance, current.Appearance))
            theme.Update(current.Appearance);
    }

    private void OnAppearanceSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingAppearanceControls || !IsInitialized || _dataStore is null) return;
        var current = _dataStore.Settings.Current;
        var snapshot = SettingsPageEditor.Clone(current);
        if (ReferenceEquals(sender, DockTransparencySlider))
            current.Appearance.DockGlassTransparency = DockTransparencySlider.Value;
        else if (ReferenceEquals(sender, LauncherTransparencySlider))
            current.Appearance.LauncherGlassTransparency = LauncherTransparencySlider.Value;
        else if (ReferenceEquals(sender, StatusBarTransparencySlider))
            current.Appearance.StatusBarGlassTransparency = StatusBarTransparencySlider.Value;
        else if (ReferenceEquals(sender, ControlCenterTransparencySlider))
            current.Appearance.ControlCenterGlassTransparency = ControlCenterTransparencySlider.Value;
        else if (ReferenceEquals(sender, DockBuiltinIconBrightnessSlider))
            current.Appearance.DockBuiltinIconBrightness = DockBuiltinIconBrightnessSlider.Value;
        else if (ReferenceEquals(sender, LauncherTextBrightnessSlider))
            current.Appearance.LauncherTextBrightness = LauncherTextBrightnessSlider.Value;
        else if (ReferenceEquals(sender, StatusBarTextBrightnessSlider))
            current.Appearance.StatusBarTextBrightness = StatusBarTextBrightnessSlider.Value;
        else return;
        SavePageSnapshot(snapshot, ReferenceEquals(sender, StatusBarTextBrightnessSlider) ? "信息栏" : "外观",
            ReferenceEquals(sender, StatusBarTextBrightnessSlider) ? InformationBarSettingsStatus : AppearanceStatus);
        // Rollback replaces Appearance; refresh the preview's reference after a failed save.
        if (ThemeService.Current is { } theme && !ReferenceEquals(theme.Appearance, current.Appearance))
            theme.Update(current.Appearance);
        DockTransparencyValue.Text = current.Appearance.DockGlassTransparency.ToString("P0", CultureInfo.CurrentCulture);
        LauncherTransparencyValue.Text = current.Appearance.LauncherGlassTransparency.ToString("P0", CultureInfo.CurrentCulture);
        StatusBarTransparencyValue.Text = current.Appearance.GetGlassTransparency(GlassMaterialTarget.StatusBar).ToString("P0", CultureInfo.CurrentCulture);
        ControlCenterTransparencyValue.Text = current.Appearance.GetGlassTransparency(GlassMaterialTarget.ControlCenter).ToString("P0", CultureInfo.CurrentCulture);
        DockBuiltinIconBrightnessValue.Text = current.Appearance.DockBuiltinIconBrightness.ToString("P0", CultureInfo.CurrentCulture);
        LauncherTextBrightnessValue.Text = current.Appearance.LauncherTextBrightness.ToString("P0", CultureInfo.CurrentCulture);
        StatusBarTextBrightnessValue.Text = current.Appearance.StatusBarTextBrightness.ToString("P0", CultureInfo.CurrentCulture);
    }

    private void LoadInteractionControls()
    {
        if (_dataStore is null) return;
        try
        {
            var current = _dataStore.Settings.Current;
            ToggleLauncherHotkeyBox.Text = HotkeyText.Format(current.Shortcuts.ToggleLauncher);
            NextPageHotkeyBox.Text = HotkeyText.Format(current.Shortcuts.NextPage);
            PreviousPageHotkeyBox.Text = HotkeyText.Format(current.Shortcuts.PreviousPage);
            ToggleDesktopIconsHotkeyBox.Text = HotkeyText.Format(current.Shortcuts.ToggleDesktopIcons);
            ToggleDockRevealLockHotkeyBox.Text = HotkeyText.Format(current.Shortcuts.ToggleDockRevealLock);
            ToggleGameSleepHotkeyBox.Text = HotkeyText.Format(current.Shortcuts.ToggleGameSleep);
            InteractionSettingsStatus.Text = "快捷键录制完成后自动保存并应用。";
        }
        catch (Exception ex)
        {
            InteractionSettingsStatus.Text = "读取快捷操作设置失败：" + ex.Message;
        }
    }

    private void OnApplyInteractionSettingsClick(object sender, RoutedEventArgs e)
    {
        if (_dataStore is null) { InteractionSettingsStatus.Text = "设置数据尚未加载。"; return; }
        if (InteractionSettingsApply is null) { InteractionSettingsStatus.Text = "运行服务未连接，未保存更改。"; return; }
        if (!TryReadHotkey(ToggleLauncherHotkeyBox, out var toggle)
            || !TryReadHotkey(NextPageHotkeyBox, out var next) || !TryReadHotkey(PreviousPageHotkeyBox, out var previous)
            || !TryReadHotkey(ToggleDesktopIconsHotkeyBox, out var icons)
            || !TryReadHotkey(ToggleDockRevealLockHotkeyBox, out var dockRevealLock)
            || !TryReadHotkey(ToggleGameSleepHotkeyBox, out var gameSleep)) return;
        var shortcuts = new ShortcutSettings
        {
            ToggleLauncher = toggle, HideLauncher = new(), NextPage = next, PreviousPage = previous,
            ToggleDesktopIcons = icons, ToggleDockRevealLock = dockRevealLock, ToggleGameSleep = gameSleep,
        };
        var display = new DisplaySettings
        {
            DockDisplayId = _dataStore.Settings.Current.Display.DockDisplayId,
            LauncherFollowsPointer = _dataStore.Settings.Current.Display.LauncherFollowsPointer,
            SuppressTriggersWhenFullscreen = _dataStore.Settings.Current.Display.SuppressTriggersWhenFullscreen,
        };
        var candidate = SettingsPageEditor.Clone(_dataStore.Settings.Current);
        candidate.Shortcuts = shortcuts;
        candidate.Display = display;
        var validationError = SettingsPageEditor.Validate(candidate);
        if (validationError is not null) { LoadInteractionControls(); InteractionSettingsStatus.Text = "设置无效：" + validationError; return; }
        try
        {
            var error = InteractionSettingsApply(shortcuts, display);
            if (error is not null)
            {
                LoadInteractionControls();
                InteractionSettingsStatus.Text = "应用失败，已恢复原设置：" + error;
                return;
            }
            LoadInteractionControls();
            InteractionSettingsStatus.Text = "快捷操作设置已应用并保存。";
        }
        catch (Exception ex)
        {
            LoadInteractionControls();
            InteractionSettingsStatus.Text = "应用失败，已恢复原设置：" + ex.Message;
        }
    }

    private void OnShortcutRecordingCompleted(object? sender, EventArgs e)
    {
        if ((_dataStore is null || InteractionSettingsApply is null) && sender is ShortcutRecorder recorder)
            recorder.RestorePreviousBinding();
        OnApplyInteractionSettingsClick(sender ?? this, new RoutedEventArgs());
    }

    public bool TryCaptureRegisteredHotkey(nint packedChord)
    {
        var recorder = new[] { ToggleLauncherHotkeyBox, NextPageHotkeyBox,
            PreviousPageHotkeyBox, ToggleDesktopIconsHotkeyBox, ToggleDockRevealLockHotkeyBox, ToggleGameSleepHotkeyBox }
            .FirstOrDefault(control => control.IsRecording && control.IsKeyboardFocusWithin);
        if (recorder is null) return false;
        var packed = unchecked((uint)packedChord.ToInt64());
        var modifiers = System.Windows.Input.ModifierKeys.None;
        if ((packed & 1) != 0) modifiers |= System.Windows.Input.ModifierKeys.Alt;
        if ((packed & 2) != 0) modifiers |= System.Windows.Input.ModifierKeys.Control;
        if ((packed & 4) != 0) modifiers |= System.Windows.Input.ModifierKeys.Shift;
        if ((packed & 8) != 0) modifiers |= System.Windows.Input.ModifierKeys.Windows;
        var virtualKey = (int)(packed >> 16);
        if (virtualKey is 5 or 6) recorder.CaptureMouseButton(virtualKey == 5 ? 1 : 2, modifiers);
        else if (virtualKey is 0x100 or 0x101) recorder.CaptureWheel(virtualKey == 0x100 ? 120 : -120, modifiers);
        else recorder.CaptureKey(System.Windows.Input.KeyInterop.KeyFromVirtualKey(virtualKey), modifiers);
        return true;
    }

    private bool TryReadHotkey(ShortcutRecorder box, out HotkeyDefinition value)
    {
        if (HotkeyText.TryParse(box.Text, out value, out var error)) return true;
        InteractionSettingsStatus.Text = $"{AutomationProperties.GetName(box)}无法识别：{error}";
        box.Focus();
        return false;
    }

    private void OnCreateBackupClick(object sender, RoutedEventArgs e)
    {
        if (_dataStore is null) { BackupStatus.Text = "设置数据尚未加载。"; return; }
        var dialog = new OpenFolderDialog { Title = "选择 GlassDesk 备份保存文件夹", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        var result = _dataStore.Backup.BackupTo(dialog.FolderName);
        BackupStatus.Text = result.Succeeded
            ? $"备份已创建：{result.Directory}（{result.Files.Count} 个文件）。"
            : result.Error ?? "备份未能完成。";
    }

    private void OnDeleteUserDataClick(object sender, RoutedEventArgs e)
    {
        if (_userDataDeletionPending || _dataStore is null) return;
        if (UserDataDeletionRequested is null)
        {
            BackupStatus.Text = "运行宿主未连接安全维护流程，未执行删除。";
            return;
        }

        var dataRoot = _dataStore.Paths.RootDirectory;
        var answer = MessageBox.Show(
            this,
            $"将删除以下目录中的全部 GlassDesk 用户数据：\n\n{dataRoot}\n\n包括设置、应用库、布局、自定义图标、内部备份和日志。不会卸载程序、删除此目录之外的导入文件或备份，也不会更改登录启动项。建议先使用上方“创建备份”并把备份保存到此目录之外。\n\n确认后 GlassDesk 将停止并退出。要继续吗？",
            "删除全部 GlassDesk 数据",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        _userDataDeletionPending = true;
        DeleteUserDataButton.IsEnabled = false;
        PrepareForUserDataMaintenance();
        UserDataDeletionRequested.Invoke(this, EventArgs.Empty);
    }

    private void OnUninstallClick(object sender, RoutedEventArgs e)
    {
        if (_installationInfo?.State != InstallationState.VerifiedInstalled || _userDataDeletionPending) return;
        if (UninstallRequested is null)
        {
            ShowUninstallLaunchError("运行宿主未连接卸载流程。");
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"将启动已核验的 GlassDesk 卸载程序：\n\n{_installationInfo.UninstallerPath}\n\n卸载仅移除已安装的程序文件，不会删除 GlassDesk 用户数据。要继续吗？",
            "卸载 GlassDesk",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        UninstallButton.IsEnabled = false;
        UninstallRequested.Invoke(this, EventArgs.Empty);
    }

    private async void OnRestoreBackupClick(object sender, RoutedEventArgs e)
    {
        if (_dataStore is null) { BackupStatus.Text = "设置数据尚未加载。"; return; }
        var dialog = new OpenFolderDialog { Title = "选择 GlassDesk 备份文件夹", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        if (DataRestored is null)
        {
            BackupStatus.Text = "运行宿主未连接安全退出通知，未执行恢复。";
            return;
        }
        var answer = MessageBox.Show(this,
            "恢复会用所选备份替换当前 GlassDesk 数据。系统会先保存当前数据副本。恢复后请重新启动 GlassDesk 以载入全部内容。要继续吗？",
            "恢复 GlassDesk 备份", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        if (BeforeDataRestore is not null)
        {
            try { await BeforeDataRestore(); }
            catch (Exception error) { BackupStatus.Text = "未能停止后台任务，未执行恢复：" + error.Message; return; }
        }
        var result = _dataStore.Backup.RestoreFrom(dialog.FolderName);
        if (!result.Succeeded)
        {
            if (AfterDataRestoreFailure is not null) await AfterDataRestoreFailure();
            BackupStatus.Text = result.Error ?? "恢复未能完成。";
            return;
        }
        BackupStatus.Text = $"已从备份恢复 {result.Files.Count} 个文件。当前数据的安全副本位于：{result.SafetyCopyDirectory}。请重新启动 GlassDesk。为避免写回恢复前的内存设置，本窗口的设置编辑已停用。";
        SettingsScroll.IsEnabled = false;
        SettingsPageList.IsEnabled = false;
        ResetPageButton.IsEnabled = false;
        _dataStore = null;
        DataRestored?.Invoke(this, EventArgs.Empty);
    }

    private async void OnResetPageClick(object sender, RoutedEventArgs e)
    {
        if (_dataStore is null || !CanResetSelectedPage) return;
        var page = PlannedPages[SettingsPageList.SelectedIndex];
        if (page == "附近传输") { await SaveNearbyAsync(new NearbySettings()); return; }
        var target = _dataStore.Settings.Current;
        var old = SettingsPageEditor.Clone(target);
        var reset = SettingsPageEditor.ResetPage(old, page);
        var validationError = SettingsPageEditor.Validate(reset);
        if (validationError is not null) { StatusFor(page).Text = "无法恢复默认：" + validationError; return; }
        if (page == "快捷操作")
        {
            if (InteractionSettingsApply is null) { InteractionSettingsStatus.Text = "运行服务未连接，未保存更改。"; return; }
            var error = InteractionSettingsApply(reset.Shortcuts, reset.Display);
            if (error is not null) { InteractionSettingsStatus.Text = "恢复默认失败，已恢复原设置：" + error; LoadInteractionControls(); return; }
            LoadInteractionControls();
            InteractionSettingsStatus.Text = "本页已恢复默认设置。";
            return;
        }
        try
        {
            SettingsPageEditor.Apply(target, reset);
            _dataStore.Settings.Save();
        }
        catch (Exception ex)
        {
            SettingsPageEditor.Apply(target, old);
            ReloadPage(page);
            StatusFor(page).Text = "恢复默认失败，已恢复原设置：" + ex.Message;
            return;
        }
        NotifyPageApplied(page);
        ReloadPage(page);
    }

    private void SavePageSnapshot(GlassDeskSettings snapshot, string page, TextBlock status)
    {
        var validationError = SettingsPageEditor.Validate(_dataStore!.Settings.Current);
        if (validationError is not null)
        {
            SettingsPageEditor.Apply(_dataStore.Settings.Current, snapshot);
            LoadAppearanceControls();
            LoadDockControls();
            if (page == "回收站") LoadNaiwaControls();
            status.Text = "设置无效，已恢复原设置：" + validationError;
            return;
        }
        try
        {
            _dataStore!.Settings.Save();
        }
        catch (Exception ex)
        {
            SettingsPageEditor.Apply(_dataStore!.Settings.Current, snapshot);
            LoadAppearanceControls();
            LoadDockControls();
            if (page == "回收站") LoadNaiwaControls();
            status.Text = "保存失败，已恢复原设置：" + ex.Message;
            return;
        }
        NotifyPageApplied(page);
        status.Text = "已保存并应用。";
    }

    private void NotifyPageApplied(string page)
    {
        var current = _dataStore!.Settings.Current;
        if (page == "外观") ThemeService.Current?.Update(current.Appearance);
        if (page is "Dock" or "信息栏")
        {
            DockSettingsHost.Current?.Update(current.Dock);
            ThemeService.Current?.Update(current.Appearance);
        }
        if (page == "启动台") LauncherSettingsChanged?.Invoke(this, current.Launcher);
        if (page == "性能") PerformanceSettingsChanged?.Invoke(this, EventArgs.Empty);
        if (page == "回收站") NaiwaSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ReloadPage(string page)
    {
        switch (page)
        {
            case "外观": LoadAppearanceControls(); ThemeService.Current?.Update(_dataStore!.Settings.Current.Appearance); break;
            case "Dock": LoadDockControls(); DockSettingsHost.Current?.Update(_dataStore!.Settings.Current.Dock); break;
            case "启动台": LoadLauncherControls(); LauncherSettingsChanged?.Invoke(this, _dataStore!.Settings.Current.Launcher); break;
            case "快捷操作": LoadInteractionControls(); break;
            case "性能": LoadPerformanceControls(); break;
            case "回收站":
                LoadNaiwaControls();
                SyncNaiwaAppearance();
                NaiwaSettingsStatus.Text = "本页已恢复默认设置。";
                break;
            case "附近传输": LoadNearbyControls(); break;
        }
    }

    private TextBlock StatusFor(string page) => page switch
    {
        "外观" => AppearanceStatus, "Dock" => DockSettingsStatus, "启动台" => LauncherSettingsStatus,
        "快捷操作" => InteractionSettingsStatus, "性能" => PerformanceSettingsStatus, "附近传输" => NearbySettingsStatus,
        "回收站" => NaiwaSettingsStatus,
        _ => throw new ArgumentOutOfRangeException(nameof(page), page, "该分类没有可编辑设置。"),
    };
}
