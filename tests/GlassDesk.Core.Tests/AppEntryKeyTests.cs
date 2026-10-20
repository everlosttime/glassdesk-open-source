using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;

namespace GlassDesk.Core.Tests;

public sealed class AppEntryKeyTests
{
    private static AppEntry Entry(string target, AppItemKind kind = AppItemKind.Program, string? arguments = null) =>
        new()
        {
            Id = "x",
            DisplayName = "x",
            Kind = kind,
            Target = target,
            Arguments = arguments,
        };

    [Fact]
    public void Compute_IsCaseInsensitive_ForPaths()
    {
        // Windows 路径不区分大小写，因此重复导入同一程序必须能被发现，
        // 无论路径是怎样输入的。
        var a = AppEntryKey.Compute(Entry(@"C:\Windows\notepad.exe"));
        var b = AppEntryKey.Compute(Entry(@"c:\WINDOWS\NOTEPAD.EXE"));

        Assert.Equal(a, b);
    }

    [Fact]
    public void Compute_NormalizesForwardAndBackSlashes()
    {
        var a = AppEntryKey.Compute(Entry(@"C:\Tools\Editor\app.exe"));
        var b = AppEntryKey.Compute(Entry("C:/Tools/Editor/app.exe"));

        Assert.Equal(a, b);
    }

    [Fact]
    public void Compute_CollapsesRedundantSegments()
    {
        var a = AppEntryKey.Compute(Entry(@"C:\Tools\app.exe"));
        var b = AppEntryKey.Compute(Entry(@"C:\Tools\sub\..\app.exe"));

        Assert.Equal(a, b);
    }

    [Fact]
    public void Compute_IgnoresATrailingSeparator()
    {
        var a = AppEntryKey.Compute(Entry(@"C:\Tools", AppItemKind.Folder));
        var b = AppEntryKey.Compute(Entry(@"C:\Tools\", AppItemKind.Folder));

        Assert.Equal(a, b);
    }

    [Fact]
    public void Compute_DiffersWhenArgumentsDiffer()
    {
        var a = AppEntryKey.Compute(Entry(@"C:\Tools\app.exe", arguments: "--work"));
        var b = AppEntryKey.Compute(Entry(@"C:\Tools\app.exe", arguments: "--home"));

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Compute_TreatsAbsentAndEmptyArgumentsAlike()
    {
        var a = AppEntryKey.Compute(Entry(@"C:\Tools\app.exe"));
        var b = AppEntryKey.Compute(Entry(@"C:\Tools\app.exe", arguments: "   "));

        Assert.Equal(a, b);
    }

    [Fact]
    public void Compute_FoldsUrlSchemeAndHostButKeepsPathCasing()
    {
        var a = AppEntryKey.Compute(Entry("HTTPS://Example.COM/Docs/Page", AppItemKind.Url));
        var b = AppEntryKey.Compute(Entry("https://example.com/Docs/Page", AppItemKind.Url));

        Assert.Equal(a, b);
    }

    [Fact]
    public void Compute_DistinguishesUrlsByPort()
    {
        // 回归（阶段 3 复核）：键里只有协议与主机，端口被丢掉了，
        // 于是 localhost:8001 与 localhost:8002 这两个不同服务被当成同一条。
        var a = AppEntryKey.Compute(Entry("http://localhost:8001/app", AppItemKind.Url));
        var b = AppEntryKey.Compute(Entry("http://localhost:8002/app", AppItemKind.Url));

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Compute_TreatsAnExplicitDefaultPortAsOmitted()
    {
        // 省略端口与显式写出默认端口是同一个地址，不该被当成两条。
        var a = AppEntryKey.Compute(Entry("https://example.com/", AppItemKind.Url));
        var b = AppEntryKey.Compute(Entry("https://example.com:443/", AppItemKind.Url));

        Assert.Equal(a, b);
    }

    [Fact]
    public void Compute_TreatsAnExplicitHttpDefaultPortAsOmitted()
    {
        var a = AppEntryKey.Compute(Entry("http://example.com/", AppItemKind.Url));
        var b = AppEntryKey.Compute(Entry("http://example.com:80/", AppItemKind.Url));

        Assert.Equal(a, b);
    }

    [Fact]
    public void Compute_KeepsUrlPathsDistinctByCase()
    {
        // 与 Windows 文件路径不同，URL 路径区分大小写。
        var a = AppEntryKey.Compute(Entry("https://example.com/docs", AppItemKind.Url));
        var b = AppEntryKey.Compute(Entry("https://example.com/Docs", AppItemKind.Url));

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Compute_DoesNotThrow_ForAnUnusableTarget()
    {
        var key = AppEntryKey.Compute(Entry("\0invalid\0"));

        Assert.NotNull(key);
    }

    [Fact]
    public void Compute_IsEmpty_ForAnEmptyTarget()
    {
        Assert.Equal(string.Empty, AppEntryKey.Compute(Entry(string.Empty)));
    }
}