using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;

namespace GlassDesk.AppServices.Tests;

public sealed class DesktopAllItemsTests
{
    [Fact]
    public async Task UpgradingSoftwareOnlyBaselineDoesNotSilentlyImportExistingDocuments()
    {
        using var temp = new TempWorkspace(); var desktop = temp.CreateDirectory("桌面");
        var data = temp.CreateStore(); data.LoadAll();
        File.WriteAllText(data.Paths.DesktopSyncFile, "{\"schemaVersion\":1,\"baselineEstablished\":true,\"sources\":[]}");
        temp.CreateDirectory("桌面/之前的文件夹"); temp.CreateFile("桌面/之前的文档.txt");
        using var host = new AppServiceHost(data);
        using var discovery = new DesktopAppDiscoveryService(host, host.AutoOrganization, [desktop]);
        discovery.Configure(action => { action(); return Task.CompletedTask; });
        data.Settings.Current.Launcher.AutoAddDesktopAppsEnabled = true;
        await discovery.ScanNowAsync();
        Assert.Empty(host.Library.Items);
        var added = Path.GetFullPath(temp.CreateFile("桌面/新文档.txt"));
        await discovery.ScanNowAsync();
        Assert.Equal(added, Assert.Single(host.Library.Items).Target);
        var preview = await discovery.PreviewExistingAsync();
        Assert.Equal(3, preview.Rows.Count);
    }

    [Fact]
    public async Task ImportsEveryVisibleTopLevelItemWithoutReadingOrCopyingLargeDocuments()
    {
        using var temp = new TempWorkspace(); var desktop = temp.CreateDirectory("桌面");
        var data = temp.CreateStore(); data.LoadAll(); using var host = new AppServiceHost(data);
        using var discovery = new DesktopAppDiscoveryService(host, host.AutoOrganization, [desktop]);
        discovery.Configure(action => { action(); return Task.CompletedTask; });
        await discovery.EnableWithBaselineAsync(await discovery.PreviewExistingAsync(), []);
        data.Settings.Current.Launcher.AutoAddDesktopAppsEnabled = true;
        var folder = temp.CreateDirectory("桌面/资料.lnk");
        temp.CreateFile("桌面/资料.lnk/不展开.txt");
        var text = temp.CreateFile("桌面/报告.txt", "text");
        temp.CreateFile("桌面/图片.png", "image"); temp.CreateFile("桌面/setup.exe");
        temp.CreateFile("桌面/网页.url", "[InternetShortcut]\nURL=https://example.org/\n");
        var large = temp.CreateFile("桌面/大型数据.bin");
        using (var stream = File.OpenWrite(large)) stream.SetLength(128 * 1024 * 1024);
        await discovery.ScanNowAsync();
        Assert.Equal(6, host.Library.Items.Count);
        Assert.Contains(host.Library.Items, entry => entry.Target == Path.GetFullPath(folder) && entry.Kind == AppItemKind.Folder);
        Assert.Contains(host.Library.Items, entry => entry.Target == Path.GetFullPath(text) && entry.Kind == AppItemKind.File);
        Assert.Contains(host.Library.Items, entry => entry.Target == Path.GetFullPath(large));
        Assert.Contains(host.Library.Items, entry => entry.Target == "https://example.org/");
        Assert.Contains(host.Layouts.Launcher.FilterGroups, group => group.CategoryId == "folders");
        Assert.Contains(host.Layouts.Launcher.FilterGroups, group => group.CategoryId == "text");
        Assert.Contains(host.Layouts.Launcher.FilterGroups, group => group.CategoryId == "images");
        Assert.Empty(Directory.Exists(host.Paths.ManagedShortcutsDirectory) ? Directory.GetFiles(host.Paths.ManagedShortcutsDirectory, "*.bin") : []);
        var entry = host.Library.Items.Single(entry => entry.Target == Path.GetFullPath(text));
        var workspace = new LauncherWorkspace(host); var edit = workspace.BeginEdit();
        edit.Draft = LauncherLayoutEditor.RemoveShortcut(edit.Draft, entry.Id, 35); Assert.True(workspace.Commit(edit));
        File.AppendAllText(text, "more"); await discovery.ScanNowAsync();
        Assert.DoesNotContain(entry.Id, LauncherLayoutRules.AppIds(host.Layouts.Launcher));
        Assert.Equal(6, host.Library.Items.Count);
    }

    [Fact]
    public async Task RealWatcherAddsFolderThenRenamesItsExistingEntry()
    {
        using var temp = new TempWorkspace(); var desktop = temp.CreateDirectory("桌面");
        var data = temp.CreateStore(); data.LoadAll(); using var host = new AppServiceHost(data);
        using var discovery = new DesktopAppDiscoveryService(host, host.AutoOrganization, [desktop]);
        discovery.Configure(action => { action(); return Task.CompletedTask; });
        await discovery.EnableWithBaselineAsync(await discovery.PreviewExistingAsync(), []);
        data.Settings.Current.Launcher.AutoAddDesktopAppsEnabled = true;
        var added = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        discovery.StatusChanged += (_, status) => { if (status.Added > 0) added.TrySetResult(); };
        discovery.Start();
        var initial = Directory.CreateDirectory(Path.Combine(desktop, "新建文件夹")).FullName;
        await added.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var renamed = Path.Combine(desktop, "我的资料"); Directory.Move(initial, renamed);
        await Task.Delay(1500); await discovery.StopAsync();
        Assert.Equal(renamed, Assert.Single(host.Library.Items).Target);
        Assert.Equal("我的资料", host.Library.Items[0].DisplayName);
    }
}
