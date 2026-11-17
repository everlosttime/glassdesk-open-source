using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;
using GlassDesk.Platform.Shell;

namespace GlassDesk.AppServices.Tests;

public sealed class DesktopDeletionSyncTests
{
    [Fact]
    public async Task RealWatcherRemovesDeletedFolderWithoutManualScan()
    {
        using var temp = new TempWorkspace(); var desktop = temp.CreateDirectory("桌面");
        var data = temp.CreateStore(); data.LoadAll(); using var host = new AppServiceHost(data);
        using var sync = new DesktopAppDiscoveryService(host, host.AutoOrganization, [desktop]);
        sync.Configure(action => { action(); return Task.CompletedTask; });
        await sync.EnableWithBaselineAsync(await sync.PreviewExistingAsync(), []);
        data.Settings.Current.Launcher.AutoAddDesktopAppsEnabled = true;
        var added = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var removed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sync.StatusChanged += (_, status) =>
        {
            if (status.Added > 0) added.TrySetResult();
            if (status.Message.Contains("移除 1 项", StringComparison.Ordinal)) removed.TrySetResult();
        };
        sync.Start();
        var folder = Directory.CreateDirectory(Path.Combine(desktop, "同步文件夹")).FullName;
        await added.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Directory.Delete(folder);
        await removed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await sync.StopAsync();
        Assert.Empty(LauncherLayoutRules.AppIds(host.Layouts.Launcher));
    }

    [Fact]
    public async Task PreviewSelectedDesktopItemsAreManagedButExplicitManualReimportsAreProtected()
    {
        using var temp = new TempWorkspace(); var desktop = temp.CreateDirectory("桌面");
        var data = temp.CreateStore(); data.LoadAll(); using var host = new AppServiceHost(data);
        using var sync = new DesktopAppDiscoveryService(host, host.AutoOrganization, [desktop]);
        sync.Configure(action => { action(); return Task.CompletedTask; });
        var first = Path.GetFullPath(temp.CreateFile("桌面/同步.txt"));
        var manual = Path.GetFullPath(temp.CreateFile("桌面/保留.txt"));
        await sync.EnableWithBaselineAsync(await sync.PreviewExistingAsync(), [first, manual]);
        host.Import.ImportItems([new() { Source = manual }]);
        data.Settings.Current.Launcher.AutoAddDesktopAppsEnabled = true;
        File.Delete(first); File.Delete(manual);
        await sync.ScanNowAsync(); await Task.Delay(850); await sync.ScanNowAsync();
        var remaining = Assert.Single(LauncherLayoutRules.AppIds(host.Layouts.Launcher));
        Assert.Equal(manual, host.Library.Find(remaining)!.Target);
    }

    [Fact]
    public async Task DeletedDesktopItemLeavesLauncherAndRecreationRestoresTheSameEntry()
    {
        using var temp = new TempWorkspace(); var desktop = temp.CreateDirectory("桌面");
        var data = temp.CreateStore(); data.LoadAll(); using var host = new AppServiceHost(data);
        using var sync = new DesktopAppDiscoveryService(host, host.AutoOrganization, [desktop]);
        sync.Configure(action => { action(); return Task.CompletedTask; });
        await sync.EnableWithBaselineAsync(await sync.PreviewExistingAsync(), []);
        data.Settings.Current.Launcher.AutoAddDesktopAppsEnabled = true;
        var path = Path.GetFullPath(temp.CreateFile("桌面/报告.txt")); await sync.ScanNowAsync();
        var entry = Assert.Single(host.Library.Items);
        data.Layouts.Dock.PinnedItemIds.Add(entry.Id); data.Layouts.SaveDock();
        File.Delete(path); await sync.ScanNowAsync(); await Task.Delay(850); await sync.ScanNowAsync();
        Assert.Empty(LauncherLayoutRules.AppIds(host.Layouts.Launcher));
        Assert.Contains(entry.Id, host.Layouts.Dock.PinnedItemIds);
        Assert.Single(host.Library.Items);
        entry.IsTargetMissing = true; host.Library.Save();
        File.WriteAllText(path, "recreated"); await sync.ScanNowAsync();
        Assert.Equal(entry.Id, Assert.Single(LauncherLayoutRules.AppIds(host.Layouts.Launcher)));
        Assert.Single(host.Library.Items);
        Assert.False(host.Library.Find(entry.Id)!.IsTargetMissing);
    }

    [Fact]
    public async Task ManualEntriesAndAnUnavailableDesktopAreNeverPruned()
    {
        using var temp = new TempWorkspace(); var desktop = temp.CreateDirectory("桌面");
        var data = temp.CreateStore(); data.LoadAll(); using var host = new AppServiceHost(data);
        using var sync = new DesktopAppDiscoveryService(host, host.AutoOrganization, [desktop]);
        sync.Configure(action => { action(); return Task.CompletedTask; });
        await sync.EnableWithBaselineAsync(await sync.PreviewExistingAsync(), []);
        data.Settings.Current.Launcher.AutoAddDesktopAppsEnabled = true;
        var path = Path.GetFullPath(temp.CreateFile("桌面/手动.txt"));
        host.Import.ImportItems([new() { Source = path }]); await sync.ScanNowAsync();
        File.Delete(path); await sync.ScanNowAsync(); await Task.Delay(850); await sync.ScanNowAsync();
        Assert.Single(LauncherLayoutRules.AppIds(host.Layouts.Launcher));
        temp.CreateFile("桌面/自动.txt"); await sync.ScanNowAsync();
        Directory.Move(desktop, desktop + "-offline");
        await sync.ScanNowAsync(); await Task.Delay(850); await sync.ScanNowAsync();
        Assert.Equal(2, LauncherLayoutRules.AppIds(host.Layouts.Launcher).Count);
    }

    [Fact]
    public async Task DuplicateShortcutsKeepEntryUntilTheLastDesktopSourceDisappears()
    {
        using var temp = new TempWorkspace(); var desktop = temp.CreateDirectory("桌面");
        var data = temp.CreateStore(); data.LoadAll(); using var host = new AppServiceHost(data);
        using var sync = new DesktopAppDiscoveryService(host, host.AutoOrganization, [desktop]);
        sync.Configure(action => { action(); return Task.CompletedTask; });
        await sync.EnableWithBaselineAsync(await sync.PreviewExistingAsync(), []);
        data.Settings.Current.Launcher.AutoAddDesktopAppsEnabled = true;
        var exe = temp.CreateFile("Apps/test.exe"); var first = Path.Combine(desktop, "一.lnk"); var second = Path.Combine(desktop, "二.lnk");
        Assert.True(ShortcutWriter.Write(first, new ShortcutDefinition(exe)));
        Assert.True(ShortcutWriter.Write(second, new ShortcutDefinition(exe)));
        await sync.ScanNowAsync(); Assert.Single(host.Library.Items);
        File.Delete(first); await sync.ScanNowAsync(); await Task.Delay(850); await sync.ScanNowAsync();
        Assert.Single(LauncherLayoutRules.AppIds(host.Layouts.Launcher));
        File.Delete(second); await sync.ScanNowAsync(); await Task.Delay(850); await sync.ScanNowAsync();
        Assert.Empty(LauncherLayoutRules.AppIds(host.Layouts.Launcher));
    }
}
