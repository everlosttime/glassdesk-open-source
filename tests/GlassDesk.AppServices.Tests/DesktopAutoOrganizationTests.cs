using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;
using GlassDesk.Platform.Shell;

namespace GlassDesk.AppServices.Tests;

public sealed class DesktopAutoOrganizationTests
{
    [Fact]
    public async Task FirstBaselineSkipsUnselectedAndOnlyImportsLaterSoftwareShortcuts()
    {
        using var temp = new TempWorkspace();
        var desktop = temp.CreateDirectory("桌面");
        var old = CreateShortcut(temp, "桌面/已有软件.lnk", "Apps/old.exe");
        var data = temp.CreateStore(); data.LoadAll();
        using var host = new AppServiceHost(data);
        var organization = new LauncherAutoOrganizationService(host);
        using var discovery = new DesktopAppDiscoveryService(host, organization, [desktop]);
        discovery.Configure(action => { action(); return Task.CompletedTask; });
        var preview = await discovery.PreviewExistingAsync();
        Assert.Equal(Path.GetFullPath(old), Assert.Single(preview.Rows).SourcePath);
        var baseline = await discovery.EnableWithBaselineAsync(preview, []);
        Assert.Equal(0, baseline.Added);
        Assert.True(discovery.HasBaseline);
        data.Settings.Current.Launcher.AutoAddDesktopAppsEnabled = true;
        CreateShortcut(temp, "桌面/新软件.lnk", "Apps/new.exe");
        temp.CreateFile("桌面/报告.txt");
        temp.CreateFile("桌面/setup.exe");
        await discovery.ScanNowAsync();
        Assert.Equal(3, host.Library.Items.Count);
        Assert.Contains(host.Library.Items, entry => entry.DisplayName == "新软件");
        var before = File.GetLastWriteTimeUtc(temp.DataPaths.AppLibraryFile);
        await discovery.ScanNowAsync();
        Assert.Equal(3, host.Library.Items.Count);
        Assert.Equal(before, File.GetLastWriteTimeUtc(temp.DataPaths.AppLibraryFile));
    }

    [Fact]
    public async Task RemovingFromLauncherDoesNotResurrectAfterShortcutRenameOrRestart()
    {
        using var temp = new TempWorkspace();
        var desktop = temp.CreateDirectory("桌面");
        var data = temp.CreateStore(); data.LoadAll();
        using var host = new AppServiceHost(data);
        var organization = new LauncherAutoOrganizationService(host);
        using (var discovery = new DesktopAppDiscoveryService(host, organization, [desktop]))
        {
            discovery.Configure(action => { action(); return Task.CompletedTask; });
            await discovery.EnableWithBaselineAsync(await discovery.PreviewExistingAsync(), []);
            data.Settings.Current.Launcher.AutoAddDesktopAppsEnabled = true;
            var shortcut = CreateShortcut(temp, "桌面/新软件.lnk", "Apps/tool.exe");
            await discovery.ScanNowAsync();
            var entry = Assert.Single(host.Library.Items);
            organization.SuppressAutoAdd(entry.Id);
            Assert.True(host.Layouts.CommitLauncher(LauncherLayoutEditor.RemoveShortcut(host.Layouts.Launcher, entry.Id, 35), host.Layouts.LauncherVersion));
            File.Move(shortcut, Path.Combine(desktop, "改名软件.lnk"));
        }
        using var reopened = new DesktopAppDiscoveryService(host, organization, [desktop]);
        reopened.Configure(action => { action(); return Task.CompletedTask; });
        await reopened.ScanNowAsync();
        Assert.Empty(LauncherLayoutRules.AppIds(host.Layouts.Launcher));
        Assert.Single(host.Library.Items);
    }

    [Fact]
    public async Task GameProtocolShortcutIsManagedAndSurvivesRemovingDesktopSource()
    {
        using var temp = new TempWorkspace();
        var desktop = temp.CreateDirectory("桌面");
        var game = temp.CreateFile("桌面/游戏.url", "[InternetShortcut]\r\nURL=steam://rungameid/123\r\n");
        temp.CreateFile("桌面/网站.url", "[InternetShortcut]\r\nURL=https://example.org/\r\n");
        var data = temp.CreateStore(); data.LoadAll();
        using var host = new AppServiceHost(data);
        var organization = new LauncherAutoOrganizationService(host);
        using var discovery = new DesktopAppDiscoveryService(host, organization, [desktop]);
        discovery.Configure(action => { action(); return Task.CompletedTask; });
        var preview = await discovery.PreviewExistingAsync();
        Assert.Equal("应用程序", preview.Rows.Single(row => row.SourcePath == Path.GetFullPath(game)).CategoryName);
        Assert.Equal(2, preview.Rows.Count);
        var result = await discovery.EnableWithBaselineAsync(preview, [game]);
        Assert.Equal(1, result.Added);
        var entry = Assert.Single(host.Library.Items);
        Assert.Equal(AppItemKind.Shortcut, entry.Kind);
        Assert.StartsWith(Path.Combine(temp.DataPaths.RootDirectory, "managed-shortcuts"), entry.Target, StringComparison.OrdinalIgnoreCase);
        File.Delete(game);
        Assert.True(File.Exists(entry.Target));
        Assert.Equal("[InternetShortcut]\r\nURL=steam://rungameid/123\r\n", File.ReadAllText(entry.Target));
        Assert.False(InternetShortcutReader.IsSupportedUrl("steam://rungameid/123"));
    }

    [Fact]
    public async Task ExistingLibraryEntryOutsideLauncherIsNotAutomaticallyRestored()
    {
        using var temp = new TempWorkspace();
        var desktop = temp.CreateDirectory("桌面");
        var data = temp.CreateStore(); data.LoadAll();
        using var host = new AppServiceHost(data);
        var organization = new LauncherAutoOrganizationService(host);
        using var discovery = new DesktopAppDiscoveryService(host, organization, [desktop]);
        discovery.Configure(action => { action(); return Task.CompletedTask; });
        await discovery.EnableWithBaselineAsync(await discovery.PreviewExistingAsync(), []);
        var path = CreateShortcut(temp, "桌面/手动移除.lnk", "Apps/tool.exe");
        var entry = Assert.Single(host.Import.ImportItems([new() { Source = path }]).Added);
        host.Layouts.CommitLauncher(LauncherLayoutEditor.RemoveShortcut(host.Layouts.Launcher, entry.Id, 35), host.Layouts.LauncherVersion);
        data.Settings.Current.Launcher.AutoAddDesktopAppsEnabled = true;
        await discovery.ScanNowAsync();
        Assert.Empty(LauncherLayoutRules.AppIds(host.Layouts.Launcher));
    }

    [Fact]
    public async Task BulkClassificationPreservesManualGroupsAndRejectsStalePreview()
    {
        using var temp = new TempWorkspace();
        var data = temp.CreateStore(); data.LoadAll();
        using var host = new AppServiceHost(data);
        var path = CreateShortcut(temp, "Code.lnk", "Apps/Code.exe");
        var entry = Assert.Single(host.Import.ImportItems([new() { Source = path }]).Added);
        host.Layouts.CommitLauncher(LauncherFilterRules.AddGroup(host.Layouts.Launcher, "mine", "我的工具", [entry.Id]), host.Layouts.LauncherVersion);
        var organization = new LauncherAutoOrganizationService(host);
        var preview = await organization.PreviewAsync();
        Assert.True(Assert.Single(preview.Rows).IsManual);
        var applied = organization.Apply(preview);
        Assert.Equal(0, applied.Applied);
        Assert.Equal("mine", Assert.Single(host.Layouts.Launcher.FilterGroups).Id);
        var next = await organization.PreviewAsync();
        host.Layouts.CommitLauncher(LauncherFilterRules.RenameGroup(host.Layouts.Launcher, "mine", "改名"), host.Layouts.LauncherVersion);
        Assert.True(organization.Apply(next).Failed > 0);
    }

    [Fact]
    public async Task ReadOnlyLayoutLeavesImportPendingAndCanRetryWithoutDuplicatingLibrary()
    {
        using var temp = new TempWorkspace();
        var desktop = temp.CreateDirectory("桌面");
        var data = temp.CreateStore(); data.LoadAll();
        using var host = new AppServiceHost(data);
        var organization = new LauncherAutoOrganizationService(host);
        using var discovery = new DesktopAppDiscoveryService(host, organization, [desktop]);
        discovery.Configure(action => { action(); return Task.CompletedTask; });
        await discovery.EnableWithBaselineAsync(await discovery.PreviewExistingAsync(), []);
        // Establish a real writable layout before temporarily denying writes.
        host.Layouts.SaveLauncher();
        data.Settings.Current.Launcher.AutoAddDesktopAppsEnabled = true;
        CreateShortcut(temp, "桌面/重试软件.lnk", "Apps/retry.exe");
        File.SetAttributes(temp.DataPaths.LauncherLayoutFile, FileAttributes.ReadOnly);
        try { await discovery.ScanNowAsync(); }
        finally { File.SetAttributes(temp.DataPaths.LauncherLayoutFile, FileAttributes.Normal); }
        await discovery.ScanNowAsync();
        Assert.Single(host.Library.Items);
        Assert.Single(LauncherLayoutRules.AppIds(host.Layouts.Launcher));
    }

    [Fact]
    public async Task InitialPreviewDoesNotSwallowNewFilesAndExplicitSelectionRestoresRemovedItem()
    {
        using var temp = new TempWorkspace();
        var desktop = temp.CreateDirectory("桌面");
        var old = CreateShortcut(temp, "桌面/旧软件.lnk", "Apps/old.exe");
        var data = temp.CreateStore(); data.LoadAll();
        using var host = new AppServiceHost(data);
        var organization = host.AutoOrganization;
        using var discovery = new DesktopAppDiscoveryService(host, organization, [desktop]);
        discovery.Configure(action => { action(); return Task.CompletedTask; });
        var removed = Assert.Single(host.Import.ImportItems([new() { Source = old }]).Added);
        organization.SuppressAutoAdd(removed.Id);
        host.Layouts.CommitLauncher(LauncherLayoutEditor.RemoveShortcut(host.Layouts.Launcher, removed.Id, 35), host.Layouts.LauncherVersion);
        var preview = await discovery.PreviewExistingAsync();
        CreateShortcut(temp, "桌面/预览中新出现.lnk", "Apps/new.exe");
        var result = await discovery.EnableWithBaselineAsync(preview, [old]);
        Assert.Equal(1, result.Added);
        Assert.Contains(removed.Id, LauncherLayoutRules.AppIds(host.Layouts.Launcher));
        data.Settings.Current.Launcher.AutoAddDesktopAppsEnabled = true;
        await discovery.ScanNowAsync();
        Assert.Equal(2, host.Library.Items.Count);
    }

    [Fact]
    public async Task GameProtocolAllowsOnlyLaunchActionsAndBulkPreviewRetainsGameIdentity()
    {
        using var temp = new TempWorkspace();
        var desktop = temp.CreateDirectory("桌面");
        var game = temp.CreateFile("桌面/小众游戏.url", "[InternetShortcut]\r\nURL=steam://rungameid/456\r\n");
        temp.CreateFile("桌面/商店.url", "[InternetShortcut]\r\nURL=steam://store/456\r\n");
        temp.CreateFile("桌面/安装.url", "[InternetShortcut]\r\nURL=steam://install/456\r\n");
        temp.CreateFile("桌面/Epic商店.url", "[InternetShortcut]\r\nURL=com.epicgames.launcher://store/product/example\r\n");
        var data = temp.CreateStore(); data.LoadAll();
        using var host = new AppServiceHost(data);
        using var discovery = new DesktopAppDiscoveryService(host, host.AutoOrganization, [desktop]);
        discovery.Configure(action => { action(); return Task.CompletedTask; });
        var preview = await discovery.PreviewExistingAsync();
        Assert.Equal(4, preview.Rows.Count);
        Assert.Equal(1, (await discovery.EnableWithBaselineAsync(preview, [game])).Added);
        Assert.Equal("应用程序", Assert.Single((await host.AutoOrganization.PreviewAsync()).Rows).CategoryName);
    }

    [Fact]
    public async Task ExplicitPreviewCategoryIsLockedAndManualReimportClearsSuppression()
    {
        using var temp = new TempWorkspace();
        var desktop = temp.CreateDirectory("桌面");
        var path = CreateShortcut(temp, "桌面/Code.lnk", "Apps/Code.exe");
        var data = temp.CreateStore(); data.LoadAll();
        using var host = new AppServiceHost(data);
        using var discovery = new DesktopAppDiscoveryService(host, host.AutoOrganization, [desktop]);
        discovery.Configure(action => { action(); return Task.CompletedTask; });
        var preview = await discovery.PreviewExistingAsync();
        preview = preview with { Rows = preview.Rows.Select(row => row with
        { Classification = new("system", "手动选择分类"), CategoryEdited = true }).ToArray() };
        Assert.Equal(1, (await discovery.EnableWithBaselineAsync(preview, [path])).Added);
        var entry = Assert.Single(host.Library.Items);
        var manual = Assert.Single(host.Layouts.Launcher.AutoClassifications);
        Assert.True(manual.IsManualOverride);
        Assert.Equal("system", manual.CategoryId);
        host.AutoOrganization.SuppressAutoAdd(entry.Id);
        host.Layouts.CommitLauncher(LauncherLayoutEditor.RemoveShortcut(host.Layouts.Launcher, entry.Id, 35), host.Layouts.LauncherVersion);
        host.Import.ImportItems([new() { Source = path }]);
        Assert.Contains(entry.Id, LauncherLayoutRules.AppIds(host.Layouts.Launcher));
        Assert.DoesNotContain(LauncherAutoOrganizationService.GetLogicalKey(entry), host.Layouts.Launcher.SuppressedAutoAddKeys);
    }

    [Fact]
    public async Task UnselectedBaselineRemainsSkippedAcrossRenameAndIconRewrite()
    {
        using var temp = new TempWorkspace();
        var desktop = temp.CreateDirectory("桌面");
        var old = CreateShortcut(temp, "桌面/旧软件.lnk", "Apps/old.exe");
        var data = temp.CreateStore(); data.LoadAll();
        using var host = new AppServiceHost(data);
        using var discovery = new DesktopAppDiscoveryService(host, host.AutoOrganization, [desktop]);
        discovery.Configure(action => { action(); return Task.CompletedTask; });
        await discovery.EnableWithBaselineAsync(await discovery.PreviewExistingAsync(), []);
        var renamed = Path.Combine(desktop, "重命名.lnk");
        File.Move(old, renamed);
        Assert.True(ShortcutWriter.Write(renamed, new ShortcutDefinition(Path.Combine(temp.Root, "Apps/old.exe"), Description: "图标变更")));
        data.Settings.Current.Launcher.AutoAddDesktopAppsEnabled = true;
        await discovery.ScanNowAsync();
        Assert.Empty(host.Library.Items);
        var preview = await discovery.PreviewExistingAsync();
        Assert.Equal(1, (await discovery.EnableWithBaselineAsync(preview, [renamed])).Added);
        Assert.Single(host.Library.Items);
    }

    [Fact]
    public async Task StoppingBeforeQueuedHostCommitPreventsImport()
    {
        using var temp = new TempWorkspace();
        var desktop = temp.CreateDirectory("桌面");
        var data = temp.CreateStore(); data.LoadAll();
        using var host = new AppServiceHost(data);
        using var discovery = new DesktopAppDiscoveryService(host, host.AutoOrganization, [desktop]);
        discovery.Configure(action => { action(); return Task.CompletedTask; });
        await discovery.EnableWithBaselineAsync(await discovery.PreviewExistingAsync(), []);
        data.Settings.Current.Launcher.AutoAddDesktopAppsEnabled = true;
        CreateShortcut(temp, "桌面/新软件.lnk", "Apps/new.exe");
        var queued = new TaskCompletionSource<Action>(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        discovery.Configure(action =>
        {
            if (Interlocked.Increment(ref calls) == 1) { action(); return Task.CompletedTask; }
            queued.TrySetResult(action);
            return delivered.Task;
        });
        var scan = discovery.ScanNowAsync();
        var commit = await queued.Task.WaitAsync(TimeSpan.FromSeconds(10));
        discovery.Stop();
        commit(); delivered.SetResult();
        await scan;
        Assert.Empty(host.Library.Items);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1,\"baselineEstablished\":true,\"sources\":[null]}")]
    [InlineData("{\"schemaVersion\":1,\"baselineEstablished\":true,\"sources\":[{\"sourcePath\":\"x\",\"logicalKey\":null}]}")]
    public void CorruptSyncStateCannotRestartAutomaticDiscovery(string json)
    {
        using var temp = new TempWorkspace();
        var data = temp.CreateStore(); data.LoadAll();
        File.WriteAllText(Path.Combine(temp.DataPaths.RootDirectory, "desktop-sync.json"), json);
        using var host = new AppServiceHost(data);
        data.Settings.Current.Launcher.AutoAddDesktopAppsEnabled = true;
        host.DesktopDiscovery.Configure(action => { action(); return Task.CompletedTask; });
        host.DesktopDiscovery.Start();
        Assert.False(host.DesktopDiscovery.IsRunning);
        Assert.Empty(host.Library.Items);
        Assert.Equal(json, File.ReadAllText(Path.Combine(temp.DataPaths.RootDirectory, "desktop-sync.json")));
    }

    [Fact]
    public async Task RealWatcherImportsNewShortcutWithoutManualScan()
    {
        using var temp = new TempWorkspace();
        var desktop = temp.CreateDirectory("桌面");
        var data = temp.CreateStore(); data.LoadAll();
        using var host = new AppServiceHost(data);
        using var discovery = new DesktopAppDiscoveryService(host, host.AutoOrganization, [desktop]);
        discovery.Configure(action => { action(); return Task.CompletedTask; });
        await discovery.EnableWithBaselineAsync(await discovery.PreviewExistingAsync(), []);
        data.Settings.Current.Launcher.AutoAddDesktopAppsEnabled = true;
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        discovery.StatusChanged += (_, status) => { if (status.Added > 0) changed.TrySetResult(); };
        discovery.Start();
        CreateShortcut(temp, "桌面/自动发现.lnk", "Apps/watch.exe");
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await discovery.StopAsync();
        Assert.Equal("自动发现", Assert.Single(host.Library.Items).DisplayName);
    }

    [Fact]
    public async Task HundredSourcesRemainUniqueAndIdleScansDoNotRewriteDocuments()
    {
        using var temp = new TempWorkspace();
        var desktop = temp.CreateDirectory("桌面");
        var data = temp.CreateStore(); data.LoadAll();
        using var host = new AppServiceHost(data);
        using var discovery = new DesktopAppDiscoveryService(host, host.AutoOrganization, [desktop]);
        discovery.Configure(action => { action(); return Task.CompletedTask; });
        await discovery.EnableWithBaselineAsync(await discovery.PreviewExistingAsync(), []);
        for (var i = 0; i < 100; i++) CreateShortcut(temp, $"桌面/软件{i}.lnk", $"Apps/app{i}.exe");
        data.Settings.Current.Launcher.AutoAddDesktopAppsEnabled = true;
        await discovery.ScanNowAsync();
        Assert.Equal(100, host.Library.Items.Count);
        var paths = new[] { temp.DataPaths.AppLibraryFile, temp.DataPaths.LauncherLayoutFile, temp.DataPaths.DesktopSyncFile };
        var timestamps = paths.Select(File.GetLastWriteTimeUtc).ToArray();
        await discovery.ScanNowAsync();
        await discovery.ScanNowAsync();
        Assert.Equal(100, host.Library.Items.Count);
        Assert.Equal(timestamps, paths.Select(File.GetLastWriteTimeUtc).ToArray());
    }

    private static string CreateShortcut(TempWorkspace temp, string shortcutName, string targetName)
    {
        var target = temp.CreateFile(targetName, "not executed");
        var path = Path.Combine(temp.Root, shortcutName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Assert.True(ShortcutWriter.Write(path, new ShortcutDefinition(target)));
        return path;
    }
}
