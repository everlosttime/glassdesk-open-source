using GlassDesk.Platform.Storage;

namespace GlassDesk.AppServices.Tests;

public sealed class LibraryWriterLeaseTests
{
    [Fact]
    public void ASecondWriterCannotLoadAndOverwriteTheActiveHostData()
    {
        var root = Path.Combine(Path.GetTempPath(), "GlassDesk-writer-" + Guid.NewGuid().ToString("N"), "Data");
        try
        {
            Assert.True(LibraryWriterLease.TryAcquire(root, out var first));
            using (first)
            {
                Assert.False(LibraryWriterLease.TryAcquire(root.ToUpperInvariant(), out var second));
                Assert.Null(second);
            }
            Assert.True(LibraryWriterLease.TryAcquire(root, out var next));
            next!.Dispose();
        }
        finally { if (Directory.Exists(Path.GetDirectoryName(root))) Directory.Delete(Path.GetDirectoryName(root)!, true); }
    }
}
