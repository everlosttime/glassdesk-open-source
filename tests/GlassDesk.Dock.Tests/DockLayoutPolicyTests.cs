using System.Windows;
using System.Windows.Controls;
using GlassDesk.Dock;

namespace GlassDesk.Dock.Tests;

/// <summary>
/// 布局策略与排序候选的断言——对应复核报告缺陷 1：
/// 排序把内置入口写进了应用列表。
/// </summary>
public class DockLayoutPolicyTests
{
    private static readonly HashSet<string> Library = ["app-a", "app-b", "app-c", "app-d", "app-e"];

    [Fact]
    public void 内置入口不写进布局()
    {
        // 缺陷复现：排序把整行磁贴（含三个入口）原样写回，布局里混进了 entry:*。
        var sanitized = DockLayoutPolicy.Sanitize(
            ["app-a", "entry:launcher", "app-b", "entry:showdesktop", "entry:recyclebin"],
            Library.Contains);

        Assert.Equal(["app-a", "app-b"], sanitized);
    }

    [Fact]
    public void 保持原有应用顺序并去重()
    {
        var sanitized = DockLayoutPolicy.Sanitize(
            ["app-c", "app-a", "app-c", "app-b", "app-a"],
            Library.Contains);

        Assert.Equal(["app-c", "app-a", "app-b"], sanitized);
    }

    [Fact]
    public void 库里不存在的id被清掉()
    {
        // 用户从应用库删掉的条目、或手工编辑进去的陌生 id。
        var sanitized = DockLayoutPolicy.Sanitize(
            ["app-a", "已经删掉的条目id", "app-b"],
            Library.Contains);

        Assert.Equal(["app-a", "app-b"], sanitized);
    }

    [Fact]
    public void 全是入口时清成空列表()
    {
        var sanitized = DockLayoutPolicy.Sanitize(
            ["entry:launcher", "entry:showdesktop", "entry:recyclebin"],
            Library.Contains);

        Assert.Empty(sanitized);
    }

    [Fact]
    public void 入口前缀能被识别()
    {
        Assert.True(DockLayoutPolicy.IsEntryTile("entry:launcher"));
        Assert.True(DockLayoutPolicy.IsEntryTile("entry:recyclebin"));
        Assert.False(DockLayoutPolicy.IsEntryTile("entry"));
        Assert.False(DockLayoutPolicy.IsEntryTile("普通条目id"));
        Assert.False(DockLayoutPolicy.IsEntryTile(null));
    }

    [Fact]
    public void 顺序比较是顺序敏感的()
    {
        Assert.True(DockLayoutPolicy.SameOrder(["a", "b"], ["a", "b"]));
        Assert.False(DockLayoutPolicy.SameOrder(["a", "b"], ["b", "a"]));
        Assert.False(DockLayoutPolicy.SameOrder(["a"], ["a", "b"]));
        Assert.True(DockLayoutPolicy.SameOrder([], []));
    }
}

/// <summary>
/// 排序候选集合的断言：磁贴行里只有应用磁贴参与排序。
/// </summary>
public class DockTileStripReorderScopeTests
{
    private static DockTileModel App(string id) => new() { EntryId = id, DisplayName = id };

    private static DockTileModel Entry(string id) => new() { EntryId = id, DisplayName = id };

    [Fact]
    public void 排序候选只含应用磁贴()
    {
        StaTest.Run(() =>
        {
            var strip = new DockTileStrip();
            strip.SetMetrics(48, 8, 1.35, 8, 0);

            strip.SetTiles([
                App("app-a"), App("app-b"), App("app-c"),
                Entry("entry:launcher"), Entry("entry:showdesktop"), Entry("entry:recyclebin"),
            ]);

            Assert.Equal(["app-a", "app-b", "app-c"], strip.ReorderCandidateIds);
        });
    }

    [Fact]
    public void 没有应用磁贴时排序候选为空()
    {
        StaTest.Run(() =>
        {
            var strip = new DockTileStrip();
            strip.SetMetrics(48, 8, 1.35, 8, 0);

            strip.SetTiles([Entry("entry:launcher"), Entry("entry:showdesktop"), Entry("entry:recyclebin")]);

            Assert.Empty(strip.ReorderCandidateIds);
        });
    }

    [Fact]
    public void 只有应用磁贴时全部参与排序()
    {
        StaTest.Run(() =>
        {
            var strip = new DockTileStrip();
            strip.SetMetrics(48, 8, 1.35, 8, 0);

            strip.SetTiles([App("app-a"), App("app-b")]);

            Assert.Equal(["app-a", "app-b"], strip.ReorderCandidateIds);
        });
    }
}

/// <summary>
/// 多窗口列表的点击解析——对应复核报告缺陷 2：
/// 只读 <c>OriginalSource.Tag</c> 会什么都取不到。
/// </summary>
public class WindowChooserHitTests
{
    [Fact]
    public void 点在文字上也取得到所属列表项的句柄()
    {
        StaTest.Run(() =>
        {
            var list = new ListBox();
            var text = new TextBlock { Text = "窗口一" };
            var handle = new nint(0x1234);

            var item = new ListBoxItem { Content = text, Tag = handle };
            list.Items.Add(item);

            // 放进真实窗口并跑一遍布局，容器才会被生成（ContainerFromElement 依赖它）。
            var host = new Window
            {
                Width = 200,
                Height = 120,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                Content = list,
            };

            try
            {
                host.Show();
                host.UpdateLayout();

                // 用例前提：点击来源（里面的文字）自己没有 Tag——
                // 这正是老写法「只读 OriginalSource.Tag」取不到句柄的原因。
                Assert.Null(text.Tag);

                Assert.Equal(handle, WindowChooserHit.Resolve(list, text));
            }
            finally
            {
                host.Close();
            }
        });
    }

    [Fact]
    public void 点在其他行上取到的是那一行的句柄()
    {
        StaTest.Run(() =>
        {
            var list = new ListBox();
            var firstText = new TextBlock { Text = "窗口一" };
            var secondText = new TextBlock { Text = "窗口二" };
            var secondHandle = new nint(0x5678);

            list.Items.Add(new ListBoxItem { Content = firstText, Tag = new nint(0x1234) });
            list.Items.Add(new ListBoxItem { Content = secondText, Tag = secondHandle });

            var host = new Window
            {
                Width = 200,
                Height = 120,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                Content = list,
            };

            try
            {
                host.Show();
                host.UpdateLayout();

                Assert.Equal(secondHandle, WindowChooserHit.Resolve(list, secondText));
            }
            finally
            {
                host.Close();
            }
        });
    }

    [Fact]
    public void 取不到时返回零而不是抛出()
    {
        // WPF 元素必须在 STA 线程上创建（xunit 默认在线程池上跑用例）。
        StaTest.Run(() =>
        {
            var list = new ListBox();

            Assert.Equal(0, WindowChooserHit.Resolve(list, null));
            Assert.Equal(0, WindowChooserHit.Resolve(list, "不是可视元素"));
            Assert.Equal(0, WindowChooserHit.Resolve(list, new TextBlock()));
        });
    }
}