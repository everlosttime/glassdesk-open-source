using GlassDesk.AppServices;
using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;

namespace GlassDesk.AppServices.Tests;

public sealed class AppEntryEditorTests
{
    [Fact]
    public void Edit_RenamesAndPreservesStableIdAfterReload()
    {
        var root = Path.Combine(Path.GetTempPath(), "GlassDesk-Edit-" + Guid.NewGuid().ToString("n"));
        try
        {
            var data = new GlassDeskDataStore(new AppPaths(root));
            data.LoadAll();
            data.Library.Add(new AppEntry { Id = "a", DisplayName = "旧名", Target = "unused" });
            data.Library.Save();
            using var host = new AppServiceHost(data);
            Assert.True(host.Editor.Edit("a", "新名", ["alias"], null));
            var reloaded = new GlassDeskDataStore(new AppPaths(root));
            reloaded.LoadAll();
            Assert.Equal("新名", reloaded.Library.Find("a")!.DisplayName);
            Assert.Equal(["alias"], reloaded.Library.Find("a")!.SearchAliases);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void SaveFailure_RestoresNameAliasesAndSearch()
    {
        var root = Path.Combine(Path.GetTempPath(), "GlassDesk-EditFail-" + Guid.NewGuid().ToString("n"));
        try
        {
            var data = new GlassDeskDataStore(new AppPaths(root));
            data.LoadAll();
            data.Library.Add(new AppEntry { Id = "a", DisplayName = "旧名", Target = "unused", SearchAliases = ["old"] });
            data.Library.Save();
            using var host = new AppServiceHost(data);
            using (File.Open(data.Library.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
                Assert.Throws<StorageException>(() => host.Editor.Edit("a", "新名", ["new"], null));
            Assert.Equal("旧名", data.Library.Find("a")!.DisplayName);
            Assert.Equal(["old"], data.Library.Find("a")!.SearchAliases);
            Assert.Empty(host.Search.SearchApps("new", 500));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
