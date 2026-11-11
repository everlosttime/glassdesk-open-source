using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;

namespace GlassDesk.AppServices.Tests;

public sealed class LauncherGroupModeWorkspaceTests
{
    [Fact]
    public void FlatDragCommitsItsOrderWithoutReplacingFolderTreeAndRejectsStaleEdit()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "GlassDesk-flat-workspace-" + Guid.NewGuid().ToString("N")));
        try
        {
            var data = new GlassDeskDataStore(new AppPaths(root)); data.LoadAll();
            foreach (var id in new[] { "a", "b", "c" })
                data.Library.Add(new() { Id = id, DisplayName = id, Target = "missing", IsTargetMissing = true });
            using var host = new AppServiceHost(data);
            var workspace = new LauncherWorkspace(host);
            var populated = LauncherLayoutEditor.AddShortcuts(workspace.Layout, ["a", "b", "c"], 4);
            var folder = LauncherLayoutEditor.CreateGroup(populated, "b", "a", "folder", 4);
            Assert.True(host.Layouts.CommitLauncher(folder, host.Layouts.LauncherVersion));
            var edit = workspace.BeginGroupModeEdit(null, 4);
            var stale = workspace.BeginEdit();
            edit.Draft = LauncherLayoutEditor.PlaceAtSlot(edit.Draft, "c", 0, 4, false);
            Assert.True(workspace.Commit(edit));
            Assert.Equal(new[] { "c", "a", "b" }, workspace.Layout.FlatOrder);
            Assert.Equal(new[] { "a", "b" }, workspace.Layout.Pages[0].Slots[0].Group!.ItemIds);
            Assert.False(workspace.Commit(stale));
            var reload = new GlassDeskDataStore(new AppPaths(root)); reload.LoadAll();
            Assert.Equal(new[] { "c", "a", "b" }, reload.Layouts.Launcher.FlatOrder);
        }
        finally
        {
            if (Path.GetDirectoryName(root) != Path.TrimEndingDirectorySeparator(Path.GetTempPath())) throw new IOException("Unexpected test directory.");
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
