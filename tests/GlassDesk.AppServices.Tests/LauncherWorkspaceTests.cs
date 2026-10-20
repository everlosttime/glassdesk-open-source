using GlassDesk.AppServices;
using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;

namespace GlassDesk.AppServices.Tests;

public sealed class LauncherWorkspaceTests
{
    [Fact]
    public void Reconcile_UsesSharedLibraryAndDoesNotRewriteUnchangedLayout()
    {
        var root = Path.Combine(Path.GetTempPath(), "GlassDesk-LauncherWorkspace-" + Guid.NewGuid().ToString("n"));
        try
        {
            var data = new GlassDeskDataStore(new AppPaths(root));
            data.LoadAll();
            data.Library.Add(new AppEntry { Id = "a", DisplayName = "A", Target = "missing", IsTargetMissing = true });
            data.Library.Save();
            using var host = new AppServiceHost(data);
            var workspace = new LauncherWorkspace(host);
            Assert.True(workspace.Reconcile(35));
            Assert.Equal("a", data.Layouts.Launcher.Pages[0].Slots[0].ItemId);
            var stamp = File.GetLastWriteTimeUtc(data.Layouts.LauncherFilePath);
            Assert.False(workspace.Reconcile(35));
            Assert.Equal(stamp, File.GetLastWriteTimeUtc(data.Layouts.LauncherFilePath));
            Assert.Empty(data.Layouts.Dock.PinnedItemIds);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
