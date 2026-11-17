using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;

namespace GlassDesk.Core.Tests;

public sealed class DesktopAutomationBackupTests
{
    [Fact]
    public void ManagedShortcutsAndSyncStateTravelWithBackupAndRebaseTargets()
    {
        using var source = new TempDataDirectory();
        using var archive = new TempDataDirectory();
        using var destination = new TempDataDirectory();
        var data = source.CreateStore();
        data.LoadAll();
        var managed = Path.Combine(source.Root, "managed-shortcuts", "example.lnk");
        Directory.CreateDirectory(Path.GetDirectoryName(managed)!);
        File.WriteAllText(managed, "shortcut bytes");
        File.WriteAllText(Path.Combine(source.Root, "desktop-sync.json"), "{\"schemaVersion\":1,\"baselineEstablished\":true,\"sources\":[]}");
        data.Library.Add(new AppEntry { Id = "managed", Target = managed, Kind = AppItemKind.Shortcut, IconSource = managed, AutomaticSourceKey = "aumid:example" });
        data.Library.Save();
        Assert.True(data.Backup.BackupTo(archive.Root).Succeeded);
        Assert.True(File.Exists(Path.Combine(archive.Root, "managed-shortcuts", "example.lnk")));
        var restored = destination.CreateStore();
        Assert.True(restored.Backup.RestoreFrom(archive.Root).Succeeded);
        restored.LoadAll();
        Assert.Equal(Path.Combine(destination.Root, "managed-shortcuts", "example.lnk"), restored.Library.Find("managed")!.Target);
        Assert.True(File.Exists(Path.Combine(destination.Root, "desktop-sync.json")));
    }

    [Fact]
    public void CorruptSyncStateRejectsRestoreBeforeChangingCurrentData()
    {
        using var source = new TempDataDirectory();
        using var archive = new TempDataDirectory();
        var data = source.CreateStore();
        data.LoadAll();
        data.Settings.Save();
        data.Backup.BackupTo(archive.Root);
        File.WriteAllText(Path.Combine(archive.Root, "desktop-sync.json"), "broken");
        Assert.False(data.Backup.RestoreFrom(archive.Root).Succeeded);
    }

    [Fact]
    public void LegacyRestoreDisablesAutomationAndDiscardsAnUnrelatedBaseline()
    {
        using var source = new TempDataDirectory();
        using var archive = new TempDataDirectory();
        using var destination = new TempDataDirectory();
        var old = source.CreateStore();
        old.LoadAll();
        old.Settings.Current.Launcher.AutoAddDesktopAppsEnabled = true;
        old.Settings.Save();
        old.Backup.BackupTo(archive.Root);
        var current = destination.CreateStore();
        current.LoadAll();
        File.WriteAllText(Path.Combine(destination.Root, "desktop-sync.json"), "{\"schemaVersion\":1,\"sources\":[]}");
        File.WriteAllText(Path.Combine(destination.Root, "desktop-sync.json.bak"), "{\"schemaVersion\":1,\"baselineEstablished\":true,\"sources\":[]}");
        Assert.True(current.Backup.RestoreFrom(archive.Root).Succeeded);
        current.LoadAll();
        Assert.False(current.Settings.Current.Launcher.AutoAddDesktopAppsEnabled);
        Assert.False(File.Exists(Path.Combine(destination.Root, "desktop-sync.json")));
        Assert.False(File.Exists(Path.Combine(destination.Root, "desktop-sync.json.bak")));
    }

    [Fact]
    public void ManualShortcutInAnUnrelatedSameNamedFolderIsNeverRebased()
    {
        using var temp = new TempDataDirectory();
        var data = temp.CreateStore(); data.LoadAll();
        var target = @"C:\UserFiles\managed-shortcuts\manual.lnk";
        data.Library.Add(new AppEntry { Id = "manual", Target = target, Kind = AppItemKind.Shortcut });
        data.Library.Save();
        data.LoadAll();
        Assert.Equal(target, data.Library.Find("manual")!.Target);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1,\"sources\":[42]}")]
    [InlineData("{\"schemaVersion\":1,\"sources\":[null]}")]
    [InlineData("{\"schemaVersion\":1,\"sources\":[],\"suppressedKeys\":[false]}")]
    public void InvalidSyncEntriesRejectRestore(string invalid)
    {
        using var source = new TempDataDirectory(); using var archive = new TempDataDirectory();
        var data = source.CreateStore(); data.LoadAll(); data.Settings.Save();
        data.Backup.BackupTo(archive.Root);
        File.WriteAllText(Path.Combine(archive.Root, "desktop-sync.json"), invalid);
        Assert.False(data.Backup.RestoreFrom(archive.Root).Succeeded);
    }
}
