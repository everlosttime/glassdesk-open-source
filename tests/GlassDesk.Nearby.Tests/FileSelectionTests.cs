namespace GlassDesk.Nearby.Tests;

public sealed class FileSelectionTests
{
    [Fact]
    public void ExpandsMixedMediaAndRetainsFoldersWithoutEmptyDirectories()
    {
        var root = Path.Combine(Path.GetTempPath(), "GlassDesk-selection-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "相册", "空目录"));
            Directory.CreateDirectory(Path.Combine(root, "相册", "nested"));
            File.WriteAllText(Path.Combine(root, "相册", "nested", "😀.HEIC"), "abc");
            File.WriteAllText(Path.Combine(root, "movie.MOV"), "12345");
            var selection = FileSelection.Expand([Path.Combine(root, "相册"), Path.Combine(root, "movie.MOV")]);
            Assert.Equal(2, selection.Files.Count);
            Assert.Contains(selection.Files, f => f.Name == "相册/nested/😀.HEIC" && f.ContentType == "image/heic" && f.Size == 3);
            Assert.Contains(selection.Files, f => f.Name == "movie.MOV" && f.ContentType == "video/quicktime" && f.Size == 5);
            Assert.Single(selection.Warnings);
        }
        finally { Directory.Delete(root, true); }
    }
}
