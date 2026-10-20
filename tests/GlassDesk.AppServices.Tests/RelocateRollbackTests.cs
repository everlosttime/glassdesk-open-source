using GlassDesk.AppServices;
using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;

namespace GlassDesk.AppServices.Tests;

public sealed class RelocateRollbackTests
{
    [Fact]
    public void SaveFailure_RestoresSharedEntryAndDisk()
    {
        var root = Path.Combine(Path.GetTempPath(), "GlassDesk-Relocate-" + Guid.NewGuid().ToString("n"));
        try
        {
            var data = new GlassDeskDataStore(new AppPaths(root));
            data.LoadAll();
            var oldPath = Path.Combine(root, "old.txt");
            var newPath = Path.Combine(root, "new.txt");
            File.WriteAllText(oldPath, "old"); File.WriteAllText(newPath, "new");
            data.Library.Add(new AppEntry { Id = "a", Target = oldPath, DisplayName = "A" });
            data.Library.Save();
            using var host = new AppServiceHost(data);
            using (File.Open(data.Library.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.Equal(RelocateResult.SaveFailed, host.Maintenance.Relocate("a", newPath).Result);
                Assert.Equal(oldPath, data.Library.Find("a")!.Target);
            }
            var reloaded = new GlassDeskDataStore(new AppPaths(root));
            reloaded.LoadAll();
            Assert.Equal(oldPath, reloaded.Library.Find("a")!.Target);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
