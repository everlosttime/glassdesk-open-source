using System.Text;
using GlassDesk.Core.Storage;

namespace GlassDesk.Core.Tests;

public sealed class AtomicFileTests : IDisposable
{
    private readonly TempDataDirectory _temp = new();

    private string FilePath => Path.Combine(_temp.Root, "sample.json");

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void WriteAllText_CreatesFile_WithExactContents()
    {
        AtomicFile.WriteAllText(FilePath, """{"a":1}""");

        Assert.Equal("""{"a":1}""", File.ReadAllText(FilePath));
    }

    [Fact]
    public void WriteAllText_LeavesNoTempFileBehind()
    {
        AtomicFile.WriteAllText(FilePath, "first");
        AtomicFile.WriteAllText(FilePath, "second");

        Assert.False(File.Exists(FilePath + AtomicFile.TempSuffix));
    }

    [Fact]
    public void WriteAllText_SecondWrite_PreservesPreviousContentsAsBackup()
    {
        AtomicFile.WriteAllText(FilePath, "first");
        AtomicFile.WriteAllText(FilePath, "second");

        Assert.Equal("second", File.ReadAllText(FilePath));
        Assert.Equal("first", File.ReadAllText(FilePath + AtomicFile.BackupSuffix));
    }

    [Fact]
    public void WriteAllText_WritesWithoutByteOrderMark()
    {
        AtomicFile.WriteAllText(FilePath, """{"name":"程序坞"}""");

        var bytes = File.ReadAllBytes(FilePath);

        // BOM 会破坏朴素的 JSON 解析器，也不是 System.Text.Json 期望的形式。
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
    }

    [Fact]
    public void WriteAllText_RoundTripsChineseText()
    {
        const string Content = """{"name":"启动台与程序坞","path":"C:\\用户\\测试 目录"}""";

        AtomicFile.WriteAllText(FilePath, Content);

        Assert.Equal(Content, File.ReadAllText(FilePath, Encoding.UTF8));
    }

    [Fact]
    public void ReadAllTextOrNull_ReturnsNullForMissingFile()
    {
        Assert.Null(AtomicFile.ReadAllTextOrNull(Path.Combine(_temp.Root, "absent.json")));
    }

    [Fact]
    public void PreserveCorrupt_MovesFileAside_InsteadOfDeleting()
    {
        AtomicFile.WriteAllText(FilePath, "damaged");

        var preserved = AtomicFile.PreserveCorrupt(FilePath, DateTimeOffset.UtcNow);

        Assert.NotNull(preserved);
        Assert.False(File.Exists(FilePath));
        Assert.True(File.Exists(preserved));
        Assert.Equal("damaged", File.ReadAllText(preserved));
    }

    [Fact]
    public void PreserveCorrupt_KeepsBothCopies_WhenCalledTwiceInTheSameSecond()
    {
        AtomicFile.WriteAllText(FilePath, "first damage");
        var first = AtomicFile.PreserveCorrupt(FilePath, DateTimeOffset.UtcNow);

        AtomicFile.WriteAllText(FilePath, "second damage");
        var second = AtomicFile.PreserveCorrupt(FilePath, DateTimeOffset.UtcNow);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first, second);
        Assert.Equal("first damage", File.ReadAllText(first));
        Assert.Equal("second damage", File.ReadAllText(second));
    }
}