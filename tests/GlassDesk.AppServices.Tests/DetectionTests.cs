using GlassDesk.Core.Models;
using GlassDesk.Platform.Shell;

namespace GlassDesk.AppServices.Tests;

/// <summary>按路径判断条目种类与显示名。</summary>
public class ItemKindDetectorTests
{
    [Theory]
    [InlineData("app.exe", AppItemKind.Program)]
    [InlineData("app.EXE", AppItemKind.Program)]
    [InlineData("脚本.cmd", AppItemKind.Program)]
    [InlineData("脚本.bat", AppItemKind.Program)]
    [InlineData("快捷.lnk", AppItemKind.Shortcut)]
    [InlineData("网页.url", AppItemKind.Url)]
    [InlineData("报告.docx", AppItemKind.File)]
    [InlineData("没有扩展名", AppItemKind.File)]
    public void 按扩展名判断种类(string fileName, AppItemKind expected)
        => Assert.Equal(expected, ItemKindDetector.Detect(fileName, isDirectory: false));

    [Fact]
    public void 目录优先于扩展名()
    {
        // 一个叫「工具.exe」的文件夹仍然是文件夹——先看扩展名会把它判成程序，
        // 于是启动时去执行一个目录，得到的是一片迷惑。
        Assert.Equal(AppItemKind.Folder, ItemKindDetector.Detect(@"D:\工具.exe", isDirectory: true));
    }

    [Fact]
    public void 安装包不算程序()
    {
        // .msi 启动的是安装程序，把它标成程序会让「已运行就激活」
        // 去匹配安装进程。
        Assert.Equal(AppItemKind.File, ItemKindDetector.Detect("setup.msi", isDirectory: false));
    }

    [Theory]
    [InlineData("notepad.exe", "notepad")]
    [InlineData("记事 本.cmd", "记事 本")]
    [InlineData("带 空格 的快捷方式.lnk", "带 空格 的快捷方式")]
    public void 程序与快捷方式去掉扩展名(string path, string expected)
        => Assert.Equal(expected, ItemKindDetector.DeriveDisplayName(path, ItemKindDetector.Detect(path, false)));

    [Theory]
    [InlineData("季度 报告 2026.txt")]
    [InlineData("归档.zip")]
    public void 文档保留扩展名(string path)
        => Assert.Equal(Path.GetFileName(path), ItemKindDetector.DeriveDisplayName(path, AppItemKind.File));

    [Fact]
    public void 文件夹名取最后一段()
    {
        Assert.Equal("我的 资料", ItemKindDetector.DeriveDisplayName(@"D:\a\我的 资料", AppItemKind.Folder));
    }
}

/// <summary>读 <c>.url</c> 文件（Internet 快捷方式）。</summary>
public class InternetShortcutReaderTests
{
    [Fact]
    public void 读出URL那一行()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.CreateFile(
            "网页.url",
            "[InternetShortcut]" + Environment.NewLine + "URL=https://example.org/a b" + Environment.NewLine);

        Assert.Equal("https://example.org/a b", InternetShortcutReader.ReadUrl(path));
    }

    [Fact]
    public void 忽略注释与空行()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.CreateFile(
            "网页.url",
            "; 这是注释" + Environment.NewLine
            + Environment.NewLine
            + "[InternetShortcut]" + Environment.NewLine
            + "IconFile=whatever.ico" + Environment.NewLine
            + "URL=http://example.net/" + Environment.NewLine);

        Assert.Equal("http://example.net/", InternetShortcutReader.ReadUrl(path));
    }

    [Fact]
    public void 大小写不敏感()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.CreateFile("网页.url", "url=https://example.com/" + Environment.NewLine);

        Assert.Equal("https://example.com/", InternetShortcutReader.ReadUrl(path));
    }

    [Fact]
    public void 没有URL行时返回空()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.CreateFile("残缺.url", "这不是一个 Internet 快捷方式" + Environment.NewLine);

        Assert.Null(InternetShortcutReader.ReadUrl(path));
    }

    [Fact]
    public void 文件不存在时返回空而不是抛出()
    {
        using var workspace = new TempWorkspace();

        Assert.Null(InternetShortcutReader.ReadUrl(Path.Combine(workspace.Root, "不存在.url")));
    }

    [Theory]
    [InlineData("https://example.com/", true)]
    [InlineData("http://example.com/", true)]
    [InlineData("file:///C:/x.txt", false)]
    [InlineData("mailto:a@b.c", false)]
    [InlineData("steam://run/1", false)]
    [InlineData("example.com", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void 只认网页协议(string? value, bool expected)
        => Assert.Equal(expected, InternetShortcutReader.IsSupportedUrl(value));

    [Fact]
    public void 写出来的内容能被自己读回来()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.CreateFile("往返.url", InternetShortcutReader.Compose("https://example.com/路径 带空格"));

        Assert.Equal("https://example.com/路径 带空格", InternetShortcutReader.ReadUrl(path));
    }
}

/// <summary>按文件头判断图片格式。</summary>
public class ImageFormatSnifferTests
{
    [Theory]
    [InlineData("a.png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, ImageFormat.Png)]
    [InlineData("a.jpg", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, ImageFormat.Jpeg)]
    [InlineData("a.bmp", new byte[] { 0x42, 0x4D, 0x00, 0x00 }, ImageFormat.Bmp)]
    [InlineData("a.gif", new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }, ImageFormat.Gif)]
    [InlineData("a.ico", new byte[] { 0x00, 0x00, 0x01, 0x00, 0x01, 0x00 }, ImageFormat.Ico)]
    public void 按内容认出格式(string name, byte[] content, ImageFormat expected)
    {
        using var workspace = new TempWorkspace();
        var path = Path.Combine(workspace.Root, name);
        File.WriteAllBytes(path, content);

        Assert.Equal(expected, ImageFormatSniffer.Detect(path));
    }

    [Fact]
    public void 看内容而不是扩展名()
    {
        // 用户完全可能把一个 PNG 存成 .jpg。按扩展名判断会挑错解码器。
        using var workspace = new TempWorkspace();
        var path = Path.Combine(workspace.Root, "其实是png.jpg");
        File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        Assert.Equal(ImageFormat.Png, ImageFormatSniffer.Detect(path));
    }

    [Fact]
    public void 认不出来的内容返回空()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.CreateFile("文档.pdf", "%PDF-1.7 这是一个 PDF");

        Assert.Null(ImageFormatSniffer.Detect(path));
    }

    [Fact]
    public void 空文件返回空()
    {
        using var workspace = new TempWorkspace();
        var path = Path.Combine(workspace.Root, "空的.png");
        File.WriteAllBytes(path, []);

        Assert.Null(ImageFormatSniffer.Detect(path));
    }

    [Fact]
    public void 文件不存在时返回空而不是抛出()
    {
        using var workspace = new TempWorkspace();

        Assert.Null(ImageFormatSniffer.Detect(Path.Combine(workspace.Root, "不存在.png")));
    }

    [Theory]
    [InlineData(ImageFormat.Png, ".png")]
    [InlineData(ImageFormat.Jpeg, ".jpg")]
    [InlineData(ImageFormat.Ico, ".ico")]
    public void 扩展名与格式一致(ImageFormat format, string expected)
        => Assert.Equal(expected, ImageFormatSniffer.FileExtensionFor(format));
}

/// <summary>拆分「路径,索引」形式的图标位置。</summary>
public class IconSourceSplitTests
{
    [Theory]
    [InlineData(@"C:\Windows\System32\shell32.dll,3", @"C:\Windows\System32\shell32.dll", 3)]
    [InlineData(@"C:\Windows\System32\shell32.dll,-1", @"C:\Windows\System32\shell32.dll", -1)]
    [InlineData(@"C:\app.exe,0", @"C:\app.exe", 0)]
    public void 把路径与索引拆开(string source, string expectedPath, int expectedIndex)
    {
        var (path, index) = IconExtractor.SplitIconSource(source);

        Assert.Equal(expectedPath, path);
        Assert.Equal(expectedIndex, index);
    }

    [Fact]
    public void 目录名里的逗号不会被当成索引分隔符()
    {
        // D:\a,b\app.exe 是合法路径。从头找逗号会把它拆成
        // 「D:\a」和「b\app.exe」，于是图标来源整个错掉。
        var (path, index) = IconExtractor.SplitIconSource(@"D:\a,b\app.exe");

        Assert.Equal(@"D:\a,b\app.exe", path);
        Assert.Null(index);
    }

    [Fact]
    public void 逗号后面不是整数时不当索引()
    {
        var (path, index) = IconExtractor.SplitIconSource(@"D:\资料,备份\app.exe");

        Assert.Equal(@"D:\资料,备份\app.exe", path);
        Assert.Null(index);
    }

    [Fact]
    public void 没有索引时原样返回()
    {
        var (path, index) = IconExtractor.SplitIconSource(@"C:\Windows\notepad.exe");

        Assert.Equal(@"C:\Windows\notepad.exe", path);
        Assert.Null(index);
    }

    [Fact]
    public void 末尾逗号不算索引()
    {
        var (path, index) = IconExtractor.SplitIconSource(@"C:\app.exe,");

        Assert.Equal(@"C:\app.exe,", path);
        Assert.Null(index);
    }

    [Fact]
    public void 前后空白被去掉()
    {
        var (path, index) = IconExtractor.SplitIconSource(@"  C:\app.exe, 5  ");

        Assert.Equal(@"C:\app.exe", path);
        Assert.Equal(5, index);
    }
}