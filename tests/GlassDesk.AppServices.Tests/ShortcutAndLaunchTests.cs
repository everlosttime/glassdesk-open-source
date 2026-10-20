using GlassDesk.Core.Contracts;
using GlassDesk.Core.Models;
using GlassDesk.Platform.Shell;

namespace GlassDesk.AppServices.Tests;

/// <summary>解析 <c>.lnk</c>。</summary>
public class ShortcutResolverTests
{
    [Fact]
    public void 读出目标参数与工作目录()
    {
        using var workspace = new TempWorkspace();

        var program = workspace.CreateProgram("目标 程序.cmd");
        var shortcut = Path.Combine(workspace.Root, "快捷方式.lnk");

        Assert.True(ShortcutWriter.Write(shortcut, new ShortcutDefinition(
            program,
            Arguments: "--one --two \"带 空格\"",
            WorkingDirectory: workspace.Root)));

        var info = ShortcutResolver.Resolve(shortcut);

        Assert.NotNull(info);
        Assert.Equal(program, info.Target);
        Assert.Equal("--one --two \"带 空格\"", info.Arguments);
        Assert.Equal(workspace.Root, info.WorkingDirectory);
        Assert.True(info.HasResolvedTarget);
        Assert.False(info.TargetIsUrl);
    }

    [Fact]
    public void 图标位置带索引时索引被保留()
    {
        using var workspace = new TempWorkspace();
        var shortcut = Path.Combine(workspace.Root, "快捷方式.lnk");

        Assert.True(ShortcutWriter.Write(shortcut, new ShortcutDefinition(
            workspace.CreateProgram("程序.cmd"),
            IconLocation: @"C:\Windows\System32\shell32.dll,7")));

        var info = ShortcutResolver.Resolve(shortcut);

        Assert.Equal(@"C:\Windows\System32\shell32.dll,7", info?.IconSource);
    }

    [Fact]
    public void 没写图标位置时退回目标本身()
    {
        using var workspace = new TempWorkspace();
        var program = workspace.CreateProgram("程序.cmd");
        var shortcut = Path.Combine(workspace.Root, "快捷方式.lnk");

        Assert.True(ShortcutWriter.Write(shortcut, new ShortcutDefinition(program)));

        Assert.Equal(program, ShortcutResolver.Resolve(shortcut)?.IconSource);
    }

    [Fact]
    public void 指向网址的快捷方式从ID列表里读出网址()
    {
        using var workspace = new TempWorkspace();
        var shortcut = Path.Combine(workspace.Root, "网页.lnk");

        Assert.True(ShortcutWriter.Write(shortcut, new ShortcutDefinition("https://example.com/路径 带空格")));

        var info = ShortcutResolver.Resolve(shortcut);

        Assert.NotNull(info);

        // GetPath 对网址一定返回空串，四种标志都试过。补上 ID 列表这条路
        // 之后这里才有内容。
        Assert.True(info.TargetIsUrl, $"实际目标「{info.Target}」");
        Assert.StartsWith("https://example.com/", info.Target, StringComparison.Ordinal);
        Assert.True(info.HasShellItem);
    }

    [Fact]
    public void 空文件读不出来时返回空而不是抛出()
    {
        using var workspace = new TempWorkspace();
        var empty = Path.Combine(workspace.Root, "空的.lnk");
        File.WriteAllBytes(empty, []);

        Assert.Null(ShortcutResolver.Resolve(empty));
    }

    [Fact]
    public void 文件不存在时返回空而不是抛出()
    {
        using var workspace = new TempWorkspace();

        Assert.Null(ShortcutResolver.Resolve(Path.Combine(workspace.Root, "不存在.lnk")));
    }

    [Fact]
    public void 路径为空时返回空()
    {
        Assert.Null(ShortcutResolver.Resolve(string.Empty));
        Assert.Null(ShortcutResolver.Resolve("   "));
    }

    [Fact]
    public void 中文与空格路径完整往返()
    {
        using var workspace = new TempWorkspace();

        var program = workspace.CreateProgram(Path.Combine("程序 子目录", "记事 本 模拟器.cmd"));
        var folder = Path.Combine(workspace.Root, "快捷方式 子目录");
        Directory.CreateDirectory(folder);
        var shortcut = Path.Combine(folder, "带 空格与中文 的快捷方式.lnk");

        Assert.True(ShortcutWriter.Write(shortcut, new ShortcutDefinition(
            program,
            Arguments: "--值 \"带 空格 的值\"",
            WorkingDirectory: Path.GetDirectoryName(program)!)));

        var info = ShortcutResolver.Resolve(shortcut);

        Assert.NotNull(info);
        Assert.Equal(program, info.Target);
        Assert.Equal("--值 \"带 空格 的值\"", info.Arguments);
    }

    [Fact]
    public void 写一个快捷方式再读回来字段一致()
    {
        using var workspace = new TempWorkspace();

        var program = workspace.CreateProgram("往返 程序.cmd");
        var definition = new ShortcutDefinition(
            program,
            Arguments: "--a --b",
            WorkingDirectory: workspace.Root,
            IconLocation: @"C:\Windows\System32\shell32.dll,2",
            Description: "往返用例");

        var shortcut = Path.Combine(workspace.Root, "往返.lnk");
        Assert.True(ShortcutWriter.Write(shortcut, definition));

        var info = Assert.IsType<ShortcutInfo>(ShortcutResolver.Resolve(shortcut));

        Assert.Equal(definition.TargetPath, info.Target);
        Assert.Equal(definition.Arguments, info.Arguments);
        Assert.Equal(definition.WorkingDirectory, info.WorkingDirectory);
        Assert.Equal(definition.IconLocation, info.IconSource);
    }

    [Fact]
    public void 连续解析两百次不泄漏GDI与USER句柄()
    {
        using var workspace = new TempWorkspace();
        var shortcut = Path.Combine(workspace.Root, "循环.lnk");

        Assert.True(ShortcutWriter.Write(shortcut, new ShortcutDefinition(workspace.CreateProgram("程序.cmd"))));

        // 先热身一次，把首次加载 dll 之类的噪声排除掉。
        _ = ShortcutResolver.Resolve(shortcut);

        var before = CountGuiResources();

        for (var i = 0; i < 200; i++)
        {
            _ = ShortcutResolver.Resolve(shortcut);
        }

        var after = CountGuiResources();

        // 泄漏的表现是句柄数单调上涨。允许极小的波动（外壳内部会有缓存），
        // 但一次解析泄漏一个句柄的话这里会涨到两三百。
        Assert.True(after.Gdi - before.Gdi < 20, $"GDI 句柄从 {before.Gdi} 涨到 {after.Gdi}");
        Assert.True(after.User - before.User < 20, $"USER 句柄从 {before.User} 涨到 {after.User}");
    }

    private static (int Gdi, int User) CountGuiResources()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        process.Refresh();

        return (GetGuiResources(process.Handle, 0), GetGuiResources(process.Handle, 1));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetGuiResources(nint process, int flags);
}

/// <summary>启动条目。</summary>
public class LaunchServiceTests
{
    [Fact]
    public void 未知条目返回未知而不是抛出()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var result = services.Launch.LaunchItem("不存在的id");

        Assert.False(result.Succeeded);
        Assert.Equal(LaunchFailureReason.UnknownItem, result.Failure);
    }

    [Fact]
    public void 目标消失时报告目标不存在并给出可读说明()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var file = workspace.CreateFile("会消失.txt");
        var entry = ImportOne(services, file);
        File.Delete(file);

        var result = services.Launch.LaunchItem(entry.Id);

        Assert.False(result.Succeeded);
        Assert.Equal(LaunchFailureReason.TargetMissing, result.Failure);

        // 说明必须能让用户看懂并知道下一步做什么，而不是原始异常消息。
        Assert.NotNull(result.Message);
        Assert.Contains("重新定位", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 失效条目不影响别的条目启动()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var doomed = workspace.CreateFile("会消失.txt");
        var doomedEntry = ImportOne(services, doomed);
        File.Delete(doomed);

        var healthy = ImportOne(services, workspace.CreateFile("正常.txt"));

        var broken = services.Launch.LaunchItem(doomedEntry.Id);
        Assert.False(broken.Succeeded);

        // 真正要断言的是这一条：库里有坏条目不能连累好条目。
        //
        // 只核对**判定**，不真的打开：打开会拉起记事本/资源管理器，
        // 而「测试打开的窗口」是这一轮反复出问题的地方（残留窗口、目录被删后的
        // 「位置不可用」弹窗）。真实的打开动作由验收用 charmap 确定性地跑
        // （它总是自己拥有窗口、关掉即退出），以及人工核对清单覆盖。
        Assert.False(LaunchService.IsTargetGone(healthy));
        Assert.False(healthy.IsTargetMissing);
        Assert.True(ShellTargets.CanResolve(healthy.Target), "外壳解析不了这条正常记录的目标");
    }

    [Fact]
    public void 文件夹与文档的目标都能被外壳解析()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var folder = ImportOne(services, workspace.CreateDirectory("目录"));
        var document = ImportOne(services, workspace.CreateFile("文档.txt"));

        // 服务层的「外壳认不认这个目标」：沿用的正是验收里查 shell: 目标的那条路，
        // 零窗口、零弹窗。真实的打开由验收（charmap）与人工核对覆盖。
        Assert.True(ShellTargets.CanResolve(folder.Target), "外壳解析不了文件夹目标");
        Assert.True(ShellTargets.CanResolve(document.Target), "外壳解析不了文档目标");

        Assert.False(LaunchService.IsTargetGone(folder));
        Assert.False(LaunchService.IsTargetGone(document));
    }

    [Fact]
    public void 非程序条目不做窗口匹配()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        // 文件夹、文档与网址每次打开都可能落到不同的宿主进程，
        // 把它们的窗口当成「这个条目的窗口」会造成误判：
        // 用户点一下网页图标，结果被切到另一个已经开着的浏览器窗口。
        var folder = ImportOne(services, workspace.CreateDirectory("目录"));
        var document = ImportOne(services, workspace.CreateFile("文档.txt"));

        // 断言的是**匹配机制**：这两类条目不会进入窗口归属判定。
        // 不真的打开它们——打开文件夹会拉起资源管理器窗口，而测试打开的窗口
        // 正是这一轮反复出问题的地方（残留窗口、目录被删后的「位置不可用」弹窗）。
        // 「确实能打开」由验收用 charmap 确定性地跑，以及人工核对覆盖。
        Assert.False(WindowMatcher.CanMatchByImage(folder));
        Assert.False(WindowMatcher.CanMatchByImage(document));

        Assert.Empty(WindowMatcher.FindWindows(folder));
        Assert.Empty(WindowMatcher.FindWindows(document));
    }

    [Fact]
    public void 未知条目的激活请求报告失败()
    {
        using var workspace = new TempWorkspace();
        using var services = Create(workspace);

        var outcome = services.Launch.ActivateOrLaunch("不存在的id");

        Assert.Equal(ActivationOutcome.Failed, outcome.Outcome);
    }

    [Fact]
    public void 用零句柄激活窗口报告失败而不是抛出()
    {
        var outcome = LaunchService.ActivateWindow(0);

        Assert.Equal(ActivationOutcome.Failed, outcome.Outcome);
    }

    [Fact]
    public void 网址不算目标消失()
    {
        var entry = new AppEntry
        {
            Id = "x",
            Kind = AppItemKind.Url,
            Target = "https://这个域名一定不存在.example/",
        };

        Assert.False(LaunchService.IsTargetGone(entry));
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

/// <summary>找出属于某个程序的窗口并把它提到前台。</summary>
public class WindowLocatorTests
{
    /// <summary>
    /// 用「字符映射表」做靶子。
    /// </summary>
    /// <remarks>
    /// 挑它的理由是：它是少数仍然<strong>自己拥有窗口</strong>的经典 Win32 程序。
    /// 换成记事本就不行了——<c>System32\notepad.exe</c> 现在只是一个存根，
    /// 真正开窗口的是 WindowsApps 里的打包版本；控制台程序则由
    /// <c>WindowsTerminal.exe</c> 承载。那两类都拿不到匹配，见
    /// <see cref="WindowLocatorTests"/> 下方关于边界的说明。
    /// </remarks>
    private const string ClassicApp = @"C:\Windows\System32\charmap.exe";

    [Fact]
    public void 按映像路径找到属于该程序的窗口()
    {
        if (!File.Exists(ClassicApp))
        {
            // 换一台没有这个程序的机器时跳过，而不是给出一个假的失败。
            return;
        }

        // 用户桌面可能自己开着字符映射表。此时不能杀它（那是用户的窗口），
        // 也不能在同一份结论里混入它的状态——如实跳过这一条。
        if (WindowLocator.FindWindowsForImage(ClassicApp).Count > 0)
        {
            return;
        }

        uint spawnedProcessId = 0;

        try
        {
            var launch = ShellExecutor.Open(ClassicApp);
            Assert.True(launch.Started, $"外壳拒绝启动靶子程序，错误码 {launch.ErrorCode}");
            spawnedProcessId = launch.ProcessId;

            // 窗口不是立刻出现的：等的是外部的真实事件，所以轮询。
            var deadline = DateTime.UtcNow.AddSeconds(10);
            IReadOnlyList<WindowCandidateInfo> found = [];

            while (DateTime.UtcNow < deadline)
            {
                found = WindowLocator.FindWindowsForImage(ClassicApp);

                if (found.Count > 0)
                {
                    break;
                }

                Thread.Sleep(250);
            }

            Assert.NotEmpty(found);
            Assert.All(found, window => Assert.NotEqual(0, window.Handle));

            // 匹配到的窗口必须真的有标题：无标题窗口被排除是对的，
            // 否则「只有一个窗口」会变成「有多个窗口」，界面就会弹选择列表。
            Assert.All(found, window => Assert.False(string.IsNullOrWhiteSpace(window.Title)));
        }
        finally
        {
            // 只按记录的 pid 收尾——绝不按进程名（那会误伤用户的同名窗口）。
            KillRecordedProcess(spawnedProcessId);
        }
    }

    [Fact]
    public void 没有匹配窗口时返回空列表而不是抛出()
    {
        Assert.Empty(WindowLocator.FindWindowsForImage(@"C:\根本 不存在\不存在.exe"));
        Assert.Empty(WindowLocator.FindWindowsForImage(null));
        Assert.Empty(WindowLocator.FindWindowsForImage("   "));
        Assert.Empty(WindowLocator.FindWindowsForImage("只是一个名字"));
    }

    [Fact]
    public void 桌面窗口不算候选()
    {
        // Progman（桌面）可见、有标题、没有属主，但它带 WS_EX_TOOLWINDOW，
        // 因此必须被排除。不排除的话，资源管理器条目会匹配到桌面窗口，
        // 用户点一下程序坞就会被切到桌面。
        var desktop = WindowLocator.FindWindowsForImage(@"C:\Windows\explorer.exe");

        Assert.DoesNotContain(desktop, window => window.Title.Contains("Program Manager", StringComparison.Ordinal));
    }

    [Fact]
    public void 零句柄与无效句柄都安静失败()
    {
        Assert.False(WindowLocator.TryBringToForeground(0));
        Assert.False(WindowLocator.TryBringToForeground(new nint(0x1234)));
    }

    [Fact]
    public void 激活窗口的三种结局分得清()
    {
        // 多窗口列表里点一行之后要走的就是这条路径，三种结局对用户意味着不同的事：
        // 合成一句「失败了」用户不知道该做什么。这里逐条锁住分类与说明。
        var none = LaunchService.ActivateWindow(0);
        Assert.Equal(ActivationOutcome.Failed, none.Outcome);
        Assert.Contains("没有指定窗口", none.Message, StringComparison.Ordinal);

        // 列表是几秒前列的：窗口可能已经被用户关掉。这不是「系统拒绝」。
        var gone = LaunchService.ActivateWindow(new nint(0x1234));
        Assert.Equal(ActivationOutcome.Failed, gone.Outcome);
        Assert.Contains("已经关掉", gone.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("任务栏", gone.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 结束测试自己启动并记录的靶子进程。
    /// </summary>
    /// <remarks>
    /// <strong>绝不按进程名清理。</strong><c>GetProcessesByName</c> 会把这个名字下的
    /// <em>所有</em>进程一起杀掉，包括用户自己打开的字符映射表——一条测试不该
    /// 有这种副作用。只认启动时记录下来的 pid。
    /// </remarks>
    private static void KillRecordedProcess(uint processId)
    {
        if (processId == 0)
        {
            return;
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)processId);

            if (!process.HasExited)
            {
                process.Kill();
                process.WaitForExit(3000);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
            or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // 进程已经不在，或拿不到句柄——它本来就是测试拉起来的，退出即达目的。
        }
    }
}

/// <summary>扫描「开始」菜单。</summary>
public class StartMenuScannerTests
{
    [Fact]
    public void 扫描本机开始菜单并去重()
    {
        var scan = StartMenuScanner.Scan();

        // 这两个根目录在 Windows 上一定存在；缺一个说明
        // SHGetKnownFolderPath 取路径这一环出了问题。
        Assert.Equal(2, scan.Roots.Count);
        Assert.NotEmpty(scan.Shortcuts);

        // 去重只能让数量变少或不变，不可能变多。
        Assert.True(scan.Shortcuts.Count <= scan.ScannedFileCount,
            $"原始 {scan.ScannedFileCount}，去重后 {scan.Shortcuts.Count}");

        // 同一个分组里的同名项必须只剩一条。
        var keys = scan.Shortcuts
            .Select(shortcut => $"{shortcut.Group}\u0000{shortcut.Name}");

        Assert.Equal(scan.Shortcuts.Count, keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void 扫描结果里没有空名字()
    {
        var scan = StartMenuScanner.Scan();

        Assert.DoesNotContain(scan.Shortcuts, shortcut => string.IsNullOrWhiteSpace(shortcut.Name));
        Assert.DoesNotContain(scan.Shortcuts, shortcut => string.IsNullOrWhiteSpace(shortcut.Path));
    }

    [Fact]
    public void 扫描到的快捷方式都真实存在()
    {
        var scan = StartMenuScanner.Scan();

        // 扫描结果要能直接拿去导入，所以每条路径都得是真的。
        Assert.All(scan.Shortcuts, shortcut => Assert.True(File.Exists(shortcut.Path), shortcut.Path));
    }

    [Fact]
    public void 大多数开始菜单条目能解析出目标()
    {
        var scan = StartMenuScanner.Scan();
        var sample = scan.Shortcuts.Take(30).ToList();

        var resolved = sample.Count(shortcut => ShortcutResolver.Resolve(shortcut.Path) is not null);

        // 不要求 100%：UWP 与广告快捷方式本来就解不出文件路径，
        // 它们应当走「保留 .lnk」那条路。但绝大多数必须能解出来。
        Assert.True(resolved >= sample.Count * 8 / 10,
            $"{sample.Count} 条里只解析出 {resolved} 条");
    }
}