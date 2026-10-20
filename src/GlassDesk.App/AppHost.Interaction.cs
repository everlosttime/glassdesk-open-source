using System.IO;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Threading;
using GlassDesk.App.Startup;
using GlassDesk.Core.Input;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Platform;
using GlassDesk.Platform.Displays;
using GlassDesk.Launcher;
using GlassDesk.UI.Controls;

namespace GlassDesk.App;

internal sealed partial class AppHost
{
    private InteractionController? _interaction;
    private int _displayRefreshPending;
    private bool _restoreDockAfterDisplayChange, _restoreLauncherAfterDisplayChange, _restoreSettingsAfterDisplayChange;

    internal static void ToggleLauncherFromDock(
        LauncherWindow? launcher,
        PixelRect? sourceBounds,
        Action<PixelRect?> showLauncher)
    {
        ArgumentNullException.ThrowIfNull(showLauncher);
        if (launcher is null) return;

        // Mouse-up can arrive after deactivation has already started closing.
        // The Dock button still means close until the launcher is hidden.
        if (launcher.IsVisible)
        {
            launcher.CloseWithTransition();
            return;
        }

        showLauncher(sourceBounds);
    }

    private void InitializeInteractionController()
    {
        if (_data is null || _messageWindow is null || _launcher is null || _settings is null) return;
        _interaction = new InteractionController(
            _data,
            _primaryDisplaySession ?? throw new InvalidOperationException("Primary Display must be captured before interaction starts."),
            _messageWindow.Handle,
            _application.Dispatcher,
            CanExecuteInteraction,
            ExecuteInteraction,
            ReportInteractionError);

        _launcher.NavigationAvailabilityChanged += OnLauncherNavigationAvailabilityChanged;
        _interaction.SetLauncherAvailability(_launcher.IsVisible, _launcher.CanNavigatePages);
        _settings.InteractionSettingsApply = ApplyInteractionSettings;
        _settings.DesktopIconsRead = _interaction.ReadDesktopIcons;
        _settings.DesktopIconsSet = _interaction.SetDesktopIcons;
        _settings.DataRestored += OnSettingsDataRestored;
        _settings.UserDataDeletionRequested += OnSettingsUserDataDeletionRequested;
        _settings.UninstallRequested += OnSettingsUninstallRequested;

        var error = _interaction.Initialize();
        if (error is not null) ReportInteractionError(error);
        else _settings.ShowInteractionStatus("快捷操作服务已就绪。");
        ApplyDisplayRuntimeSettings(_data.Settings.Current.Display);

        SystemEvents.DisplaySettingsChanged += OnSystemDisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged += OnSystemUserPreferenceChanged;
        SystemEvents.PowerModeChanged += OnBackgroundPowerModeChanged;
        SystemEvents.SessionSwitch += OnBackgroundSessionSwitch;
    }

    private string? ApplyInteractionSettings(ShortcutSettings shortcuts, DisplaySettings display)
    {
        if (_interaction is null) return "运行服务未连接，未保存更改。";
        var error = _interaction.Apply(shortcuts, display);
        if (error is not null) return error;
        ApplyDisplayRuntimeSettings(_data!.Settings.Current.Display);
        _interaction.RefreshDisplays();
        return null;
    }

    private void ApplyDisplayRuntimeSettings(DisplaySettings display)
    {
        if (_dockSettings is not null && _data is not null)
            _dockSettings.Update(_data.Settings.Current.Dock);
        if (_dock is not null)
            _dock.SuppressPassiveTriggersWhenFullscreen = display.SuppressTriggersWhenFullscreen;
    }

    private bool CanExecuteInteraction(InputAction action) => _interaction?.CanExecute(action) == true
        && !_performancePointerInputSuppressed;

    private void ExecuteInteraction(InputAction action)
    {
        switch (action)
        {
            case InputAction.ToggleLauncher:
                if (_launcher?.IsVisible == true && !_launcher.IsClosingContainer) HideLauncher(); else ShowLauncher();
                break;
            case InputAction.HideLauncher:
                HideLauncher();
                break;
            case InputAction.NextPage:
                _launcher?.NavigatePage(1);
                break;
            case InputAction.PreviousPage:
                _launcher?.NavigatePage(-1);
                break;
            case InputAction.ToggleDesktopIcons:
                _interaction?.ToggleDesktopIcons();
                break;
            case InputAction.ToggleDockRevealLock:
                _dock?.ToggleRevealLock();
                break;
        }
    }

    private void OnLauncherNavigationAvailabilityChanged(object? sender, EventArgs e)
    {
        if (_launcher is not null)
            _interaction?.SetLauncherAvailability(_launcher.IsVisible, _launcher.CanNavigatePages);
    }

    private void OnSettingsDataRestored(object? sender, EventArgs e) => ExitApplication();

    private void ReportInteractionError(string message)
    {
        _settings?.ShowInteractionStatus(message);
        var path = _data is null ? null : Path.Combine(_data.Paths.LogDirectory, "interaction.log");
        ReportWriter.Write(message, path);
    }

    private void OnSystemDisplaySettingsChanged(object? sender, EventArgs e)
    { if (_performance?.State != PerformanceState.Sleeping) GlassDesk.UI.BackdropRecovery.RetryBackground("DisplaySettingsChanged"); QueueDisplayRefresh(); }
    private void OnBackgroundPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) SetPerformanceSessionSuspended(true);
        else if (e.Mode == PowerModes.Resume) SetPerformanceSessionSuspended(false);
    }
    private void OnBackgroundSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.RemoteDisconnect or SessionSwitchReason.ConsoleDisconnect)
            SetPerformanceSessionSuspended(true);
        else if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.RemoteConnect or SessionSwitchReason.ConsoleConnect)
            SetPerformanceSessionSuspended(false);
    }

    private void OnSystemUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        // Generation invalidation does not start capture. Sleeping feeds remain
        // stopped and reject their previous wallpaper on the next explicit wake.
        if (e.Category == UserPreferenceCategory.Desktop)
            GlassDesk.UI.BackdropRecovery.RetryBackground("DesktopSettingsChanged");
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle or UserPreferenceCategory.Window)
            QueueDisplayRefresh();
    }

    private void QueueDisplayRefresh()
    {
        if (_application.Dispatcher.HasShutdownStarted || _application.Dispatcher.HasShutdownFinished) return;
        if (Interlocked.Exchange(ref _displayRefreshPending, 1) != 0) return;
        _application.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            Interlocked.Exchange(ref _displayRefreshPending, 0);
            if (_disposed) return;
            var currentPrimary = DisplayService.Primary();
            var rebound = false;
            if (_primaryDisplaySession is { } primary && !primary.TryRefreshPrimary(currentPrimary))
            {
                _restoreDockAfterDisplayChange |= _dock?.IsVisible == true;
                _restoreLauncherAfterDisplayChange |= _launcher?.IsVisible == true;
                _restoreSettingsAfterDisplayChange |= _settings?.IsVisible == true;
                _dock?.StopForPrimaryDisplayChange();
                _launcher?.StopForPrimaryDisplayChange();
                foreach (Window window in _application.Windows)
                    if (window is GlassWindow glassWindow) glassWindow.Hide();
                if (_performance?.State != PerformanceState.Sleeping) GlassDesk.UI.BackdropRecovery.RetryBackground("PrimaryDisplayRebind");
                if (!primary.TryRebindPrimary(currentPrimary)) return;
                rebound = true;
            }
            _interaction?.RefreshDisplays();
            _performanceFullscreen?.Refresh();
            _dock?.RefreshDisplayPlacement();
            _launcher?.RefreshDisplayPlacement();
            if (_settings?.IsVisible == true) _settings.PositionOnPrimary();
            foreach (Window window in _application.Windows)
                if (window is GlassWindow glassWindow) glassWindow.ConstrainCurrentBoundsToPrimary();
            if (rebound)
            {
                _dock?.ResumeAfterPrimaryDisplayChange(_restoreDockAfterDisplayChange);
                if (_restoreLauncherAfterDisplayChange) ShowLauncher();
                if (_restoreSettingsAfterDisplayChange) ShowSettings();
                _restoreDockAfterDisplayChange = _restoreLauncherAfterDisplayChange = _restoreSettingsAfterDisplayChange = false;
            }
        }));
    }

    private void DisposeInteractionController()
    {
        SystemEvents.DisplaySettingsChanged -= OnSystemDisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged -= OnSystemUserPreferenceChanged;
        SystemEvents.PowerModeChanged -= OnBackgroundPowerModeChanged;
        SystemEvents.SessionSwitch -= OnBackgroundSessionSwitch;
        if (_launcher is not null)
            _launcher.NavigationAvailabilityChanged -= OnLauncherNavigationAvailabilityChanged;
        if (_settings is not null)
        {
            _settings.DataRestored -= OnSettingsDataRestored;
            _settings.UserDataDeletionRequested -= OnSettingsUserDataDeletionRequested;
            _settings.UninstallRequested -= OnSettingsUninstallRequested;
        }
        _interaction?.Dispose();
        _interaction = null;
    }
}
