using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassDesk.Platform;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock.Tests;

public sealed class CachedWallpaperRenderingTests
{
    [Fact]
    public void DeferredRenderingStillPollsMetadataAndBuildsTheLatestFallback()
    {
        StaTest.Run(() =>
        {
            uint color = 0x00C800;
            var wallpaper = new WallpaperBackdrop(readDesktop: () => new DesktopWallpaperSnapshot(
                [new WallpaperMonitor("", new(0, 0, 32, 16))], DesktopWallpaperPosition.Fill, color));
            Assert.False(wallpaper.Update(new Rect(0, 0, 32, 16), 32, 16, 0, renderPixels: false));
            Assert.Null(wallpaper.Pixels);
            color = 0xC80000;
            Expire(wallpaper);
            Assert.False(wallpaper.Update(new Rect(0, 0, 32, 16), 32, 16, 0, renderPixels: false));
            Assert.True(wallpaper.Update(new Rect(0, 0, 32, 16), 32, 16, 0));
            Assert.Equal(new byte[] { 200, 0, 0, 255 }, wallpaper.Pixels![..4]);
        });
    }

    [Fact]
    public void ReleasingDecodedImagesKeepsCompletedPixelsWithoutReopeningTheFile()
    {
        StaTest.Run(() =>
        {
            var root = NewRoot();
            var original = Path.Combine(root, "wallpaper.png");
            WriteImage(original, Color.FromRgb(37, 123, 211));
            var wallpaper = Create(original, "unused");
            Assert.True(wallpaper.Update(new Rect(0, 0, 32, 16), 32, 16, 0));
            var completed = Assert.IsType<byte[]>(wallpaper.Pixels);
            var expected = completed.ToArray();
            wallpaper.ReleaseDecodedImages();
            var cache = Assert.IsAssignableFrom<System.Collections.IDictionary>(typeof(WallpaperBackdrop)
                .GetField("_images", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(wallpaper));
            Assert.Empty(cache);
            using var locked = new FileStream(original, FileMode.Open, FileAccess.Read, FileShare.None);
            Assert.False(wallpaper.Update(new Rect(0, 0, 32, 16), 32, 16, 0));
            Assert.Same(completed, wallpaper.Pixels);
            Assert.Equal(expected, wallpaper.Pixels);
        });
    }

    [Fact]
    public void ReleasedDecodeIsRebuiltWhenWallpaperFileChanges()
    {
        StaTest.Run(() =>
        {
            var root = NewRoot();
            var original = Path.Combine(root, "wallpaper.png");
            WriteImage(original, Colors.Red);
            var wallpaper = Create(original, "unused");
            Read(wallpaper, new byte[] { 0, 0, 255, 255 });
            wallpaper.ReleaseDecodedImages();
            WriteImage(original, Colors.Blue);
            File.SetLastWriteTimeUtc(original, DateTime.UtcNow.AddSeconds(5));
            Expire(wallpaper);
            Read(wallpaper, new byte[] { 255, 0, 0, 255 });
        });
    }

    [Fact]
    public void ReleasingSuccessfulDecodesPreservesTheRetryForAnUnavailableImage()
    {
        StaTest.Run(() =>
        {
            var original = Path.Combine(NewRoot(), "wallpaper.png");
            WriteImage(original, Colors.Lime);
            var valid = File.ReadAllBytes(original);
            var written = File.GetLastWriteTimeUtc(original);
            var invalid = valid.ToArray(); invalid[0] = 0;
            File.WriteAllBytes(original, invalid);
            File.SetLastWriteTimeUtc(original, written);
            var wallpaper = Create(original, "unused");
            Assert.True(wallpaper.Update(new Rect(0, 0, 32, 16), 32, 16, 0));
            Assert.Null(wallpaper.Pixels);
            wallpaper.ReleaseDecodedImages();
            // Same file identity/mtime: only the failed-decode retry can wake it.
            File.WriteAllBytes(original, valid);
            File.SetLastWriteTimeUtc(original, written);
            Expire(wallpaper);
            Read(wallpaper, new byte[] { 0, 255, 0, 255 });
        });
    }

    [Fact]
    public void MissingOriginalRendersTheValidatedExtensionlessWindowsCache()
    {
        StaTest.Run(() =>
        {
            var root = NewRoot();
            var original = Path.Combine(root, "deleted.png");
            var cache = Path.Combine(root, "TranscodedWallpaper");
            WriteImage(cache, Colors.White);
            var wallpaper = Create(original, cache);
            Assert.True(wallpaper.Update(new Rect(0, 0, 32, 16), 32, 16, 0));
            Assert.NotNull(wallpaper.Pixels);
            Assert.Equal(new byte[] { 255, 255, 255, 255 }, wallpaper.Pixels[..4]);
            Assert.Equal("DecodedWindowsCache", wallpaper.SourceKind);
        });
    }

    [Fact]
    public void CacheRefreshAndRestoredOriginalReplacePreviouslyDecodedPixels()
    {
        StaTest.Run(() =>
        {
            var root = NewRoot();
            var original = Path.Combine(root, "deleted.png");
            var cache = Path.Combine(root, "TranscodedWallpaper");
            WriteImage(cache, Colors.Lime);
            var wallpaper = Create(original, cache);
            Read(wallpaper, new byte[] { 0, 255, 0, 255 });
            WriteImage(cache, Colors.Red);
            File.SetLastWriteTimeUtc(cache, DateTime.UtcNow.AddSeconds(5));
            Expire(wallpaper);
            Read(wallpaper, new byte[] { 0, 0, 255, 255 });
            WriteImage(original, Colors.Blue);
            Expire(wallpaper);
            Read(wallpaper, new byte[] { 255, 0, 0, 255 });
            Assert.Equal("DecodedOriginal", wallpaper.SourceKind);
        });
    }

    [Fact]
    public void CacheIdentityChangingDuringDecodeCannotPublishTheOldImage()
    {
        StaTest.Run(() =>
        {
            var root = NewRoot();
            var original = Path.Combine(root, "deleted.png");
            var cache = Path.Combine(root, "TranscodedWallpaper");
            WriteImage(cache, Colors.White);
            var calls = 0;
            var wallpaper = new WallpaperBackdrop((_, _) => ++calls <= 2 ? cache : null,
                () => Snapshot(original));
            wallpaper.Update(new Rect(0, 0, 32, 16), 32, 16, 0);
            Assert.Null(wallpaper.Pixels);
            Assert.Equal("Unavailable", wallpaper.SourceKind);
        });
    }

    [Fact]
    public void MissingOriginalWithoutVerifiedCacheStaysUnavailable()
    {
        StaTest.Run(() =>
        {
            var original = Path.Combine(NewRoot(), "deleted.png");
            var wallpaper = new WallpaperBackdrop((_, _) => null, () => Snapshot(original));
            wallpaper.Update(new Rect(0, 0, 32, 16), 32, 16, 0);
            Assert.Null(wallpaper.Pixels);
            Assert.Equal("Unavailable", wallpaper.SourceKind);
        });
    }

    [Fact]
    public void SolidColorDoesNotConsultAnUnrelatedCache()
    {
        StaTest.Run(() =>
        {
            var wallpaper = new WallpaperBackdrop((_, _) => throw new InvalidOperationException("Not a file wallpaper"),
                () => Snapshot(""));
            wallpaper.Update(new Rect(0, 0, 32, 16), 32, 16, 0);
            Assert.NotNull(wallpaper.Pixels);
            Assert.Equal(new byte[] { 0, 200, 0, 255 }, wallpaper.Pixels[..4]);
            Assert.Equal("SolidColor", wallpaper.SourceKind);
        });
    }

    private static WallpaperBackdrop Create(string original, string cache)
        => new((_, _) => cache, () => Snapshot(original));

    private static DesktopWallpaperSnapshot Snapshot(string original)
        => new(new[] { new WallpaperMonitor(original, new(0, 0, 32, 16)) }, DesktopWallpaperPosition.Fill, 0x00C800);

    private static void Read(WallpaperBackdrop wallpaper, byte[] expected)
    {
        Assert.True(wallpaper.Update(new Rect(0, 0, 32, 16), 32, 16, 0));
        Assert.NotNull(wallpaper.Pixels);
        Assert.Equal(expected, wallpaper.Pixels[..4]);
    }

    private static void Expire(WallpaperBackdrop wallpaper)
        => typeof(WallpaperBackdrop).GetField("_refreshAt", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(wallpaper, DateTime.MinValue);

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "GlassDesk-cache-render-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void WriteImage(string path, Color color)
    {
        var pixels = Enumerable.Range(0, 32 * 16).SelectMany(_ => new byte[] { color.B, color.G, color.R, (byte)255 }).ToArray();
        var bitmap = BitmapSource.Create(32, 16, 96, 96, PixelFormats.Bgra32, null, pixels, 32 * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path);
        encoder.Save(output);
    }
}
