using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;

namespace GlassDesk.AppServices.Tests;

public sealed class AutomaticRemovalPersistenceTests
{
    [Fact]
    public void RemovingThroughWorkspaceCommitsTheLogicalSuppressionWithTheLayout()
    {
        using var temp = new TempWorkspace();
        var data = temp.CreateStore(); data.LoadAll();
        using var host = new AppServiceHost(data);
        var target = temp.CreateFile("program.exe");
        host.Import.ImportItems([new() { Source = target }]);
        var entry = Assert.Single(host.Library.Items);
        var workspace = new LauncherWorkspace(host);
        var edit = workspace.BeginEdit();
        edit.Draft = LauncherLayoutEditor.RemoveShortcut(edit.Draft, entry.Id, 35);
        Assert.True(workspace.Commit(edit));
        var reopened = temp.CreateStore(); reopened.LoadAll();
        Assert.Empty(LauncherLayoutRules.AppIds(reopened.Layouts.Launcher));
        Assert.Contains(LauncherAutoOrganizationService.GetLogicalKey(entry), reopened.Layouts.Launcher.SuppressedAutoAddKeys);
        host.Import.ImportItems([new() { Source = target }]);
        Assert.DoesNotContain(LauncherAutoOrganizationService.GetLogicalKey(entry), host.Layouts.Launcher.SuppressedAutoAddKeys);
        Assert.Contains(entry.Id, LauncherLayoutRules.AppIds(host.Layouts.Launcher));
    }
}
