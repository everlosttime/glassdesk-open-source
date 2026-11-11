// Public 1.6.0 adaptation: information-bar-specific implementation is excluded.
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
        _dock.BindPrimaryDisplay(primary);
        _dock.AttachServices(_services!, _dockSettings!, (PixelRect? source) =>
            ToggleLauncherFromDock(_launcher!, source, ShowLauncher));
        _dock.SuppressPassiveTriggersWhenFullscreen = _data.Settings.Current.Display.SuppressTriggersWhenFullscreen;
        _dock.SetLauncherContainerState(_launcher?.IsVisible == true);
        _dock.ToggleDesktopIcons = () => ExecuteInteraction(GlassDesk.Core.Input.InputAction.ToggleDesktopIcons);
        var icon = _launcher?.Icon ?? CreateWindowIcon();
        foreach (var bar in new GlassDesk.UI.Controls.GlassWindow[] { _dock })
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
            _dock.IsVisibleChanged += OnPerformanceWindowVisibilityChanged;
        }
        if (_performance?.IsSuppressionRequested == true)
        {
            _dock.SetPerformanceSleeping(true);
        }
        else _dock.ShowSurface();
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
        if (!_desktopBarsNeedRebuild && _dock is not null
            && WindowControl.IsAlive(new WindowInteropHelper(_dock).Handle))
        {
            _dock.RefreshDesktopAttachment();
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
            foreach (var bar in new Window?[] { _dock })
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
