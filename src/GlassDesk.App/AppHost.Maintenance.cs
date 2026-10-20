using System.IO;
using System.Diagnostics;
using System.Windows;
using GlassDesk.AppServices;
using GlassDesk.Platform.Installation;

namespace GlassDesk.App;

internal sealed partial class AppHost
{
    private int _maintenanceStarted;

    private void OnSettingsUserDataDeletionRequested(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _maintenanceStarted, 1) != 0)
        {
            return;
        }

        var installDirectory = Path.GetDirectoryName(Environment.ProcessPath ?? string.Empty);
        UserDataMaintenanceSequence.Execute(
            closeCommandDispatch: () =>
            {
                _deferredCommandGate.Close();
                _shuttingDown = true;
            },
            prepareSettings: () => _settings?.PrepareForUserDataMaintenance(),
            stopInteractions: DisposeInteractionController,
            stopTray: () =>
            {
                _tray?.Dispose();
                _tray = null;
            },
            stopServices: () =>
            {
                _services?.Dispose();
                _services = null;
            },
            closeMessageWindow: () =>
            {
                _messageWindow?.Dispose();
                _messageWindow = null;
            },
            releaseDataLease: () =>
            {
                _userDataLease?.Dispose();
                _userDataLease = null;
            },
            deleteData: () => UserDataDeletionService.ForCurrentUser(installDirectory).DeleteAll(confirmed: true),
            reportStopFailure: ex => MessageBox.Show(
                $"未能安全停止 GlassDesk 的数据写入，因此没有删除数据。请重新启动后重试。\n\n{ex.Message}",
                "GlassDesk 数据未删除",
                MessageBoxButton.OK,
                MessageBoxImage.Error),
            presentResult: result =>
            {
                var (message, icon) = FormatDeletionResult(result);
                MessageBox.Show(message, "GlassDesk 数据维护", MessageBoxButton.OK, icon);
            },
            shutdown: _application.Shutdown);
    }

    private void OnSettingsUninstallRequested(object? sender, EventArgs e)
    {
        if (_shuttingDown || Volatile.Read(ref _maintenanceStarted) != 0) return;

        var info = new InstallationLocator().Inspect(Environment.ProcessPath ?? string.Empty);
        if (info.State != InstallationState.VerifiedInstalled || string.IsNullOrWhiteSpace(info.UninstallerPath))
        {
            _settings?.AttachInstallation(info);
            return;
        }

        try
        {
            // 仅启动已核验的可执行文件路径；不传递或执行注册表中的 UninstallString 参数。
            using var process = Process.Start(new ProcessStartInfo(info.UninstallerPath) { UseShellExecute = true });
            if (process is null)
            {
                _settings?.ShowUninstallLaunchError("系统未能创建卸载程序进程。");
                return;
            }

            _deferredCommandGate.Close();
            _shuttingDown = true;
            _application.Shutdown();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _settings?.ShowUninstallLaunchError(ex.Message);
        }
    }

    private static (string Message, MessageBoxImage Icon) FormatDeletionResult(UserDataDeletionResult result) => result.Status switch
    {
        UserDataDeletionStatus.Deleted =>
            ("GlassDesk 用户数据已删除。程序即将退出；下次启动时会创建一份新的默认数据。", MessageBoxImage.Information),
        UserDataDeletionStatus.NotFound =>
            ("没有找到 GlassDesk 用户数据目录。程序即将退出。", MessageBoxImage.Information),
        UserDataDeletionStatus.Busy =>
            (result.Error ?? "数据仍被其它 GlassDesk 实例使用；本次没有删除任何数据。请退出其它实例后重试。", MessageBoxImage.Warning),
        UserDataDeletionStatus.UnsafePath =>
            ("数据目录路径校验未通过，本次没有删除数据。\n\n" + result.Error, MessageBoxImage.Error),
        UserDataDeletionStatus.Residual =>
            ($"数据删除未能完整完成。已删除 {result.DeletedEntries} 个条目；剩余内容已隔离在以下目录，请检查并手动处理：\n\n{result.ResidualDirectory}\n\nGlassDesk 不会在下次启动时自动继续删除。\n\n{result.Error}", MessageBoxImage.Warning),
        _ =>
            ("数据目录改名隔离失败；原数据保留，没有继续删除。\n\n" + result.Error, MessageBoxImage.Error),
    };
}
