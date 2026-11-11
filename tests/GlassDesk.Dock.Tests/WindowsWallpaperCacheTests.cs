using System.IO;
using System.Reflection;
using System.Text;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock.Tests;

public sealed class WindowsWallpaperCacheTests
{
    [Fact]
    public void MissingOriginalResolvesOnlyTheCacheWithMatchingWindowsIdentity()
    {
        using var fixture = new CacheFixture();
        var resolve = fixture.CreateResolver(() => (Identity(fixture.OriginalPath), fixture.OriginalPath));

        Assert.False(File.Exists(fixture.OriginalPath));
        Assert.Equal(fixture.CachePath, resolve(fixture.OriginalPath, 1));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void MatchingCacheDoesNotRequireAnAdditionalRegistryWallpaperPath(string? registryWallpaper)
    {
        using var fixture = new CacheFixture();
        var resolve = fixture.CreateResolver(() => (Identity(fixture.OriginalPath), registryWallpaper));

        Assert.Equal(fixture.CachePath, resolve(fixture.OriginalPath, 1));
    }

    [Fact]
    public void RegistryPointingToTheExactTranscodedCacheRetainsTheMatchingOriginalIdentity()
    {
        using var fixture = new CacheFixture();
        var resolve = fixture.CreateResolver(() => (Identity(fixture.OriginalPath), fixture.CachePath));

        Assert.Equal(fixture.CachePath, resolve(fixture.OriginalPath, 1));
    }

    [Fact]
    public void RegistryPointingToAnUnrelatedCacheCannotAuthorizeItsPixels()
    {
        using var fixture = new CacheFixture();
        var resolve = fixture.CreateResolver(() => (Identity(fixture.OriginalPath), Path.Combine(fixture.Root, "CachedFiles", "previous.jpg")));

        Assert.Null(resolve(fixture.OriginalPath, 1));
    }

    [Fact]
    public void MatchingPathsAreNormalizedWithoutRequiringTheDeletedOriginalToExist()
    {
        using var fixture = new CacheFixture();
        var original = Path.Combine(fixture.Root, "images", "..", "missing.png");
        var resolve = fixture.CreateResolver(() => (Identity(fixture.OriginalPath.ToUpperInvariant()), original));

        Assert.Equal(fixture.CachePath, resolve(original, 1));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("missing.png")]
    [InlineData("C:missing.png")]
    public void SolidColorAndRelativeOriginalPathsNeverUseAnOldCache(string original)
    {
        using var fixture = new CacheFixture();
        var resolve = fixture.CreateResolver(() => (Identity(fixture.OriginalPath), fixture.OriginalPath));

        Assert.Null(resolve(original, 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    public void AmbiguousMonitorMappingNeverAppliesOneCacheToOtherDisplays(int monitorCount)
    {
        using var fixture = new CacheFixture();
        var resolve = fixture.CreateResolver(() => (Identity(fixture.OriginalPath), fixture.OriginalPath));

        Assert.Null(resolve(fixture.OriginalPath, monitorCount));
    }

    [Fact]
    public void OldTranscodedIdentityDoesNotMatchTheNewCurrentWallpaper()
    {
        using var fixture = new CacheFixture();
        var resolve = fixture.CreateResolver(() => (Identity(Path.Combine(fixture.Root, "previous.png")), fixture.OriginalPath));

        Assert.Null(resolve(fixture.OriginalPath, 1));
    }

    [Fact]
    public void ChangedRegistryWallpaperRejectsAnOtherwiseMatchingCache()
    {
        using var fixture = new CacheFixture();
        var resolve = fixture.CreateResolver(() => (Identity(fixture.OriginalPath), Path.Combine(fixture.Root, "new.png")));

        Assert.Null(resolve(fixture.OriginalPath, 1));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("truncated")]
    [InlineData("odd")]
    [InlineData("unterminated")]
    [InlineData("oversized")]
    [InlineData("relative")]
    [InlineData("invalid-unicode")]
    public void MalformedIdentityCannotAuthorizeUnrelatedCachedPixels(string kind)
    {
        using var fixture = new CacheFixture();
        byte[]? identity = kind switch
        {
            "missing" => null,
            "truncated" => new byte[24],
            "odd" => Identity(fixture.OriginalPath).Concat(new byte[1]).ToArray(),
            "unterminated" => new byte[24].Concat(Encoding.Unicode.GetBytes(fixture.OriginalPath)).ToArray(),
            "oversized" => Identity(fixture.OriginalPath, 65_538),
            "relative" => Identity("missing.png"),
            "invalid-unicode" => new byte[24].Concat(new byte[] { 0, 216, 0, 0 }).ToArray(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var resolve = fixture.CreateResolver(() => (identity, fixture.OriginalPath));

        Assert.Null(resolve(fixture.OriginalPath, 1));
    }

    [Fact]
    public void MissingCachedImageIsNotReportedAsAUsableSource()
    {
        using var fixture = new CacheFixture();
        File.Delete(fixture.CachePath);
        var resolve = fixture.CreateResolver(() => (Identity(fixture.OriginalPath), fixture.OriginalPath));

        Assert.Null(resolve(fixture.OriginalPath, 1));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(33_554_433L)]
    public void EmptyOrOversizedCacheCannotBecomeAWallpaperSource(long length)
    {
        using var fixture = new CacheFixture();
        using (var stream = File.OpenWrite(fixture.CachePath)) stream.SetLength(length);
        var resolve = fixture.CreateResolver(() => (Identity(fixture.OriginalPath), fixture.OriginalPath));

        Assert.Null(resolve(fixture.OriginalPath, 1));
    }

    [Fact]
    public void ChangedIdentityIsRecheckedInsteadOfReusingAPreviousResolution()
    {
        using var fixture = new CacheFixture();
        var identity = Identity(fixture.OriginalPath);
        var resolve = fixture.CreateResolver(() => (identity, fixture.OriginalPath));
        Assert.Equal(fixture.CachePath, resolve(fixture.OriginalPath, 1));

        identity = Identity(Path.Combine(fixture.Root, "previous.png"));

        Assert.Null(resolve(fixture.OriginalPath, 1));
    }

    [Fact]
    public void UnavailableRegistryMetadataFailsClosedWithoutInterruptingCapture()
    {
        using var fixture = new CacheFixture();
        var resolve = fixture.CreateResolver(() => throw new UnauthorizedAccessException());

        Assert.Null(resolve(fixture.OriginalPath, 1));
    }

    private static byte[] Identity(string original, int length = 800)
    {
        var bytes = new byte[length];
        Encoding.Unicode.GetBytes(original + '\0').CopyTo(bytes, 24);
        return bytes;
    }

    private sealed class CacheFixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "GlassDesk-wallpaper-cache-" + Guid.NewGuid().ToString("N"));
        internal string CachePath => Path.Combine(Root, "TranscodedWallpaper");
        internal string OriginalPath => Path.Combine(Root, "missing.png");

        internal CacheFixture()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllBytes(CachePath, new byte[] { 1, 2, 3 });
        }

        internal Func<string, int, string?> CreateResolver(Func<(byte[]? Identity, string? Wallpaper)> readIdentity)
        {
            var type = typeof(WallpaperBackdrop).Assembly.GetType("GlassDesk.UI.Controls.WindowsWallpaperCache");
            Assert.NotNull(type);
            var instance = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new object[] { CachePath, readIdentity }, null);
            Assert.NotNull(instance);
            var resolve = type.GetMethod("Resolve", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.NotNull(resolve);
            return (original, monitors) => (string?)resolve.Invoke(instance, new object[] { original, monitors });
        }

        public void Dispose() => Directory.Delete(Root, true);
    }
}
