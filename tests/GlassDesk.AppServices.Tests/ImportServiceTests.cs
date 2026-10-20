using GlassDesk.Core.Contracts;
using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;
using GlassDesk.Platform.Shell;

namespace GlassDesk.AppServices.Tests;

/// <summary>
/// 端到端地导入四类条目与网页。
/// </summary>
/// <remarks>
/// 这些用例驱动<strong>真实的</strong> COM 与外壳 API，不做替换：阶段 3 最值钱的
/// 两个缺陷（<c>[GeneratedComInterface]</c> 少了 <c>[PreserveSig]</c> 导致返回值是垃圾、
/// <c>SHELLEXECUTEINFO</c> 多了一个字段导致启动被拒）都只在真东西上才会现形，
/// 换成假实现会全部通过。
/// </remarks>
public class ImportServiceTests
{
    [Fact]
    public void 导入文件夹()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var folder = workspace.CreateDirectory("我的 资料 文件夹");

        var result = services.Import.ImportItems([new ImportRequest { Source = folder }]);

        var entry = Assert.Single(result.Added);
        Assert.Equal(AppItemKind.Folder, entry.Kind);
        Assert.Equal(folder, entry.Target);
        Assert.Equal("我的 资料 文件夹", entry.DisplayName);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public void 导入文件()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var file = workspace.CreateFile("季度 报告 2026.txt");

        var result = services.Import.ImportItems([new ImportRequest { Source = file }]);

        var entry = Assert.Single(result.Added);
        Assert.Equal(AppItemKind.File, entry.Kind);

        // 文档保留扩展名：去掉之后「季度 报告 2026」认不出是什么。
        Assert.Equal("季度 报告 2026.txt", entry.DisplayName);
    }

    [Fact]
    public void 导入程序()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var program = workspace.CreateProgram("记事 本 模拟器.cmd");

        var result = services.Import.ImportItems([new ImportRequest { Source = program }]);

        var entry = Assert.Single(result.Added);
        Assert.Equal(AppItemKind.Program, entry.Kind);

        // 程序去掉扩展名。
        Assert.Equal("记事 本 模拟器", entry.DisplayName);
    }

    [Fact]
    public void 导入网址()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var result = services.Import.ImportItems(
            [new ImportRequest { Source = "https://example.net/路径", IsUrl = true }]);

        var entry = Assert.Single(result.Added);
        Assert.Equal(AppItemKind.Url, entry.Kind);
        Assert.Equal("https://example.net/路径", entry.Target);

        // 网页的默认名取主机名：整条地址在界面上显示不下。
        Assert.Equal("example.net", entry.DisplayName);
    }

    [Fact]
    public void 导入网页时会拒绝非网页协议()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var result = services.Import.ImportItems(
            [new ImportRequest { Source = "steam://run/1", IsUrl = true }]);

        Assert.Empty(result.Added);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(ImportFailureReason.UnsupportedType, failure.Reason);
    }

    [Fact]
    public void 导入带参数的快捷方式时参数与图标索引都保留()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var program = workspace.CreateProgram("目标 程序.cmd");
        var shortcut = Path.Combine(workspace.Root, "带 参数 的快捷方式.lnk");

        Assert.True(ShortcutWriter.Write(shortcut, new ShortcutDefinition(
            program,
            Arguments: "--flag \"带 空格 的值\"",
            WorkingDirectory: workspace.Root,
            IconLocation: @"C:\Windows\System32\shell32.dll,3")));

        var result = services.Import.ImportItems([new ImportRequest { Source = shortcut }]);

        var entry = Assert.Single(result.Added);
        Assert.Equal(AppItemKind.Program, entry.Kind);
        Assert.Equal(program, entry.Target);
        Assert.Equal("--flag \"带 空格 的值\"", entry.Arguments);
        Assert.Equal(workspace.Root, entry.WorkingDirectory);

        // 索引必须跟着图标位置一起保留：只留 shell32.dll 会取到第 0 个图标，
        // 完全不是用户在菜单上看到的那个。
        Assert.Equal(@"C:\Windows\System32\shell32.dll,3", entry.IconSource);
    }

    [Fact]
    public void 导入指向网址的快捷方式时识别为网页()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var shortcut = Path.Combine(workspace.Root, "网页 快捷方式.lnk");

        Assert.True(ShortcutWriter.Write(shortcut, new ShortcutDefinition("https://example.com/路径 带空格")));

        var result = services.Import.ImportItems([new ImportRequest { Source = shortcut }]);

        var entry = Assert.Single(result.Added);

        // 这一条曾经是错的：GetPath 对网址返回空串，于是它被当成「读不出目标」
        // 而降级成一条保留 .lnk 的快捷方式记录。
        Assert.Equal(AppItemKind.Url, entry.Kind);
        Assert.StartsWith("https://example.com/", entry.Target, StringComparison.Ordinal);
        Assert.NotEqual(shortcut, entry.Target);
    }

    [Fact]
    public void 导入不存在的路径时逐条报告而不是抛出()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var missing = Path.Combine(workspace.Root, "根本 不存在.txt");

        var result = services.Import.ImportItems([new ImportRequest { Source = missing }]);

        Assert.Empty(result.Added);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(ImportFailureReason.NotFound, failure.Reason);
        Assert.Contains("找不到", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 一批里有一个坏来源不影响其余的()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var good = workspace.CreateProgram("好的 程序.cmd");
        var missing = Path.Combine(workspace.Root, "坏的.txt");

        var result = services.Import.ImportItems(
        [
            new ImportRequest { Source = good },
            new ImportRequest { Source = missing },
        ]);

        Assert.Single(result.Added);
        Assert.Single(result.Failures);

        // 已经进库的那一条必须留下来：整批回滚会让用户丢掉本来能用的条目。
        Assert.NotNull(services.Library.Find(result.Added[0].Id));
    }

    [Fact]
    public void 同一个目标导入两次会被识别为重复()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var program = workspace.CreateProgram("程序.cmd");
        var request = new ImportRequest { Source = program };

        var first = services.Import.ImportItems([request]);
        Assert.Single(first.Added);

        var second = services.Import.ImportItems([request]);

        Assert.Empty(second.Added);
        var duplicate = Assert.Single(second.Duplicates);
        Assert.Equal(first.Added[0].Id, duplicate.Id);
    }

    [Fact]
    public void 大小写不同的同一路径算重复()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var program = workspace.CreateProgram("程序.cmd");

        services.Import.ImportItems([new ImportRequest { Source = program }]);

        var second = services.Import.ImportItems([new ImportRequest { Source = program.ToUpperInvariant() }]);

        Assert.Empty(second.Added);
        Assert.Single(second.Duplicates);
    }

    [Fact]
    public void 参数不同就不算重复()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var program = workspace.CreateProgram("程序.cmd");
        var shortcut = Path.Combine(workspace.Root, "带参数的.lnk");
        Assert.True(ShortcutWriter.Write(shortcut, new ShortcutDefinition(program, Arguments: "--second")));

        var direct = services.Import.ImportItems([new ImportRequest { Source = program }]);
        var withArguments = services.Import.ImportItems([new ImportRequest { Source = shortcut }]);

        // 同一个可执行文件配不同参数是两次不同的启动动作，合并会丢条目。
        Assert.Single(direct.Added);
        Assert.Single(withArguments.Added);
    }

    [Fact]
    public void 导入空文件伪装的快捷方式会报告读不出来()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var empty = Path.Combine(workspace.Root, "空的 快捷方式.lnk");
        File.WriteAllBytes(empty, []);

        var result = services.Import.ImportItems([new ImportRequest { Source = empty }]);

        Assert.Empty(result.Added);
        var failure = Assert.Single(result.Failures);

        // 空文件既不指向路径、也不指向任何外壳项，收下只会得到
        // 一条按下去没反应的记录。
        Assert.Equal(ImportFailureReason.ShortcutUnreadable, failure.Reason);
    }

    [Fact]
    public void 导入会保留库里原有的条目()
    {
        using var workspace = new TempWorkspace();
        var store = workspace.CreateStore();
        store.LoadAll();

        // 先手工放一条进去，模拟「库里本来就有东西」。
        var original = new AppEntry
        {
            Id = AppLibraryStore.NewId(),
            Kind = AppItemKind.Folder,
            Target = workspace.CreateDirectory("原有的 文件夹"),
            DisplayName = "原有的",
        };

        Assert.Equal(AddEntryResult.Added, store.Library.Add(original));
        store.Library.Save();

        using var services = new AppServiceHost(store);
        var missing = Path.Combine(workspace.Root, "不存在.txt");

        services.Import.ImportItems(
        [
            new ImportRequest { Source = workspace.CreateProgram("新的.cmd") },
            new ImportRequest { Source = missing },
        ]);

        var reloaded = new AppLibraryStore(workspace.DataPaths.AppLibraryFile);
        reloaded.Load();

        Assert.NotNull(reloaded.Find(original.Id));
        Assert.Equal(2, reloaded.Items.Count);
    }

    [Fact]
    public void 导入网页时不会因为网络不可用而失败()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        // 网址的导入只存字符串，不去访问网络。产品里没有任何网络调用，
        // 这条用例锁住这一点：即使地址根本不存在也能导入。
        var result = services.Import.ImportItems(
            [new ImportRequest { Source = "https://这个域名一定不存在.example/", IsUrl = true }]);

        Assert.Single(result.Added);
        Assert.Empty(result.Failures);
    }

    private static AppServiceHost Create(TempWorkspace workspace)
    {
        var store = workspace.CreateStore();
        store.LoadAll();
        return new AppServiceHost(store);
    }
}