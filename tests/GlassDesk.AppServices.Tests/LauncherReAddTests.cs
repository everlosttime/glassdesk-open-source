using GlassDesk.Core.Contracts;
using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;

namespace GlassDesk.AppServices.Tests;

public sealed class LauncherReAddTests
{
    [Fact]
    public void OldProfileMigratesWithoutResurrectingRemovedShortcuts()
    {
        using var temp = new TempWorkspace();
        var data = temp.CreateStore();
        data.LoadAll();
        using var host = new AppServiceHost(data);
        var request = new ImportRequest { Source = temp.CreateDirectory("Old app") };
        var entry = Assert.Single(host.Import.ImportItems([request]).Added);
        File.WriteAllText(temp.DataPaths.LauncherLayoutFile,
            $"{{\"pages\":[],\"excludedAppIds\":[\"{entry.Id}\"]}}");
        var reopenedData = temp.CreateStore();
        reopenedData.LoadAll();
        using var reopened = new AppServiceHost(reopenedData);
        new LauncherWorkspace(reopened).Reconcile(35);
        Assert.Empty(LauncherLayoutRules.AppIds(reopenedData.Layouts.Launcher));
        Assert.DoesNotContain("excludedAppIds", File.ReadAllText(temp.DataPaths.LauncherLayoutFile));
        reopened.Import.ImportItems([request]);
        Assert.Equal([entry.Id], LauncherLayoutRules.AppIds(reopenedData.Layouts.Launcher));
    }

    [Fact]
    public void FailedReAddPreservesRemovedLayoutAndCanBeRetried()
    {
        using var temp = new TempWorkspace();
        var data = temp.CreateStore();
        data.LoadAll();
        using var host = new AppServiceHost(data);
        var request = new ImportRequest { Source = temp.CreateDirectory("Retry app") };
        var entry = Assert.Single(host.Import.ImportItems([request]).Added);
        var workspace = new LauncherWorkspace(host);
        var edit = workspace.BeginEdit();
        edit.Draft = LauncherLayoutEditor.RemoveShortcut(edit.Draft, entry.Id, 35);
        Assert.True(workspace.Commit(edit));
        var before = File.ReadAllBytes(temp.DataPaths.LauncherLayoutFile);
        File.SetAttributes(temp.DataPaths.LauncherLayoutFile, FileAttributes.ReadOnly);
        try
        {
            Assert.Throws<StorageException>(() => host.Import.ImportItems([request]));
            Assert.Empty(LauncherLayoutRules.AppIds(workspace.Layout));
            Assert.Equal(before, File.ReadAllBytes(temp.DataPaths.LauncherLayoutFile));
            Assert.NotNull(host.Library.Find(entry.Id));
        }
        finally { File.SetAttributes(temp.DataPaths.LauncherLayoutFile, FileAttributes.Normal); }
        host.Import.ImportItems([request]);
        Assert.Equal([entry.Id], LauncherLayoutRules.AppIds(workspace.Layout));
    }

    [Fact]
    public void ReimportAddsRemovedShortcutBackAndPreservesItsDockEntryAndCustomIcon()
    {
        using var temp = new TempWorkspace();
        var data = new GlassDeskDataStore(temp.DataPaths);
        data.LoadAll();
        using var host = new AppServiceHost(data);
        var request = new ImportRequest { Source = temp.CreateDirectory("My app") };
        var entry = Assert.Single(host.Import.ImportItems([request]).Added);
        var workspace = new LauncherWorkspace(host);
        workspace.Reconcile(35);
        entry.CustomIconPath = "custom-icon.png";
        data.Library.Save();
        data.Layouts.Dock.PinnedItemIds.Add(entry.Id);
        data.Layouts.SaveDock();
        var edit = workspace.BeginEdit();
        edit.Draft = LauncherLayoutEditor.RemoveShortcut(edit.Draft, entry.Id, 35);
        Assert.True(workspace.Commit(edit));
        workspace.Reconcile(35);
        Assert.Empty(LauncherLayoutRules.AppIds(workspace.Layout));

        var reloaded = new GlassDeskDataStore(temp.DataPaths);
        reloaded.LoadAll();
        using var reopened = new AppServiceHost(reloaded);
        var result = reopened.Import.ImportItems([request]);
        Assert.Empty(result.Failures);
        Assert.Equal([entry.Id], LauncherLayoutRules.AppIds(reloaded.Layouts.Launcher));
        Assert.Single(reloaded.Library.Items);
        Assert.Equal([entry.Id], reloaded.Layouts.Dock.PinnedItemIds);
        Assert.Equal("custom-icon.png", reloaded.Library.Find(entry.Id)!.CustomIconPath);
        Assert.DoesNotContain("excludedAppIds", File.ReadAllText(temp.DataPaths.LauncherLayoutFile));
    }
}
