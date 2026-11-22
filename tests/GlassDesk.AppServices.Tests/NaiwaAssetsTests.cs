using GlassDesk.App;

namespace GlassDesk.AppServices.Tests;

public sealed class NaiwaAssetsTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnImportedPackIsReadyOnlyWhenAllAssetsHaveContent(bool complete)
    {
        var root = Path.Combine(Path.GetTempPath(), "GlassDesk-Naiwa-Ready-" + Guid.NewGuid().ToString("N"));
        try
        {
            CreateAssets(root, complete);
            Assert.Equal(complete, NaiwaAssets.AreReady(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static void CreateAssets(string directory, bool complete)
    {
        Directory.CreateDirectory(directory);
        foreach (var name in new[] { "素材.nwp", "idle_gif.nwp", "drag.nwp", "dance.nwp", "eye_art.nwp", "fat.nwp", "feed.nwp",
            "head.nwp", "hold.nwp", "hunger.nwp", "rest.nwp", "rest2.nwp", "sad.nwp", "seq.nwp", "walk.nwp",
            "dance.mp3", "原版笑声.wav", "cry.wav" })
            File.WriteAllText(Path.Combine(directory, name), !complete && name == "素材.nwp" ? "" : name);
    }

    [Fact]
    public void RemovingImportedFilesLeavesBundledAssetsAndOtherUserFilesIntact()
    {
        var root = Path.Combine(Path.GetTempPath(), "GlassDesk-Naiwa-Remove-" + Guid.NewGuid().ToString("N"));
        var bundled = Path.Combine(root, "installed", "assets");
        var imported = Path.Combine(root, "naiwa", "assets");
        var preferences = Path.Combine(root, "naiwa", "settings.ini");
        var originalZip = Path.Combine(root, "素材包.zip");
        var unrelated = Path.Combine(imported, "说明.txt");
        try
        {
            CreateAssets(bundled, complete: true);
            CreateAssets(imported, complete: true);
            File.WriteAllText(preferences, "original preferences");
            File.WriteAllText(originalZip, "original ZIP");
            File.WriteAllText(unrelated, "unrelated file");

            Assert.True(NaiwaAssets.HasImportedFiles(imported));
            NaiwaAssets.RemoveImportedFiles(imported);

            Assert.False(NaiwaAssets.HasImportedFiles(imported));
            Assert.Equal([unrelated], Directory.GetFiles(imported));
            Assert.Equal("unrelated file", File.ReadAllText(unrelated));
            Assert.Equal("original ZIP", File.ReadAllText(originalZip));
            Assert.Equal("original preferences", File.ReadAllText(preferences));
            Assert.True(NaiwaAssets.AreReady(bundled));
            Assert.False(NaiwaAssets.AreReady(imported));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

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
