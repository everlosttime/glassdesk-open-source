using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using GlassDesk.Dock;
using GlassDesk.Platform;
using GlassDesk.Platform.Displays;

namespace GlassDesk.App;

internal sealed partial class AppHost
{
    private bool _recreatingDesktopBars, _desktopRecoveryQueued, _desktopBarsNeedRebuild, _desktopBarsPinned;
    private DispatcherTimer? _desktopRecoveryTimer;

    private void CreateDesktopBars()
    {
        _desktopBarsPinned = _data!.Settings.Current.Appearance.PinBarsToDesktop;
        var primary = _primaryDisplaySession
            ?? throw new InvalidOperationException("Desktop bars require a primary display session.");
        _dock = new DockWindow();
        _statusBar = new StatusBarWindow();
        _dock.BindPrimaryDisplay(primary);
        _statusBar.BindPrimaryDisplay(primary);
        _statusBar.AttachSettings(_dockSettings!);
        _statusBar.IsLauncherActive = () => _launcher?.IsVisible == true;
        _statusBar.ConfigureGameSleep(() => RuntimeManualGameSleep, SetManualGameSleep);
        _statusBar.ConfigureSettings(ShowSettings);
        _statusBar.ConfigureCalendar(_data.Settings);
        _statusBar.ConfigurePlacement(() =>
            (_data!.Settings.Current.Dock.StatusBarOffsetX, _data.Settings.Current.Dock.StatusBarEdge));
        _statusBar.ConfigureSizeScale(() => _data!.Settings.Current.Dock.StatusBarSizeScale);
        _statusBar.ConfigureNearby(() => ShowNearbyPanel(null, toggle: true),
            () => _services?.DockIcons.GetIconPath("entry:nearby"));
        _dock.AttachServices(_services!, _dockSettings!, (PixelRect? source) =>
            ToggleLauncherFromDock(_launcher!, source, ShowLauncher));
        _dock.SuppressPassiveTriggersWhenFullscreen = _data.Settings.Current.Display.SuppressTriggersWhenFullscreen;
        _dock.SetLauncherContainerState(_launcher?.IsVisible == true);
        _dock.ToggleDesktopIcons = () => ExecuteInteraction(GlassDesk.Core.Input.InputAction.ToggleDesktopIcons);
        var icon = _launcher?.Icon ?? CreateWindowIcon();
        foreach (var bar in new GlassDesk.UI.Controls.GlassWindow[] { _dock, _statusBar })
        {
            bar.Icon = icon;
            bar.Closing += OnWindowClosing;
            bar.Closed += OnDesktopBarClosed;
            // A desktop-owned HWND stays registered even while its rendering is concealed.
            // Reveals never need to show a new top-level window during Show Desktop.
            bar.PrimeSurface();
        }
        if (_performance is not null)
        {
            _dock.PerformanceSurfaceVisibilityChanged += OnPerformanceVisibilityChanged;
            _statusBar.PerformanceSurfaceVisibilityChanged += OnPerformanceVisibilityChanged;
            _dock.IsVisibleChanged += OnPerformanceWindowVisibilityChanged;
        }
        if (_performance?.IsSuppressionRequested == true)
        {
            _dock.SetPerformanceSleeping(true);
            _statusBar.SetPerformanceSleeping(true);
        }
        else _dock.ShowSurface();
        _statusBar.Start();
    }

    private void OnDesktopBarClosed(object? sender, EventArgs e) => QueueDesktopBarsRecovery();

    private void QueueDesktopBarsRecovery()
    {
        if (_disposed || _shuttingDown || _recreatingDesktopBars || _desktopRecoveryQueued
            || _application.Dispatcher.HasShutdownStarted) return;
        _desktopRecoveryQueued = true;
        _application.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _desktopRecoveryQueued = false;
            RecoverDesktopBars();
        }));
    }

    private void RecoverDesktopBars()
    {
        if (_disposed || _shuttingDown || _recreatingDesktopBars || _data is null || _services is null) return;
        if (_data.Settings.Current.Appearance.PinBarsToDesktop && DesktopWindowAttachment.FindDesktopHost() == 0)
        {
            _desktopRecoveryTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
                (_, _) => QueueDesktopBarsRecovery(), _application.Dispatcher);
            _desktopRecoveryTimer.Start();
            return;
        }
        _desktopRecoveryTimer?.Stop();
        if (!_desktopBarsNeedRebuild && _dock is not null && _statusBar is not null
            && WindowControl.IsAlive(new WindowInteropHelper(_dock).Handle)
            && WindowControl.IsAlive(new WindowInteropHelper(_statusBar).Handle))
        {
            _dock.RefreshDesktopAttachment();
            _statusBar.RefreshDesktopAttachment();
            return;
        }
        _recreatingDesktopBars = true;
        try
        {
            if (_dock is not null)
            {
                _dock.PerformanceSurfaceVisibilityChanged -= OnPerformanceVisibilityChanged;
                _dock.IsVisibleChanged -= OnPerformanceWindowVisibilityChanged;
            }
            if (_statusBar is not null)
                _statusBar.PerformanceSurfaceVisibilityChanged -= OnPerformanceVisibilityChanged;
            foreach (var bar in new Window?[] { _dock, _statusBar })
            {
                if (bar is null) continue;
                bar.Closing -= OnWindowClosing;
                bar.Closed -= OnDesktopBarClosed;
                bar.Close();
            }
            CreateDesktopBars();
            _desktopBarsNeedRebuild = false;
            GlassDesk.UI.BackdropRecovery.RetryBackground("DesktopBarsRecreated");
        }
        finally { _recreatingDesktopBars = false; }
        ReconcilePerformance();
    }
}
