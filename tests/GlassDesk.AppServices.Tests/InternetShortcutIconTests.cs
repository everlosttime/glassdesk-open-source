using GlassDesk.Core.Models;
using GlassDesk.Platform.Shell;

namespace GlassDesk.AppServices.Tests;

public sealed class InternetShortcutIconTests
{
    [Theory]
    [InlineData("steam://rungameid/123", AppItemKind.Shortcut)]
    [InlineData("https://example.com/", AppItemKind.Url)]
    [InlineData("custom-launcher://open/123", AppItemKind.File)]
    public void ImportPreservesExplicitInternetShortcutIconAcrossAllLaunchKinds(string target, AppItemKind expectedKind)
    {
        using var temp = new TempWorkspace();
        var icon = temp.CreateFile("真实游戏图标.ico");
        var shortcut = temp.CreateFile("游戏.url", $"[InternetShortcut]\nURL={target}\nIconFile={icon}\nIconIndex=0\n");

        var imported = AppEntryFactory.TryCreateFromPath(shortcut);

        Assert.NotNull(imported.Entry);
        Assert.Equal(expectedKind, imported.Entry.Kind);
        Assert.Equal(icon, imported.Entry.IconSource);
    }

    [Fact]
    public void ReadsOnlyInternetShortcutSectionAndResolvesQuotedRelativePath()
    {
        using var temp = new TempWorkspace();
        var icon = temp.CreateFile("图标,集合/中文 游戏.ico");
        var shortcut = temp.CreateFile("游戏.url", "[Other]\nIconFile=missing.ico\n[InternetShortcut]\n iconfile = \"图标,集合/中文 游戏.ico\"\n iconindex = 0\n[Another]\nIconFile=missing-again.ico\n");
        Assert.Equal(Path.GetFullPath(icon), InternetShortcutReader.ReadIconSource(shortcut));
    }

    [Fact]
    public void ExpandsEnvironmentVariablesAndPreservesExplicitNegativeResourceIndex()
    {
        using var temp = new TempWorkspace();
        var icon = temp.CreateFile("图标资源.dll");
        var variable = "GLASSDESK_ICON_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, temp.Root);
        try
        {
            var shortcut = temp.CreateFile("应用.url", $"[InternetShortcut]\nIconFile=\"%{variable}%/图标资源.dll\"\nIconIndex=-42\n");
            var (path, index) = IconExtractor.SplitIconSource(InternetShortcutReader.ReadIconSource(shortcut)!);
            Assert.Equal(icon, path);
            Assert.Equal(-42, index);
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    [Fact]
    public void ExplicitIndexOverridesEmbeddedIndexWithoutSplittingCommaDirectories()
    {
        using var temp = new TempWorkspace();
        var icon = temp.CreateFile("资源,文件夹/游戏资源.dll");
        var shortcut = temp.CreateFile("应用.url", "[InternetShortcut]\nIconFile=\"资源,文件夹/游戏资源.dll\",-3\nIconIndex=5\n");
        var (path, index) = IconExtractor.SplitIconSource(InternetShortcutReader.ReadIconSource(shortcut)!);
        Assert.Equal(Path.GetFullPath(icon), path);
        Assert.Equal(5, index);
    }

    [Fact]
    public void EmbeddedIndexIsAcceptedAndAnExistingCommaFilenameWinsOverIndexParsing()
    {
        using var temp = new TempWorkspace();
        var icon = temp.CreateFile("图标.ico");
        var shortcut = temp.CreateFile("应用.url", "[InternetShortcut]\nIconFile=\"图标.ico\",2\n");
        var (path, index) = IconExtractor.SplitIconSource(InternetShortcutReader.ReadIconSource(shortcut)!);
        Assert.Equal(icon, path);
        Assert.Equal(2, index);
        var commaFile = temp.CreateFile("图标.ico,3");
        File.WriteAllText(shortcut, "[InternetShortcut]\nIconFile=图标.ico,3\nIconIndex=0\n");
        Assert.Equal(commaFile, IconExtractor.SplitIconSource(InternetShortcutReader.ReadIconSource(shortcut)!).Path);
    }

    [Theory]
    [InlineData("[InternetShortcut]\nIconFile=missing.ico\n")]
    [InlineData("[InternetShortcut]\nIconFile=https://example.com/icon.ico\n")]
    [InlineData("[Other]\nIconFile=icon.ico\n")]
    [InlineData("this is not an Internet shortcut")]
    public void MissingRemoteOrUnrelatedIconsFallBackWithoutChangingTheWebsiteContract(string contents)
    {
        using var temp = new TempWorkspace();
        temp.CreateFile("icon.ico");
        var shortcut = temp.CreateFile("应用.url", contents);
        Assert.Null(InternetShortcutReader.ReadIconSource(shortcut));
        Assert.Null(InternetShortcutReader.ReadIconSource(Path.Combine(temp.Root, "missing.url")));
        Assert.False(InternetShortcutReader.IsSupportedUrl("steam://rungameid/123"));
    }

    [Fact]
    public void OversizedShortcutIsNotParsedAndRemainsAnOrdinaryFileWhenImported()
    {
        using var temp = new TempWorkspace();
        var icon = temp.CreateFile("icon.ico");
        var shortcut = temp.CreateFile("过大的.url", $"[InternetShortcut]\nURL=steam://rungameid/123\nIconFile={icon}\n"
            + new string(' ', 1024 * 1024));
        Assert.Null(InternetShortcutReader.ReadIconSource(shortcut));
        Assert.Null(InternetShortcutReader.ReadUrl(shortcut));
        var imported = AppEntryFactory.TryCreateFromPath(shortcut);
        Assert.Equal(AppItemKind.File, imported.Entry!.Kind);
        Assert.Equal(shortcut, imported.Entry.Target);
    }
}
