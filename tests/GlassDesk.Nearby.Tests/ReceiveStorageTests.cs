using System.Security.Cryptography;
using GlassDesk.Core.Contracts;

namespace GlassDesk.Nearby.Tests;

public sealed class ReceiveStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GlassDesk-Nearby-" + Guid.NewGuid().ToString("N"));
    public ReceiveStorageTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);
    private static NearbyFile FileEntry(string name, long size = 3, string? hash = null) => new("a", name, size, "application/octet-stream", Sha256: hash);

    [Theory]
    [InlineData("../escape")][InlineData("C:/escape")][InlineData("/escape")]
    [InlineData("a/../../escape")][InlineData("a:secret")][InlineData("a\\..\\escape")]
    [InlineData("CON.txt")][InlineData("a./b")][InlineData("a//b")]
    public void InvalidPathsNeverCreateFiles(string name)
    {
        Assert.ThrowsAny<ArgumentException>(() => new ReceiveBatch(_root, [FileEntry(name)]));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public async Task FolderCollisionRenamesTheWholeTreeAndRetainsOriginalBytes()
    {
        Directory.CreateDirectory(Path.Combine(_root, "照片"));
        File.WriteAllText(Path.Combine(_root, "照片", "original"), "keep");
        using var batch = new ReceiveBatch(_root, [FileEntry("照片/😀.png", hash: Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3 })))]);
        await batch.WriteAsync("a", new MemoryStream([1, 2, 3]), null, CancellationToken.None);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(_root, "照片 (2)", "😀.png")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(_root, "照片", "original")));
    }

    [Theory]
    [InlineData(2, null)][InlineData(4, null)][InlineData(3, "0000000000000000000000000000000000000000000000000000000000000000")]
    public async Task IncompleteOrCorruptedFileIsRemoved(int length, string? hash)
    {
        using (var batch = new ReceiveBatch(_root, [FileEntry("movie.mov", hash: hash)]))
            await Assert.ThrowsAnyAsync<IOException>(() => batch.WriteAsync("a", new MemoryStream(new byte[length]), null, CancellationToken.None));
        Assert.Empty(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CancellationKeepsCompletedFilesAndRemovesPartialFile()
    {
        using (var batch = new ReceiveBatch(_root, [FileEntry("done.jpg"), new NearbyFile("b", "partial.mp4", 3, "video/mp4")]))
        {
            await batch.WriteAsync("a", new MemoryStream([1, 2, 3]), null, CancellationToken.None);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => batch.WriteAsync("b", new MemoryStream([4, 5, 6]), null, cancelled.Token));
        }
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(_root, "done.jpg")));
        Assert.Single(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void InsufficientSpaceRejectsManifestWithoutCreatingPartialFiles()
    {
        Assert.Throws<IOException>(() => new ReceiveBatch(_root, [FileEntry("too-large.mov", long.MaxValue)]));
        Assert.Empty(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task SameNamedMediaInOneBatchIsNumberedWithoutLosingEitherFile()
    {
        using var batch = new ReceiveBatch(_root, [FileEntry("IMG_001.jpg"),
            new NearbyFile("b", "IMG_001.jpg", 3, "image/jpeg"), new NearbyFile("c", "相册/same.png", 3, "image/png"),
            new NearbyFile("d", "相册/same.png", 3, "image/png")]);
        await batch.WriteAsync("a", new MemoryStream([1, 2, 3]), null, CancellationToken.None);
        await batch.WriteAsync("b", new MemoryStream([4, 5, 6]), null, CancellationToken.None);
        await batch.WriteAsync("c", new MemoryStream([7, 8, 9]), null, CancellationToken.None);
        await batch.WriteAsync("d", new MemoryStream([10, 11, 12]), null, CancellationToken.None);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(_root, "IMG_001.jpg")));
        Assert.Equal(new byte[] { 4, 5, 6 }, File.ReadAllBytes(Path.Combine(_root, "IMG_001 (2).jpg")));
        Assert.Equal(new byte[] { 7, 8, 9 }, File.ReadAllBytes(Path.Combine(_root, "相册", "same.png")));
        Assert.Equal(new byte[] { 10, 11, 12 }, File.ReadAllBytes(Path.Combine(_root, "相册", "same (2).png")));
    }
}
