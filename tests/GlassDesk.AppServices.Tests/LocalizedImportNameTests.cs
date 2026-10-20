using System.Text;
using GlassDesk.Core.Contracts;
using GlassDesk.Core.Models;
using GlassDesk.Platform.Shell;

namespace GlassDesk.AppServices.Tests;

public sealed class LocalizedImportNameTests
{
    [Theory]
    [InlineData("EnglishTool.lnk", "中文工具", AppItemKind.Program)]
    [InlineData("EnglishSite.url", "中文网站", AppItemKind.Url)]
    [InlineData("EnglishFolder", "中文资料 v2.0", AppItemKind.Folder)]
    [InlineData("EnglishProgram.cmd", "中文程序 v2.0", AppItemKind.Program)]
    public void ImportsUseTheShellLabelInsteadOfThePhysicalFileName(string fileName, string label, AppItemKind kind)
    {
        using var workspace = new TempWorkspace();
        var source = CreateLocalizedItem(workspace, fileName, label);

        var (entry, _, _) = AppEntryFactory.TryCreate(new ImportRequest { Source = source });

        Assert.NotNull(entry);
        Assert.Equal(label, entry.DisplayName);
        Assert.Equal(kind, entry.Kind);
    }

    [Fact]
    public void AnExplicitNameStillOverridesTheShellLabel()
    {
        using var workspace = new TempWorkspace();
        var source = CreateLocalizedItem(workspace, "EnglishTool.lnk", "中文工具");

        var (entry, _, _) = AppEntryFactory.TryCreate(new ImportRequest { Source = source, DisplayName = " 我的名称 " });

        Assert.Equal("我的名称", entry!.DisplayName);
    }

    internal static string CreateLocalizedItem(TempWorkspace workspace, string fileName, string label)
    {
        var root = workspace.CreateDirectory("本地化 " + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, fileName);
        switch (Path.GetExtension(fileName))
        {
            case ".lnk":
                var program = workspace.CreateProgram("实际目标.cmd");
                Assert.True(ShortcutWriter.Write(path, new ShortcutDefinition(program)));
                break;
            case ".url":
                File.WriteAllText(path, "[InternetShortcut]\r\nURL=https://example.net/\r\n");
                break;
            case ".cmd":
                File.WriteAllText(path, "@echo off\r\n");
                break;
            default:
                Directory.CreateDirectory(path);
                break;
        }
        var ini = Path.Combine(root, "desktop.ini");
        File.WriteAllText(ini, $"[LocalizedFileNames]\r\n{fileName}={label}\r\n", Encoding.Unicode);
        File.SetAttributes(ini, FileAttributes.Hidden | FileAttributes.System);
        File.SetAttributes(root, FileAttributes.Directory | FileAttributes.ReadOnly);
        return path;
    }
}
