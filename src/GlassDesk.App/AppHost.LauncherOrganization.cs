using System.Windows.Threading;
using GlassDesk.AppServices;
using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;
using Microsoft.Win32;
using GlassDesk.Settings;

namespace GlassDesk.App;

internal sealed partial class AppHost
{
    private CancellationTokenSource _launcherOrganizationLifetime = new();
    private LauncherOrganizationPreviewWindow? _launcherOrganizationPreview;
    private DispatcherTimer? _launcherShortcutCleanupTimer;
    private bool _launcherOrganizationInitialized;
    private bool _launcherOrganizationDisposed;

    private void InitializeLauncherAutoOrganization()
    {
        if (_services is null || _launcherOrganizationInitialized) return;
        _launcherOrganizationInitialized = true;
        SystemEvents.PowerModeChanged += OnLauncherOrganizationPowerModeChanged;
        _services.DesktopDiscovery.Configure(action =>
            _application.Dispatcher.InvokeAsync(action, DispatcherPriority.Background).Task);
        _services.DesktopDiscovery.StatusChanged += (_, status) =>
        {
            if (_application.Dispatcher.HasShutdownStarted) return;
            _application.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_shuttingDown && !_launcherOrganizationLifetime.IsCancellationRequested)
                    _settings?.UpdateLauncherOrganizationStatus(status.Message);
            }));
        };
        _application.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(ReconcileLauncherAutoOrganization));
        _application.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
        {
            if (_shuttingDown || _disposed || _services is null || _launcherOrganizationLifetime.IsCancellationRequested
                || _loadReport?.LibraryStatus == GlassDesk.Core.Storage.LoadStatus.Unrecoverable) return;
            try { await _services.DesktopDiscovery.RepairShortcutIconsAsync(_launcherOrganizationLifetime.Token); }
            catch (OperationCanceledException) { }
            catch (Exception error) { _settings?.UpdateLauncherOrganizationStatus("游戏图标暂未修复：" + error.Message); }
        }));
    }

    private void ReconcileLauncherAutoOrganization()
    {
        if (_shuttingDown || _disposed || _launcherOrganizationLifetime.IsCancellationRequested || _services is null || _data is null) return;
        if (_loadReport?.LibraryStatus == GlassDesk.Core.Storage.LoadStatus.Unrecoverable) return;
        if (_data.Settings.Current.Launcher.AutoAddDesktopAppsEnabled) _services.DesktopDiscovery.Start();
        else _services.DesktopDiscovery.Stop();
        if (_data.Settings.Current.Launcher.AutoRemoveBrokenShortcutsEnabled)
        {
            if (_launcherShortcutCleanupTimer?.IsEnabled == true) return;
            _launcherShortcutCleanupTimer ??= new DispatcherTimer(LauncherShortcutCleanup.CheckInterval,
                DispatcherPriority.Background, OnLauncherShortcutCleanupTick, _application.Dispatcher);
            _launcherShortcutCleanupTimer.Start();
            OnLauncherShortcutCleanupTick(this, EventArgs.Empty);
        }
        else StopLauncherShortcutCleanup();
    }

    private async void OnLauncherShortcutCleanupTick(object? sender, EventArgs args)
    {
        if (_shuttingDown || _disposed || _services is null || _launcherOrganizationLifetime.IsCancellationRequested) return;
        try
        {
            var removed = await _services.ShortcutCleanup.SweepAsync(_launcherOrganizationLifetime.Token);
            if (removed > 0) _settings?.UpdateLauncherOrganizationStatus($"已从启动台移除 {removed} 个失效快捷方式。");
        }
        catch (OperationCanceledException) { }
        catch (StorageException error) { _settings?.UpdateLauncherOrganizationStatus("暂未清理失效快捷方式：" + error.Message); }
    }

    private void StopLauncherShortcutCleanup()
    {
        _launcherShortcutCleanupTimer?.Stop();
        _services?.ShortcutCleanup.Reset();
    }

    private async Task<DesktopAutoAddEnableResult> EnableDesktopAutoAddAsync()
    {
        if (_services is null || _data is null || _shuttingDown || _launcherOrganizationLifetime.IsCancellationRequested)
            return new(false, "程序正在停止，未开启桌面同步。");
        var token = _launcherOrganizationLifetime.Token;
        var discovery = _services.DesktopDiscovery;
        if (discovery.HasBaseline) return new(true);
        return await ShowDesktopAppsPreviewAsync(enabling: true);
    }

    private async Task<string> ScanDesktopAppsAsync()
    {
        var result = await ShowDesktopAppsPreviewAsync(enabling: false);
        return result.Message ?? "已取消，原有项目保留。";
    }

    private async Task<DesktopAutoAddEnableResult> ShowDesktopAppsPreviewAsync(bool enabling)
    {
        if (_services is null || _data is null || _shuttingDown || _launcherOrganizationLifetime.IsCancellationRequested)
            return new(false, "程序正在停止，未扫描桌面项目。");
        var token = _launcherOrganizationLifetime.Token;
        var discovery = _services.DesktopDiscovery;
        var preview = await discovery.PreviewExistingAsync(token);
        token.ThrowIfCancellationRequested();
        var dialog = new LauncherOrganizationPreviewWindow(enabling ? "同步已有桌面项目" : "扫描桌面项目",
            enabling ? "选择已有桌面项目，或跳过存量并开启同步。" : "选择要导入的桌面项目，可调整文件类型分类。",
            preview.Rows.Select(row => new LauncherOrganizationPreviewItem(row.SourcePath, row.DisplayName, row.CategoryName, row.Reason)
                { CategoryId = row.Classification.CategoryId, IconPath = row.IconPath }).ToArray(), enabling, desktopImportOnly: !enabling)
        { Owner = _settings, Icon = _settings?.Icon ?? CreateWindowIcon() };
        dialog.SetCategoryEditingEnabled(_data.Settings.Current.Launcher.GroupModeEnabled);
        try
        {
            _launcherOrganizationPreview = dialog;
            dialog.ShowDialog();
            if (dialog.Choice == LauncherOrganizationPreviewChoice.Cancel || token.IsCancellationRequested) return new(false);
            var selected = dialog.Choice == LauncherOrganizationPreviewChoice.SkipExisting ? [] : dialog.SelectedIds;
            var categories = dialog.SelectedCategories;
            preview = preview with { Rows = preview.Rows.Select(row =>
                categories.TryGetValue(row.SourcePath, out var category) && category != row.Classification.CategoryId
                    ? row with { Classification = row.Classification with { CategoryId = category, Reason = "手动选择分类" },
                        CategoryEdited = true, CategoryName = LauncherCategoryCatalog.Find(category)?.Name ?? "其他" }
                    : row).ToArray() };
            var result = await discovery.EnableWithBaselineAsync(preview, selected, token);
            token.ThrowIfCancellationRequested();
            return new(result.Failed == 0, result.Message);
        }
        finally { _launcherOrganizationPreview = null; dialog.Close(); }
    }

    private async Task<string> OrganizeExistingLauncherAppsAsync()
    {
        if (_services is null || _data is null || _shuttingDown || _launcherOrganizationLifetime.IsCancellationRequested)
            return "程序正在停止，未整理已有项目。";
        if (!_data.Settings.Current.Launcher.GroupModeEnabled) return "请先切换到分组模式。";
        var token = _launcherOrganizationLifetime.Token;
        var preview = await _services.AutoOrganization.PreviewAsync(token);
        token.ThrowIfCancellationRequested();
        var dialog = new LauncherOrganizationPreviewWindow("整理已有项目", "检查文件类型分类后应用；已有的手动分组会保留。",
            preview.Rows.Select(row => new LauncherOrganizationPreviewItem(row.AppId, row.DisplayName, row.CategoryName, row.Reason, row.IsManual)
                { CategoryId = row.Classification.CategoryId, IconPath = row.IconPath }).ToArray(), false)
        { Owner = _settings, Icon = _settings?.Icon ?? CreateWindowIcon() };
        try
        {
            _launcherOrganizationPreview = dialog;
            dialog.ShowDialog();
            if (dialog.Choice != LauncherOrganizationPreviewChoice.ApplySelected || token.IsCancellationRequested) return "已取消，原有分类保留。";
            token.ThrowIfCancellationRequested();
            var categories = dialog.SelectedCategories;
            preview = preview with { Rows = preview.Rows.Select(row =>
                categories.TryGetValue(row.AppId, out var category) && category != row.Classification.CategoryId
                    ? row with { Classification = row.Classification with { CategoryId = category, Reason = "手动选择分类" },
                        CategoryEdited = true, CategoryName = LauncherCategoryCatalog.Find(category)?.Name ?? "其他" }
                    : row).ToArray() };
            return _services.AutoOrganization.Apply(preview, dialog.SelectedIds).Message;
        }
        finally { _launcherOrganizationPreview = null; dialog.Close(); }
    }

    private async Task StopBackgroundServicesForRestoreAsync()
    {
        SuspendLibraryCommands();
        try
        {
            StopLauncherOrganization();
            if (_services is not null) await _services.DesktopDiscovery.StopAsync();
            await StopNearbyForRestoreAsync();
        }
        catch
        {
            await ResumeBackgroundServicesAfterFailedRestoreAsync();
            throw;
        }
    }

    private async Task ResumeBackgroundServicesAfterFailedRestoreAsync()
    {
        try { await ResumeNearbyAfterFailedRestoreAsync(); }
        finally
        {
            if (!_shuttingDown && !_disposed)
            {
                _launcherOrganizationLifetime.Dispose();
                _launcherOrganizationLifetime = new CancellationTokenSource();
                ReconcileLauncherAutoOrganization();
                ResumeLibraryCommands();
            }
        }
    }

    private void StopLauncherOrganization()
    {
        if (_launcherOrganizationDisposed) return;
        _launcherOrganizationLifetime.Cancel();
        _launcherOrganizationPreview?.Close();
        _launcherOrganizationPreview = null;
        StopLauncherShortcutCleanup();
        _services?.DesktopDiscovery.Stop();
    }

    private void DisposeLauncherAutoOrganization()
    {
        if (_launcherOrganizationDisposed) return;
        if (_launcherOrganizationInitialized)
        {
            SystemEvents.PowerModeChanged -= OnLauncherOrganizationPowerModeChanged;
            _launcherOrganizationInitialized = false;
        }
        StopLauncherOrganization();
        _launcherOrganizationLifetime.Dispose();
        _launcherOrganizationDisposed = true;
    }

    private void OnLauncherOrganizationPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume || _application.Dispatcher.HasShutdownStarted) return;
        _application.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
        {
            if (_shuttingDown || _disposed || _launcherOrganizationLifetime.IsCancellationRequested
                || _data?.Settings.Current.Launcher.AutoAddDesktopAppsEnabled != true || _services is null) return;
            try
            {
                await _services.DesktopDiscovery.StopAsync();
                ReconcileLauncherAutoOrganization();
                await _services.DesktopDiscovery.ScanNowAsync(_launcherOrganizationLifetime.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { _settings?.UpdateLauncherOrganizationStatus("恢复桌面扫描未能完成：" + error.Message); }
        }));
    }
}
