using System.Windows.Threading;

namespace GlassDesk.App;

internal sealed partial class AppHost
{
    private DispatcherTimer? _trayRecoveryTimer;

    private void StartTrayRecovery()
    {
        StopTrayRecovery();
        _trayRecoveryTimer = new DispatcherTimer(DispatcherPriority.Background, _application.Dispatcher)
        { Interval = TimeSpan.FromSeconds(_tray?.IsVisible == true ? 30 : 2) };
        _trayRecoveryTimer.Tick += OnTrayRecoveryTick;
        _trayRecoveryTimer.Start();
    }

    private void OnTrayRecoveryTick(object? sender, EventArgs e) => RefreshTrayRegistration();

    private void RefreshTrayRegistration()
    {
        if (_shuttingDown || _disposed || _tray is null) return;
        var registered = _tray.EnsureVisible();
        if (_trayRecoveryTimer is not null)
            _trayRecoveryTimer.Interval = TimeSpan.FromSeconds(registered ? 30 : 2);
    }

    private void StopTrayRecovery()
    {
        if (_trayRecoveryTimer is null) return;
        _trayRecoveryTimer.Stop();
        _trayRecoveryTimer.Tick -= OnTrayRecoveryTick;
        _trayRecoveryTimer = null;
    }
}
