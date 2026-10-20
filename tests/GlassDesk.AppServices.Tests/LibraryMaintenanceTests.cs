using GlassDesk.Core.Contracts;
using GlassDesk.Core.Imaging;
using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;

namespace GlassDesk.AppServices.Tests;

/// <summary>
/// 失效目标的保留与重新定位（R-07）。
/// </summary>
/// <remarks>
/// R-07 要求路径失效时<strong>保留</strong>条目并允许重新定位，而不是让它消失。
/// 因此这里的用例重点不在「能不能检测到失效」，而在
/// 「检测到之后条目还在不在、布局对它的引用还在不在、用户能不能把它救回来」。
/// </remarks>
public class LibraryMaintenanceTests
{
    [Fact]
    public void 目标被删后条目保留并标记为失效()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var file = workspace.CreateFile("会消失 的文件.txt");
        var entry = ImportOne(services, file);

        File.Delete(file);

        var report = services.Maintenance.RefreshMissingTargets();

        Assert.Equal(1, report.Checked);
        Assert.Equal(1, report.Missing);
        Assert.True(report.Changed);

        // 关键是「保留」：条目必须还在，只是被标记。
        var after = services.Library.Find(entry.Id);
        Assert.NotNull(after);
        Assert.True(after.IsTargetMissing);
        Assert.Equal(file, after.Target);
    }

    [Fact]
    public void 失效标记会被写回文件()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var file = workspace.CreateFile("会消失 的文件.txt");
        var entry = ImportOne(services, file);
        File.Delete(file);

        services.Maintenance.RefreshMissingTargets();

        var reloaded = new AppLibraryStore(workspace.DataPaths.AppLibraryFile);
        reloaded.Load();

        // 只在内存里标记是不够的：重启之后条目又会显示成正常，
        // 用户点了才发现打不开。
        Assert.True(reloaded.Find(entry.Id)?.IsTargetMissing);
    }

    [Fact]
    public void 目标回来之后失效标记被取消()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var file = workspace.CreateFile("会消失 的文件.txt");
        var entry = ImportOne(services, file);

        File.Delete(file);
        services.Maintenance.RefreshMissingTargets();

        File.WriteAllText(file, "又回来了");

        var report = services.Maintenance.RefreshMissingTargets();

        Assert.Single(report.Recovered);
        Assert.False(services.Library.Find(entry.Id)?.IsTargetMissing);
    }

    [Fact]
    public void 状态没有变化时不写文件()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        ImportOne(services, workspace.CreateFile("文件.txt"));

        var first = services.Maintenance.RefreshMissingTargets();
        var second = services.Maintenance.RefreshMissingTargets();

        // 启动时无条件写一次会让文件的修改时间每次开机都变，
        // 也让「上次改动是什么时候」这个线索失去意义。
        Assert.False(first.Changed);
        Assert.False(second.Changed);
    }

    [Fact]
    public void 网址永远不算失效()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var result = services.Import.ImportItems(
            [new ImportRequest { Source = "https://example.com/", IsUrl = true }]);

        var entry = Assert.Single(result.Added);
        var report = services.Maintenance.RefreshMissingTargets();

        // 网址能不能打开取决于网络，不取决于本机文件。
        Assert.Equal(0, report.Missing);
        Assert.False(services.Library.Find(entry.Id)?.IsTargetMissing);
    }

    [Fact]
    public void 重新定位会同时改目标清标记并换图标来源()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var file = workspace.CreateFile("会消失 的文件.txt");
        var entry = ImportOne(services, file);
        File.Delete(file);
        services.Maintenance.RefreshMissingTargets();

        var replacement = workspace.CreateFile("替代 文件.txt");
        var outcome = services.Maintenance.Relocate(entry.Id, replacement);

        Assert.Equal(RelocateResult.Relocated, outcome.Result);

        var reloaded = new AppLibraryStore(workspace.DataPaths.AppLibraryFile);
        reloaded.Load();
        var after = reloaded.Find(entry.Id);

        Assert.Equal(replacement, after?.Target);
        Assert.False(after?.IsTargetMissing);

        // 图标来源必须跟着走。不改的话，界面上那条记录会带着旧图标
        // 继续显示为失效，用户以为没改成功。
        Assert.Equal(replacement, after?.IconSource);
    }

    [Fact]
    public void 重新定位到不存在的位置会被拒绝()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var entry = ImportOne(services, workspace.CreateFile("文件.txt"));

        var outcome = services.Maintenance.Relocate(entry.Id, Path.Combine(workspace.Root, "不存在.txt"));

        Assert.Equal(RelocateResult.TargetNotFound, outcome.Result);
        Assert.Equal(workspace.Root, Path.GetDirectoryName(services.Library.Find(entry.Id)?.Target));
    }

    [Fact]
    public void 重新定位到已被占用的位置会被拒绝且不改动任何东西()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var first = ImportOne(services, workspace.CreateFile("第一个.txt"));
        var second = ImportOne(services, workspace.CreateFile("第二个.txt"));

        var outcome = services.Maintenance.Relocate(second.Id, first.Target);

        // 强行改下去会让两条记录的去重键相同——应用库里从此有一对
        // 无法分辨的重复项，而这是阶段 2 花了 10 项测试去防的那类问题。
        Assert.Equal(RelocateResult.ConflictsWithExisting, outcome.Result);
        Assert.Equal(first.Id, outcome.ItemId);
        Assert.Equal(workspace.CreateFile("第二个.txt"), services.Library.Find(second.Id)?.Target);
    }

    [Fact]
    public void 网址不接受重新定位()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var result = services.Import.ImportItems(
            [new ImportRequest { Source = "https://example.com/", IsUrl = true }]);

        var entry = Assert.Single(result.Added);
        var outcome = services.Maintenance.Relocate(entry.Id, workspace.CreateFile("随便.txt"));

        Assert.Equal(RelocateResult.NotRelocatable, outcome.Result);
    }

    [Fact]
    public void 不存在的条目报告未知而不是抛出()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var outcome = services.Maintenance.Relocate("根本不存在的id", workspace.CreateFile("文件.txt"));

        Assert.Equal(RelocateResult.UnknownItem, outcome.Result);
    }

    [Fact]
    public void 重新定位到目录也能被识别()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var entry = ImportOne(services, workspace.CreateFile("文件.txt"));
        var folder = workspace.CreateDirectory("新的 文件夹");

        var outcome = services.Maintenance.Relocate(entry.Id, folder);

        Assert.Equal(RelocateResult.Relocated, outcome.Result);
        Assert.Equal(AppItemKind.Folder, services.Library.Find(entry.Id)?.Kind);
    }

    [Fact]
    public void 自定义图标被复制进缓存目录并挂到条目上()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var entry = ImportOne(services, workspace.CreateFile("文件.txt"));

        var userIcon = Path.Combine(workspace.Root, "我的 图标.png");
        File.WriteAllBytes(userIcon, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00]);

        Assert.True(services.Maintenance.SetCustomIcon(entry.Id, userIcon));

        var after = services.Library.Find(entry.Id);

        // 复制进来而不是只记外部路径：用户事后把源文件删掉或搬走，
        // 记着外部路径就等于图标丢了。
        Assert.NotNull(after?.CustomIconPath);
        Assert.NotEqual(userIcon, after.CustomIconPath);
        Assert.StartsWith(workspace.DataPaths.IconCacheDirectory, after.CustomIconPath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(after.CustomIconPath));

        // 条目上的图标来源保持原样，清掉缓存后还能重新提取。
        Assert.Equal(entry.Target, after.IconSource);
    }

    [Fact]
    public void 自定义图标格式不认识时被拒绝()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var entry = ImportOne(services, workspace.CreateFile("文件.txt"));
        var notAnImage = workspace.CreateFile("看着像图标.png", "%PDF-1.7 其实是 PDF");

        Assert.False(services.Maintenance.SetCustomIcon(entry.Id, notAnImage));
        Assert.Null(services.Library.Find(entry.Id)?.CustomIconPath);
    }

    [Fact]
    public void 撤掉自定义图标会连缓存文件一起清掉()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var entry = ImportOne(services, workspace.CreateFile("文件.txt"));
        var userIcon = Path.Combine(workspace.Root, "我的 图标.png");
        File.WriteAllBytes(userIcon, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00]);

        services.Maintenance.SetCustomIcon(entry.Id, userIcon);
        var cached = services.Library.Find(entry.Id)!.CustomIconPath!;

        Assert.True(services.Maintenance.SetCustomIcon(entry.Id, null));

        Assert.Null(services.Library.Find(entry.Id)?.CustomIconPath);
        Assert.False(File.Exists(cached));
    }

    [Fact]
    public void 重新定位之后旧位置可以再次被导入()
    {
        // 回归（阶段 3 复核）：A 改到 B 之后，_byKey 里仍留着 A 的键，
        // 于是再导入 A 得到「新增 0、重复 1」——一个合法的导入被拒了。
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var original = workspace.CreateFile("旧 位置.txt");
        var entry = ImportOne(services, original);
        var replacement = workspace.CreateFile("新 位置.txt");

        var relocated = services.Maintenance.Relocate(entry.Id, replacement);
        Assert.Equal(RelocateResult.Relocated, relocated.Result);

        // 旧位置现在空出来了，应当能作为一条新记录收进来。
        var again = services.Import.ImportItems([new ImportRequest { Source = original }]);

        Assert.Empty(again.Failures);
        Assert.Single(again.Added);
        Assert.Empty(again.Duplicates);
        Assert.Equal(original, again.Added[0].Target);
    }

    [Fact]
    public void 来回重新定位之后两个旧位置都能再导入()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var a = workspace.CreateFile("位置 甲.txt");
        var b = workspace.CreateFile("位置 乙.txt");
        var entry = ImportOne(services, a);

        Assert.Equal(RelocateResult.Relocated, services.Maintenance.Relocate(entry.Id, b).Result);
        Assert.Equal(RelocateResult.Relocated, services.Maintenance.Relocate(entry.Id, a).Result);

        var importB = services.Import.ImportItems([new ImportRequest { Source = b }]);

        Assert.Single(importB.Added);
        Assert.Empty(importB.Duplicates);
    }

    [Fact]
    public void 自定义图标在清空缓存之后仍然可用()
    {
        // 回归（阶段 3 复核）：自定义图标不是可再生缓存——它是从用户选的文件
        // 复制进来的，源文件可能早已不存在。清理缓存不能把它也删了。
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var entry = ImportOne(services, workspace.CreateFile("文件.txt"));
        var userIcon = Path.Combine(workspace.Root, "我的 图标.png");
        File.WriteAllBytes(userIcon, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00]);

        Assert.True(services.Maintenance.SetCustomIcon(entry.Id, userIcon));
        var cachedPath = services.Library.Find(entry.Id)!.CustomIconPath!;
        Assert.True(File.Exists(cachedPath));

        services.Icons.Clear();

        Assert.True(File.Exists(cachedPath), $"自定义图标被清掉了：{cachedPath}");
        Assert.Equal(cachedPath, services.Library.Find(entry.Id)!.CustomIconPath);
    }

    private static AppEntry ImportOne(AppServiceHost services, string source)
    {
        var result = services.Import.ImportItems([new ImportRequest { Source = source }]);
        return Assert.Single(result.Added);
    }

    private static AppServiceHost Create(TempWorkspace workspace)
    {
        var store = workspace.CreateStore();
        store.LoadAll();
        return new AppServiceHost(store);
    }

    [Fact]
    public void 重新定位被拒绝时不留下半改的条目()
    {
        // 回归：复核时发现的。目标从文件改成文件夹、更新被拒之后，
        // 旧版只还原了 Target / IconSource / IsTargetMissing，漏了 Kind，
        // 于是那条记录的类型仍是文件夹，指向的却还是原来的文件。
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var file = workspace.CreateFile("一个 文件.txt");
        var entry = ImportOne(services, file);
        var previousKind = entry.Kind;
        var previousTarget = entry.Target;
        var previousIconSource = entry.IconSource;

        // 先在库里放一条指向文件夹的记录，制造键冲突。
        var folder = ImportOne(services, workspace.CreateDirectory("一个 文件夹"));
        Assert.Equal(RelocateResult.ConflictsWithExisting, services.Maintenance.Relocate(entry.Id, folder.Target).Result);

        var after = services.Library.Find(entry.Id)!;

        Assert.Equal(previousTarget, after.Target);
        Assert.Equal(previousKind, after.Kind);
        Assert.Equal(previousIconSource, after.IconSource);
        Assert.False(after.IsTargetMissing);

        // 索引也必须完好：两个键仍各归各，谁也没被谁顶掉。
        Assert.NotNull(services.Library.FindByKey(GlassDesk.Core.Storage.AppEntryKey.Compute(new AppEntry
        {
            Kind = previousKind,
            Target = previousTarget,
        })));
        Assert.NotNull(services.Library.FindByKey(GlassDesk.Core.Storage.AppEntryKey.Compute(new AppEntry
        {
            Kind = folder.Kind,
            Target = folder.Target,
        })));
    }
}

/// <summary>图标缓存。</summary>
public class IconCacheTests
{
    [Fact]
    public void 提取出的图标被写成合法PNG()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        // 用系统里一定存在的可执行文件，这样不依赖外壳的关联设置。
        var cached = services.Icons.EnsureFor(@"C:\Windows\System32\notepad.exe");

        Assert.NotNull(cached);
        Assert.True(cached.CreatedFile);
        Assert.True(File.Exists(cached.Path));

        // 不只是「文件在」：开头必须是 PNG 签名，尺寸要从文件里读得出来。
        var header = File.ReadAllBytes(cached.Path).Take(8).ToArray();
        Assert.Equal([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], header);
        Assert.True(cached.Width > 0);
        Assert.True(cached.Height > 0);
    }

    [Fact]
    public void 同一个来源第二次取会复用已有文件()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var first = services.Icons.EnsureFor(@"C:\Windows\System32\notepad.exe");
        var second = services.Icons.EnsureFor(@"C:\Windows\System32\notepad.exe");

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.False(second.CreatedFile);
        Assert.Equal(first.Path, second.Path);
        Assert.Equal(1, services.Icons.FileCount);
    }

    [Fact]
    public void 不同来源落在不同文件上()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        services.Icons.EnsureFor(@"C:\Windows\System32\notepad.exe");
        services.Icons.EnsureFor(@"C:\Windows\System32\shell32.dll,3");

        Assert.Equal(2, services.Icons.FileCount);
    }

    [Fact]
    public void 清空缓存后条目上的图标来源仍然可用()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var entry = ImportOne(services, workspace.CreateFile("文件.txt"));
        Assert.NotNull(services.Library.Find(entry.Id)?.IconSource);

        var removed = services.Icons.Clear();

        Assert.True(removed.Removed > 0);
        Assert.Equal(0, services.Icons.FileCount);

        // 这就是为什么条目上存的是「来源」而不是缓存路径：
        // 缓存可以被清掉，来源不能。
        var again = services.Icons.EnsureFor(services.Library.Find(entry.Id)!.IconSource);
        Assert.NotNull(again);
    }

    [Fact]
public void 来源为空时返回空而不是抛出()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        Assert.Null(services.Icons.EnsureFor(null));
        Assert.Null(services.Icons.EnsureFor(""));
        Assert.Null(services.Icons.EnsureFor("   "));
    }

    [Fact]
    public void 目标不存在时退回按文件类型给图()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        // 这是有意的：条目失效之后仍然要有一张图可显示，而不是一片空白。
        // 外壳里的做法也是这个（SHGFI_USEFILEATTRIBUTES 就是为这件事存在的），
        // 「失效」由条目上的标记表达，不由缺图表达。
        var cached = services.Icons.EnsureFor(Path.Combine(workspace.Root, "不存在.exe"));

        Assert.NotNull(cached);
        Assert.True(cached.Width > 0);
    }

[Fact]
public void 各种畸形来源都返回结果而不抛出()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        // 取不到图标只是少一张图，绝不该变成异常——否则它会一路冒到导入那里
        // 变成一条导入失败。这里把几类畸形输入过一遍，确认调用面是稳的。
        string?[] hostile =
        [
            null,
            "",
            "   ",
            ",",
            ",5",
            @"不存在的盘符:\x.exe",
            @"C:\Windows\System32\shell32.dll,99999",
            Path.Combine(workspace.Root, "没有扩展名 也没有文件"),
            new string('x', 3000),
        ];

        foreach (var source in hostile)
        {
            var exception = Record.Exception(() => services.Icons.EnsureFor(source));

            Assert.Null(exception);
        }
    }

    [Fact]
    public void 图标按索引取时与同一文件的第零个图标不同()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var byIndex = services.Icons.EnsureFor(@"C:\Windows\System32\shell32.dll,3");
        var byDefault = services.Icons.EnsureFor(@"C:\Windows\System32\shell32.dll");

        Assert.NotNull(byIndex);
        Assert.NotNull(byDefault);

        // 内容必须真的不一样，否则「索引被保留」只是个说法。
        Assert.NotEqual(
            File.ReadAllBytes(byIndex.Path),
            File.ReadAllBytes(byDefault.Path));
    }

    [Fact]
    public void 缓存文件的尺寸与提取结果一致()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var cached = services.Icons.EnsureFor(@"C:\Windows\System32\notepad.exe");
        Assert.NotNull(cached);

        // 从 PNG 自己的 IHDR 里读尺寸，而不是从提取器的返回值里读——
        // 后者证明不了写下去的那几个字节是对的。
        var png = File.ReadAllBytes(cached.Path);
        var width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4));
        var height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4));

        Assert.Equal(cached.Width, width);
        Assert.Equal(cached.Height, height);
    }

    [Fact]
    public void 清理只删可再生缓存不碰自定义图标与无关文件()
    {
        // 回归（阶段 3 复核）：旧实现先 File.Delete 再判断「是不是我们的缓存」，
        // 于是用户放进目录里的无关文件也被删了；自定义图标同样被删。
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var entry = ImportOne(services, workspace.CreateFile("文件.txt"));
        Assert.NotNull(services.Icons.EnsureFor(entry.IconSource));

        // 自定义图标：从用户文件复制进来的，不可再生。
        var userIcon = Path.Combine(workspace.Root, "我的 图标.png");
        File.WriteAllBytes(userIcon, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00]);
        Assert.True(services.Maintenance.SetCustomIcon(entry.Id, userIcon));
        var customPath = services.Library.Find(entry.Id)!.CustomIconPath!;

        // 用户自己放进缓存目录的文件：不是我们的东西，不该动。
        var userNote = Path.Combine(workspace.DataPaths.IconCacheDirectory, "user-note.txt");
        File.WriteAllText(userNote, "这是用户自己放的文件");

        var result = services.Icons.Clear();

        Assert.Equal(1, result.Removed);
        Assert.True(File.Exists(customPath), "自定义图标被清掉了");
        Assert.True(File.Exists(userNote), "用户放进来的无关文件被删掉了");
    }

    [Fact]
    public void 清理之后可再生缓存能重新提取()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var cached = services.Icons.EnsureFor(@"C:\Windows\System32\notepad.exe");
        Assert.NotNull(cached);

        services.Icons.Clear();

        var again = services.Icons.EnsureFor(@"C:\Windows\System32\notepad.exe");
        Assert.NotNull(again);
        Assert.True(again.CreatedFile);
        Assert.Equal(cached.Path, again.Path);
    }

    [Fact]
    public void 清理时统计被留下的文件()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var directory = workspace.DataPaths.IconCacheDirectory;
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "README.txt"), "说明");
        File.WriteAllText(Path.Combine(directory, "a.txt"), "x");
        File.WriteAllText(Path.Combine(directory, "b.txt"), "y");

        var result = services.Icons.Clear();

        Assert.Equal(0, result.Removed);
        Assert.Equal(3, result.LeftAlone);
    }

    private static AppEntry ImportOne(AppServiceHost services, string source)
    {
        var result = services.Import.ImportItems([new ImportRequest { Source = source }]);
        return Assert.Single(result.Added);
    }

    private static AppServiceHost Create(TempWorkspace workspace)
    {
        var store = workspace.CreateStore();
        store.LoadAll();
        return new AppServiceHost(store);
    }
}