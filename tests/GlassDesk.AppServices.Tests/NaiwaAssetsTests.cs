using GlassDesk.App;

namespace GlassDesk.AppServices.Tests;

public sealed class NaiwaAssetsTests
{
    [Theory]
    [InlineData(true, false, "bundled")]
    [InlineData(true, true, "bundled")]
    [InlineData(false, true, "legacy")]
    [InlineData(false, false, null)]
    public void ResolvesCompleteBundledAssetsWithoutImportingOrChangingLegacyData(bool bundleReady, bool legacyReady, string? expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "GlassDesk-Naiwa-Resolve-" + Guid.NewGuid().ToString("N"));
        var bundled = Path.Combine(root, "bundled");
        var legacy = Path.Combine(root, "legacy");
        try
        {
            CreateAssets(bundled, bundleReady);
            CreateAssets(legacy, legacyReady);
            var before = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .ToDictionary(path => path, File.ReadAllText);
            var actual = NaiwaAssets.ResolveDirectory(bundled, legacy);
            Assert.Equal(expected is null ? null : Path.Combine(root, expected), actual);
            Assert.Equal(before, Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllText));
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
