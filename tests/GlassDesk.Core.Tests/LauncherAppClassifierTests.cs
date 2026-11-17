using GlassDesk.Core.Models;

namespace GlassDesk.Core.Tests;

public sealed class LauncherAppClassifierTests
{
    [Theory]
    [InlineData("WINWORD.EXE", "programs")]
    [InlineData("WeChat.exe", "programs")]
    [InlineData("Steam.exe", "programs")]
    [InlineData("Code.exe", "programs")]
    [InlineData("Photoshop.exe", "programs")]
    [InlineData("PotPlayerMini64.exe", "programs")]
    [InlineData("msedge.exe", "programs")]
    [InlineData("7zFM.exe", "programs")]
    [InlineData("UnfamiliarTool.exe", "programs")]
    public void ExecutableTypesStayProgramsRegardlessOfSoftwarePurpose(string executable, string expected)
    {
        var input = new LauncherClassificationInput { DisplayName = "我刚下载的软件", Target = "C:\\Apps\\" + executable };
        Assert.Equal(expected, LauncherAppClassifier.Classify(input).CategoryId);
    }

    [Fact]
    public void DisplayNameAndProductMetadataDoNotOverrideTheActualFileType()
    {
        Assert.Equal("programs", LauncherAppClassifier.Classify(new()
        { DisplayName = "Chrome 浏览器", Target = @"C:\Apps\Code.exe", CompanyName = "Microsoft Corporation" }).CategoryId);
        Assert.Equal("word", LauncherAppClassifier.Classify(new()
        { DisplayName = "游戏安装包.exe", Target = @"C:\Desktop\季度报告.docx", ProductName = "Photoshop" }).CategoryId);
        Assert.Equal("uncategorized", LauncherAppClassifier.Classify(new()
        { DisplayName = "Chrome Visual Studio Code", Target = @"C:\Desktop\unknown.private", CompanyName = "Microsoft" }).CategoryId);
    }

    [Theory]
    [InlineData("steam://rungameid/123", "programs")]
    [InlineData("com.epicgames.launcher://apps/game?action=launch", "programs")]
    [InlineData("https://example.com/game", "websites")]
    [InlineData("http://example.com/report.pdf", "websites")]
    public void GameLaunchProtocolsAreProgramsAndWebAddressesAreWebsites(string target, string expected)
        => Assert.Equal(expected, LauncherAppClassifier.Classify(new() { Target = target }).CategoryId);

    [Fact]
    public void OpaquePackagedApplicationsAreProgramsAndUnknownMetadataDoesNotCreatePurposeGroups()
    {
        Assert.Equal("programs", LauncherAppClassifier.Classify(new()
        { Target = @"C:\Desktop\计算器.lnk", AppUserModelId = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App" }).CategoryId);
        Assert.Equal("uncategorized", LauncherAppClassifier.Classify(new()
        { Target = @"C:\Desktop\unknown", ProductName = "WPS Office" }).CategoryId);
    }

    [Theory]
    [InlineData("汇报.PPTX", "ppt")]
    [InlineData("演讲.pps", "ppt")]
    [InlineData("报告.doc", "word")]
    [InlineData("文档.docx", "word")]
    [InlineData("预算.xlsx", "excel")]
    [InlineData("数据.csv", "excel")]
    [InlineData("备忘录.txt", "text")]
    [InlineData("说明.PDF", "pdf")]
    [InlineData("照片.jpeg", "images")]
    [InlineData("图标.svg", "images")]
    [InlineData("歌曲.flac", "media")]
    [InlineData("录屏.mp4", "media")]
    [InlineData("资料.7z", "archives")]
    [InlineData("打包.tar.gz", "archives")]
    [InlineData("安装包.msi", "programs")]
    [InlineData("脚本.cmd", "programs")]
    [InlineData("未知文件.xyz", "uncategorized")]
    [InlineData("没有扩展名", "uncategorized")]
    public void FileExtensionDeterminesItsTypeGroup(string fileName, string expected)
        => Assert.Equal(expected, LauncherAppClassifier.Classify(new() { Target = @"C:\Desktop\" + fileName }).CategoryId);

    [Fact]
    public void MenuOffersOnlyFileTypesWhileLegacyCategoryIdentityRemainsReadable()
    {
        Assert.Equal(new[] { "folders", "ppt", "word", "excel", "text", "pdf", "images", "media", "archives", "programs", "websites", "uncategorized" },
            LauncherCategoryCatalog.All.Select(category => category.Id));
        Assert.Equal("其他", LauncherCategoryCatalog.Find("uncategorized")!.Name);
        Assert.Equal("开发工具", LauncherCategoryCatalog.Find("development")!.Name);
        Assert.DoesNotContain(LauncherCategoryCatalog.All, category => category.Id == "development");
    }

    [Fact]
    public void DirectoryKindWinsOverItsFilenameAndKnownDocumentExtensionWinsOverProgramFallback()
    {
        Assert.Equal("folders", LauncherAppClassifier.Classify(new()
        { Kind = AppItemKind.Folder, Target = @"C:\Desktop\资料.docx", DisplayName = "资料.docx" }).CategoryId);
        Assert.Equal("word", LauncherAppClassifier.Classify(new()
        { Kind = AppItemKind.Program, Target = @"C:\Desktop\暂时缺失.docx" }).CategoryId);
        Assert.Equal("programs", LauncherAppClassifier.Classify(new()
        { Kind = AppItemKind.Shortcut, Target = @"C:\Data\opaque" }).CategoryId);
        Assert.Equal("uncategorized", LauncherAppClassifier.Classify(new()
        { Kind = AppItemKind.File, Target = @"C:\Desktop\raw-file", DisplayName = "照片.png" }).CategoryId);
    }
}
