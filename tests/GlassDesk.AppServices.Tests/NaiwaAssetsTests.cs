using GlassDesk.App;

namespace GlassDesk.AppServices.Tests;

public sealed class NaiwaAssetsTests
{
    [Fact]
    public async Task RejectsUnverifiedArchiveBeforeCreatingAssetDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "GlassDesk-Naiwa-Test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var zip = Path.Combine(root, "unverified.zip");
        var assets = Path.Combine(root, "assets");
        try
        {
            await File.WriteAllTextAsync(zip, "not the official Naiwa distribution");
            Assert.False(NaiwaAssets.AreReady(assets));
            await Assert.ThrowsAsync<InvalidDataException>(() => NaiwaAssets.ImportAsync(zip, assets));
            Assert.Equal(new[] { zip }, Directory.GetFileSystemEntries(root));
        }
        finally
        {
            File.Delete(zip);
            Directory.Delete(root);
        }
    }
}
