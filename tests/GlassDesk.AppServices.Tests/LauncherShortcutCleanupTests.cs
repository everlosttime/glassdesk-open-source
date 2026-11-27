using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;

namespace GlassDesk.AppServices.Tests;

public sealed class LauncherShortcutCleanupTests
{
    [Fact]
    public async Task RepeatedAbsenceRemovesOnlyTheLauncherShortcutAndItsGroupReferences()
    {
        using var workspace = new TempWorkspace();
        var data = workspace.CreateStore();
        data.LoadAll();
        using var host = new AppServiceHost(data);
        var clock = new TestClock();
        var cleanup = new LauncherShortcutCleanup(host, clock);
        var missing = AddShortcut(host, "missing", Path.Combine(workspace.Root, "uninstalled", "app.exe"));
        var healthy = AddShortcut(host, "healthy", workspace.CreateProgram("installed.cmd"));
        healthy.IconSource = Path.Combine(workspace.Root, "old-icon.ico");
        var network = AddShortcut(host, "network", @"\\offline.invalid\apps\app.exe");
        var layout = LauncherLayoutEditor.AddShortcuts(data.Layouts.Launcher, [missing.Id, healthy.Id, network.Id], 35);
        layout = LauncherLayoutEditor.CreateGroup(layout, missing.Id, healthy.Id, "folder", 35);
        layout = LauncherFilterRules.AddGroup(layout, "filter", "测试分组", [missing.Id, healthy.Id]);
        Assert.True(data.Layouts.CommitLauncher(layout, data.Layouts.LauncherVersion));
        data.Layouts.Dock.PinnedItemIds.Add(missing.Id);
        data.Layouts.SaveDock();

        Assert.Equal(0, await cleanup.SweepAsync());
        clock.Advance();
        data.Settings.Current.Launcher.AutoRemoveBrokenShortcutsEnabled = true;
        Assert.Equal(0, await cleanup.SweepAsync());
        Assert.Equal(0, await cleanup.SweepAsync());
        clock.Advance(TimeSpan.FromMilliseconds(59_990));
        Assert.Equal(1, await cleanup.SweepAsync());

        var reloaded = new LayoutStore(data.Paths.DockLayoutFile, data.Paths.LauncherLayoutFile);
        reloaded.LoadLauncher();
        reloaded.LoadDock();
        Assert.Equal(new[] { healthy.Id, network.Id }, LauncherLayoutRules.AppIds(reloaded.Launcher));
        Assert.Equal(new[] { healthy.Id }, Assert.Single(reloaded.Launcher.FilterGroups).ItemIds);
        Assert.Equal(new[] { missing.Id }, reloaded.Dock.PinnedItemIds);
        Assert.NotNull(host.Library.Find(missing.Id));
        Assert.True(File.Exists(healthy.Target));
    }

    [Fact]
    public async Task ATargetThatReturnsStartsASeparateConfirmationPeriod()
    {
        using var workspace = new TempWorkspace();
        var data = workspace.CreateStore();
        data.LoadAll();
        using var host = new AppServiceHost(data);
        var clock = new TestClock();
        var cleanup = new LauncherShortcutCleanup(host, clock);
        var entry = AddShortcut(host, "temporarily missing", Path.Combine(workspace.Root, "app.exe"));
        Assert.True(data.Layouts.CommitLauncher(LauncherLayoutEditor.AddShortcuts(data.Layouts.Launcher,
            [entry.Id], 35), data.Layouts.LauncherVersion));
        data.Settings.Current.Launcher.AutoRemoveBrokenShortcutsEnabled = true;

        Assert.Equal(0, await cleanup.SweepAsync());
        workspace.CreateProgram("app.exe");
        clock.Advance();
        Assert.Equal(0, await cleanup.SweepAsync());
        File.Delete(entry.Target);
        clock.Advance();
        Assert.Equal(0, await cleanup.SweepAsync());
        Assert.Contains(entry.Id, LauncherLayoutRules.AppIds(data.Layouts.Launcher));
        clock.Advance();
        Assert.Equal(1, await cleanup.SweepAsync());
    }

    private static AppEntry AddShortcut(AppServiceHost host, string id, string target)
    {
        var entry = new AppEntry { Id = id, DisplayName = id, Kind = AppItemKind.Program, Target = target };
        Assert.Equal(AddEntryResult.Added, host.Library.Add(entry));
        return entry;
    }

    private sealed class TestClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan? elapsed = null) => _timestamp += (elapsed ?? TimeSpan.FromMinutes(1)).Ticks;
    }
}
