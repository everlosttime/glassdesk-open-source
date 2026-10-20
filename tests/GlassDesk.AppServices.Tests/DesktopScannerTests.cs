using GlassDesk.Core.Contracts;
using GlassDesk.Core.Models;
using GlassDesk.Platform.Shell;

namespace GlassDesk.AppServices.Tests;

public sealed class DesktopScannerTests
{
    [Fact]
    public void CombinesBothDesktopsWithUserOverridesAndOmitsHiddenFilesWithoutRecursing()
    {
        using var workspace = new TempWorkspace();
        var user = workspace.CreateDirectory("用户桌面");
        var shared = workspace.CreateDirectory("公共桌面");
        workspace.CreateFile("用户桌面/相同.txt");
        workspace.CreateFile("公共桌面/相同.txt");
        workspace.CreateFile("公共桌面/公共.txt");
        var folder = workspace.CreateDirectory("用户桌面/资料");
        workspace.CreateFile("用户桌面/资料/不展开.txt");
        var hidden = workspace.CreateFile("用户桌面/隐藏.txt");
        File.SetAttributes(hidden, FileAttributes.Hidden);
        var system = workspace.CreateFile("公共桌面/desktop.ini");
        File.SetAttributes(system, FileAttributes.System);

        var scan = DesktopScanner.Scan(user, shared);

        Assert.Empty(scan.Warnings);
        Assert.Equal(3, scan.Items.Count);
        Assert.Contains(scan.Items, item => item.Path == Path.Combine(user, "相同.txt") && item.FromCurrentUser);
        Assert.Contains(scan.Items, item => item.Path == Path.Combine(shared, "公共.txt") && !item.FromCurrentUser);
        Assert.Contains(scan.Items, item => item.Path == Path.GetFullPath(folder) && item.IsDirectory);
    }

    [Fact]
    public void UsesLocalizedLabelsAndKeepsDifferentItemsWithTheSameLabel()
    {
        using var workspace = new TempWorkspace();
        var path = LocalizedImportNameTests.CreateLocalizedItem(workspace, "EnglishTool.lnk", "中文工具");
        var user = Path.GetDirectoryName(path)!;
        var other = Path.Combine(user, "OtherTool.lnk");
        Assert.True(ShortcutWriter.Write(other, new ShortcutDefinition(workspace.CreateProgram("另一个目标.cmd"))));
        var ini = Path.Combine(user, "desktop.ini");
        File.SetAttributes(ini, FileAttributes.Normal);
        File.AppendAllText(ini, "OtherTool.lnk=中文工具\r\n", System.Text.Encoding.Unicode);
        File.SetAttributes(ini, FileAttributes.Hidden | FileAttributes.System);

        var scan = DesktopScanner.Scan(user, null);

        Assert.Equal(2, scan.Items.Count);
        Assert.Equal("中文工具", scan.Items.Single(item => item.Path == path).DisplayName);
        Assert.Equal("中文工具", scan.Items.Single(item => item.Path == other).DisplayName);
        Assert.DoesNotContain(scan.Items, item => Path.GetFileName(item.Path) == "desktop.ini");
    }

    [Fact]
    public void MissingDesktopsAreEmptyAndTheSameRootIsNotEnumeratedTwice()
    {
        using var workspace = new TempWorkspace();
        var desktop = workspace.CreateDirectory("桌面");
        workspace.CreateFile("桌面/文件.txt");

        Assert.Empty(DesktopScanner.Scan(null, Path.Combine(workspace.Root, "missing")).Items);
        var item = Assert.Single(DesktopScanner.Scan(desktop, desktop).Items);
        Assert.True(item.FromCurrentUser);
    }

    [Fact]
    public void ImportingASelectionAddsAllGoodItemsAndPreservesExistingNamesWhenOneSourceDisappears()
    {
        using var workspace = new TempWorkspace();
        var desktop = workspace.CreateDirectory("桌面");
        var first = workspace.CreateFile("桌面/一.txt");
        workspace.CreateFile("桌面/二.txt");
        var removed = workspace.CreateFile("桌面/三.txt");
        var data = workspace.CreateStore();
        data.LoadAll();
        using var services = new AppServiceHost(data);
        var existing = Assert.Single(services.Import.ImportItems([new ImportRequest { Source = first, DisplayName = "我的自定义名称" }]).Added);
        var selected = DesktopScanner.Scan(desktop, null).Items;
        File.Delete(removed);

        var result = services.Import.ImportItems(selected.Select(item => new ImportRequest { Source = item.Path }).ToList());

        Assert.Single(result.Added);
        Assert.Equal(existing.Id, Assert.Single(result.Duplicates).Id);
        Assert.Equal(ImportFailureReason.NotFound, Assert.Single(result.Failures).Reason);
        Assert.Equal("我的自定义名称", services.Library.Find(existing.Id)!.DisplayName);
        Assert.Equal(2, LauncherLayoutRules.AppIds(data.Layouts.Launcher).Count());
    }
}
