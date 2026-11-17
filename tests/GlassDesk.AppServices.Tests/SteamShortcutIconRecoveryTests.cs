using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;

namespace GlassDesk.AppServices.Tests;

public sealed class SteamShortcutIconRecoveryTests
{
    private static string CreateIcon(TempWorkspace temp)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "GlassDesk.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var copy = Path.Combine(temp.Root, "steam game.ico");
        File.Copy(Path.Combine(root.FullName, "src", "GlassDesk.App", "Assets", "GlassDesk.ico"), copy);
        return copy;
    }

    [Fact]
    public async Task AutoImportKeepsSteamExplicitIconWhenCopyingTheUrl()
    {
        using var temp = new TempWorkspace(); var desktop = temp.CreateDirectory("桌面");
        var icon = CreateIcon(temp);
        var url = temp.CreateFile("桌面/游戏.url", $"[InternetShortcut]\nURL=steam://rungameid/880940\nIconFile={icon}\nIconIndex=0\n");
        var data = temp.CreateStore(); data.LoadAll(); using var host = new AppServiceHost(data);
        using var discovery = new DesktopAppDiscoveryService(host, host.AutoOrganization, [desktop]);
        discovery.Configure(action => { action(); return Task.CompletedTask; });
        await discovery.EnableWithBaselineAsync(await discovery.PreviewExistingAsync(), [url]);
        var entry = Assert.Single(host.Library.Items);
        Assert.Equal(icon, entry.IconSource);
        Assert.EndsWith(".url", entry.Target);
        Assert.NotEqual(Path.GetFullPath(url), entry.Target);
        Assert.NotNull(host.Icons.EnsureFor(entry.IconSource));
    }

    [Fact]
    public async Task ExistingWhitePageSourceRepairsWithoutChangingIdentityOrUserIcon()
    {
        using var temp = new TempWorkspace(); var icon = CreateIcon(temp);
        var url = temp.CreateFile("managed/game.url", $"[InternetShortcut]\nURL=steam://rungameid/1\nIconFile={icon}\nIconIndex=0\n");
        var data = temp.CreateStore(); data.LoadAll();
        data.Library.Add(new AppEntry { Id = "old-game", DisplayName = "我的游戏", Kind = AppItemKind.Shortcut, Target = url, IconSource = url });
        data.Library.Add(new AppEntry { Id = "custom", DisplayName = "自选图标", Kind = AppItemKind.Shortcut, Target = url, Arguments = "other", IconSource = url, CustomIconPath = "my-picture.png" });
        data.Library.Save();
        using var host = new AppServiceHost(data);
        var oldCache = host.Icons.EnsureFor(url)?.Path;
        var preview = await host.Maintenance.PrepareInternetShortcutIconRepairsAsync();
        Assert.Single(preview);
        Assert.Equal(1, host.Maintenance.ApplyInternetShortcutIconRepairs(preview));
        var entry = host.Library.Find("old-game")!;
        Assert.Equal("我的游戏", entry.DisplayName); Assert.Equal(url, entry.Target);
        Assert.Equal(icon, entry.IconSource);
        Assert.NotEqual(oldCache, host.Icons.PathFor(entry.IconSource!));
        Assert.Equal("my-picture.png", host.Library.Find("custom")!.CustomIconPath);
        Assert.Empty(await host.Maintenance.PrepareInternetShortcutIconRepairsAsync());
    }

    [Fact]
    public async Task RepairDoesNotOverwriteAnIconChosenWhilePreparationWasRunning()
    {
        using var temp = new TempWorkspace(); var icon = CreateIcon(temp);
        var url = temp.CreateFile("game.url", $"[InternetShortcut]\nIconFile={icon}\n");
        var data = temp.CreateStore(); data.LoadAll();
        data.Library.Add(new AppEntry { Id = "game", Target = url, Kind = AppItemKind.Shortcut, IconSource = url }); data.Library.Save();
        using var host = new AppServiceHost(data);
        var repairs = await host.Maintenance.PrepareInternetShortcutIconRepairsAsync(); Assert.Single(repairs);
        host.Library.Find("game")!.CustomIconPath = "chosen-by-user.ico";
        Assert.Equal(0, host.Maintenance.ApplyInternetShortcutIconRepairs(repairs));
        Assert.Equal(url, host.Library.Find("game")!.IconSource);
    }

    [Fact]
    public async Task FailedRepairSaveRollsBackTheInMemorySource()
    {
        using var temp = new TempWorkspace(); var icon = CreateIcon(temp);
        var url = temp.CreateFile("game.url", $"[InternetShortcut]\nIconFile={icon}\n");
        var data = temp.CreateStore(); data.LoadAll();
        data.Library.Add(new AppEntry { Id = "game", Target = url, Kind = AppItemKind.Shortcut, IconSource = url }); data.Library.Save();
        using var host = new AppServiceHost(data);
        var repairs = await host.Maintenance.PrepareInternetShortcutIconRepairsAsync(); Assert.Single(repairs);
        File.Delete(data.Paths.AppLibraryFile); Directory.CreateDirectory(data.Paths.AppLibraryFile);
        Assert.Throws<StorageException>(() => host.Maintenance.ApplyInternetShortcutIconRepairs(repairs));
        Assert.Equal(url, host.Library.Find("game")!.IconSource);
    }

    [Fact]
    public async Task StoppingDiscoveryRejectsAQueuedIconRepairCommit()
    {
        using var temp = new TempWorkspace(); var icon = CreateIcon(temp);
        var url = temp.CreateFile("game.url", $"[InternetShortcut]\nIconFile={icon}\n");
        var data = temp.CreateStore(); data.LoadAll();
        data.Library.Add(new AppEntry { Id = "game", Target = url, Kind = AppItemKind.Shortcut, IconSource = url }); data.Library.Save();
        using var host = new AppServiceHost(data);
        var queued = new TaskCompletionSource<Action>(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        host.DesktopDiscovery.Configure(action =>
        {
            if (Interlocked.Increment(ref calls) == 1) { action(); return Task.CompletedTask; }
            queued.TrySetResult(action); return delivered.Task;
        });
        var repair = host.DesktopDiscovery.RepairShortcutIconsAsync();
        var commit = await queued.Task.WaitAsync(TimeSpan.FromSeconds(10));
        host.DesktopDiscovery.Stop(); commit(); delivered.SetResult();
        Assert.Equal(0, await repair);
        Assert.Equal(url, host.Library.Find("game")!.IconSource);
    }
}
