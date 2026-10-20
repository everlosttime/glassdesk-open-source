using GlassDesk.AppServices;
using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;
using GlassDesk.Platform.Shell;

namespace GlassDesk.AppServices.Tests;

/// <summary>
/// 窗口归属匹配规则的单元测试：对<strong>伪造的窗口快照</strong>做匹配，
/// 不创建任何真实窗口，因此跑得快也跑得稳。
/// 规则本身与真实窗口/进程身份的对应关系由阶段 4 探针实测背书
/// （见 <c>docs/evidence/阶段4-探针实测输出.txt</c>），
/// 真实端到端行为由 <c>--phase4-acceptance</c> 覆盖。
/// </summary>
public class WindowMatcherTests
{
    private static WindowIdentityInfo Window(
        nint handle,
        string? imagePath = null,
        string? windowAumid = null,
        string? processAumid = null,
        string title = "窗口")
        => new(handle, title, 1000 + (uint)handle, false, imagePath, windowAumid, processAumid);

    private static AppEntry ProgramEntry(string target, string? aumid = null) => new()
    {
        Id = "e1",
        Kind = AppItemKind.Program,
        Target = target,
        AppUserModelId = aumid,
    };

    [Fact]
    public void 经典程序按映像路径匹配_大小写不敏感()
    {
        var entry = ProgramEntry(@"C:\Windows\System32\CharMap.EXE");
        var snapshot = new[]
        {
            Window(1, imagePath: @"C:\WINDOWS\SYSTEM32\charmap.exe"),
            Window(2, imagePath: @"C:\Windows\explorer.exe"),
        };

        var matches = WindowMatcher.MatchWindows(entry, snapshot, aumid: null);

        var handle = Assert.Single(matches).Handle;
        Assert.Equal(1, handle);
    }

    [Fact]
    public void 打包应用按进程身份匹配()
    {
        // 探针实测：打包版记事本的窗口身份为空，身份在归属进程上。
        var entry = ProgramEntry(@"C:\Windows\System32\notepad.exe", "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App");
        var snapshot = new[]
        {
            Window(1, imagePath: @"C:\Program Files\WindowsApps\...\Notepad.exe",
                processAumid: "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App"),
        };

        var matches = WindowMatcher.MatchWindows(entry, snapshot, entry.AppUserModelId);

        var handle = Assert.Single(matches).Handle;
        Assert.Equal(1, handle);
    }

    [Fact]
    public void UWP框架窗口按窗口身份匹配()
    {
        // 探针实测：UWP 的框架窗口宿主在 ApplicationFrameHost 上，
        // 应用自己的身份挂在窗口属性存储里，宿主进程本身没有身份。
        var entry = new AppEntry
        {
            Id = "uwp",
            Kind = AppItemKind.Shortcut,
            Target = @"C:\某\设置.lnk",
            AppUserModelId = "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel",
        };

        var snapshot = new[]
        {
            Window(1, imagePath: @"C:\Windows\System32\ApplicationFrameHost.exe",
                windowAumid: "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel"),
        };

        var matches = WindowMatcher.MatchWindows(entry, snapshot, entry.AppUserModelId);

        var handle = Assert.Single(matches).Handle;
        Assert.Equal(1, handle);
    }

    [Fact]
    public void 身份不一致的窗口不算匹配_也不回退到映像路径()
    {
        // 规则来源：两个 UWP 应用共享同一个宿主进程，
        // 凭宿主进程路径强行匹配会把 A 应用的窗口错认成 B 应用的。
        var entry = ProgramEntry(@"C:\某\目标.exe", "com.app.a");
        var snapshot = new[]
        {
            Window(1, imagePath: @"C:\某\宿主.exe", windowAumid: "com.app.b"),
        };

        var matches = WindowMatcher.MatchWindows(entry, snapshot, entry.AppUserModelId);

        Assert.Empty(matches);
    }

    [Fact]
    public void 身份匹配不上时经典路径不受身份规则影响()
    {
        // 条目没有身份（普通 exe），窗口有别的身份或没有身份——
        // 都按映像路径判定，阶段 3 的行为保持不变。
        var entry = ProgramEntry(@"C:\tools\myapp.exe");
        var snapshot = new[]
        {
            Window(1, imagePath: @"C:\tools\myapp.exe", processAumid: "com.other.app"),
            Window(2, imagePath: @"C:\tools\myapp.exe"),
        };

        var matches = WindowMatcher.MatchWindows(entry, snapshot, aumid: null);

        Assert.Equal(2, matches.Count);
    }

    [Fact]
    public void 脚本目标不参与映像匹配()
    {
        // .bat 的真身是 cmd.exe，按脚本路径匹配永远匹配不上，
        // 与其每次都误判，不如老实启动（退化行为见 WindowMatcher 注释）。
        var entry = new AppEntry
        {
            Id = "bat",
            Kind = AppItemKind.Program,
            Target = @"C:\tools\run.bat",
        };
        var snapshot = new[]
        {
            Window(1, imagePath: @"C:\tools\run.bat"),
        };

        var matches = WindowMatcher.MatchWindows(entry, snapshot, aumid: null);

        Assert.Empty(matches);
    }

    [Fact]
    public void 目标仍是lnk且库存身份为空时按需现读身份()
    {
        // 这个用例只用「缓存路径」验证会话内缓存语义，不去碰真实的 .lnk：
        // 解析函数被调用一次后，第二次直接拿缓存值。
        var entry = new AppEntry
        {
            Id = "lnk",
            Kind = AppItemKind.Shortcut,
            Target = @"C:\不存在\某某.lnk",
        };
        var cache = new Dictionary<string, string?> { ["lnk"] = "com.cached.identity" };

        var resolved = WindowMatcher.ResolveAumid(entry, cache);

        Assert.Equal("com.cached.identity", resolved);
    }

    [Fact]
    public void 库里已存的身份优先且不再读文件()
    {
        var entry = ProgramEntry(@"C:\某\目标.exe", "com.stored.identity");

        var resolved = WindowMatcher.ResolveAumid(entry, identityCache: null);

        Assert.Equal("com.stored.identity", resolved);
    }
}
