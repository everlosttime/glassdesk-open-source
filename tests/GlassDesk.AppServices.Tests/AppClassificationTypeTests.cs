using GlassDesk.Core.Models;
using GlassDesk.Platform.Shell;

namespace GlassDesk.AppServices.Tests;

public sealed class AppClassificationTypeTests
{
    [Fact]
    public void FolderWithDocumentLookingNameStaysFolder()
        => Assert.Equal("folders", AppClassificationService.Classify(new()
        { Kind = AppItemKind.Folder, Target = @"C:\Desktop\报告.docx", DisplayName = "报告.docx" }).CategoryId);

    [Fact]
    public void ShortcutUsesItsDocumentTargetTypeRatherThanTheCustomizedShortcutName()
    {
        using var temp = new TempWorkspace();
        var document = temp.CreateFile("实际报告.docx");
        var link = Path.Combine(temp.Root, "Steam游戏快捷方式.lnk");
        Assert.True(ShortcutWriter.Write(link, new(document)));
        Assert.Equal("word", AppClassificationService.Classify(new()
        { Kind = AppItemKind.Shortcut, Target = link, DisplayName = "Steam 游戏" }).CategoryId);
    }

    [Fact]
    public void FolderShortcutAndUrlEntriesKeepTheirActualKinds()
    {
        using var temp = new TempWorkspace();
        var folder = temp.CreateDirectory("素材.png");
        var link = Path.Combine(temp.Root, "照片.lnk");
        Assert.True(ShortcutWriter.Write(link, new(folder)));
        Assert.Equal("folders", AppClassificationService.Classify(new()
        { Kind = AppItemKind.Shortcut, Target = link }).CategoryId);
        Assert.Equal("websites", AppClassificationService.Classify(new()
        { Kind = AppItemKind.Url, Target = "https://example.com/doc.pdf" }).CategoryId);
        Assert.Equal("programs", AppClassificationService.Classify(new()
        { Kind = AppItemKind.Shortcut, Target = @"C:\Data\game.url", AutomaticSourceKey = "protocol:steam://rungameid/123" }).CategoryId);
    }
}
