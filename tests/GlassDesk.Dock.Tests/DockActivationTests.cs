using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using GlassDesk.AppServices;
using GlassDesk.Core.Contracts;
using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;
using GlassDesk.Dock;
using GlassDesk.Platform;
using GlassDesk.Platform.Shell;
using GlassDesk.UI.Settings;

namespace GlassDesk.Dock.Tests;

public class DockActivationTests
{
    [Theory]
    [InlineData(WindowState.Normal)]
    [InlineData(WindowState.Maximized)]
    public void 点击前台应用最小化_再点恢复原窗口状态(WindowState initialState)
    {
        StaTest.Run(() => WithWindows((dock, host, entry, target) =>
        {
            target.WindowState = initialState;
            var handle = new WindowInteropHelper(target).Handle;
            Assert.True(WindowLocator.TryBringToForeground(handle));
            Assert.Equal(handle, WindowControl.GetForegroundWindow());

            ClickTile(dock, entry.Id);

            Assert.True(WindowControl.IsMinimized(handle), "再次点击前台应用的 Dock 图标应最小化窗口");
            ClickTile(dock, entry.Id);

            Assert.False(WindowControl.IsMinimized(handle));
            Assert.Equal(handle, WindowControl.GetForegroundWindow());
            Assert.Equal(initialState, target.WindowState);
        }));
    }

    [Fact]
    public void 点击后台应用只前置_不会最小化()
    {
        StaTest.Run(() => WithWindows((dock, host, entry, target) =>
        {
            var other = new Window { Title = "Owned foreground", Owner = target, Width = 160, Height = 100 };
            try
            {
                other.Show();
                var handle = new WindowInteropHelper(target).Handle;
                Assert.True(WindowLocator.TryBringToForeground(new WindowInteropHelper(other).Handle));
                Assert.NotEqual(handle, WindowControl.GetForegroundWindow());

                ClickTile(dock, entry.Id);

                Assert.False(WindowControl.IsMinimized(handle));
                Assert.Equal(handle, WindowControl.GetForegroundWindow());
            }
            finally { other.Close(); }
        }));
    }

    [Fact]
    public void 通用启动入口重复激活前台窗口_不会最小化()
    {
        StaTest.Run(() => WithWindows((dock, host, entry, target) =>
        {
            var handle = new WindowInteropHelper(target).Handle;
            Assert.True(WindowLocator.TryBringToForeground(handle));

            Assert.Equal(ActivationOutcome.Activated, host.Launch.ActivateOrLaunch(entry.Id).Outcome);

            Assert.False(WindowControl.IsMinimized(handle));
            Assert.Equal(handle, WindowControl.GetForegroundWindow());
        }));
    }

    [Fact]
    public void 右键菜单打开前台应用_不会最小化()
    {
        StaTest.Run(() => WithWindows((dock, host, entry, target) =>
        {
            var handle = new WindowInteropHelper(target).Handle;
            Assert.True(WindowLocator.TryBringToForeground(handle));
            typeof(DockWindow).GetMethod("OpenEntry", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(dock, [entry.Id, false]);
            Assert.False(WindowControl.IsMinimized(handle));
            Assert.Equal(handle, WindowControl.GetForegroundWindow());
        }));
    }

    [Fact]
    public void 同应用多个窗口仍然显示选择列表()
    {
        StaTest.Run(() => WithWindows((dock, host, entry, target) =>
        {
            var second = new Window { Title = "Second app window", Width = 160, Height = 100 };
            try
            {
                second.Show();
                ClickTile(dock, entry.Id);

                Assert.False(WindowControl.IsMinimized(new WindowInteropHelper(target).Handle));
                Assert.False(WindowControl.IsMinimized(new WindowInteropHelper(second).Handle));
                Assert.Equal(ActivationOutcome.MultipleWindowsFound, host.Launch.ActivateOrLaunch(entry.Id).Outcome);
            }
            finally { second.Close(); }
        }));
    }

    private static void ClickTile(DockWindow dock, string entryId)
    {
        typeof(DockWindow).GetMethod("OnTileClicked", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(dock, [dock, entryId]);
        // Native focus changes complete through the UI message loop, as they do after a real click.
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) => frame.Continue = false;
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
    }

    private static void WithWindows(Action<DockWindow, AppServiceHost, AppEntry, Window> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "GlassDesk-dock-activation-" + Guid.NewGuid().ToString("N"));
        DockWindow? dock = null;
        Window? target = null;
        try
        {
            var data = new GlassDeskDataStore(new AppPaths(root));
            data.LoadAll();
            var entry = new AppEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                DisplayName = "Activation fixture",
                Kind = AppItemKind.Program,
                Target = Environment.ProcessPath!,
            };
            data.Library.Add(entry);
            using var host = new AppServiceHost(data);
            dock = new DockWindow();
            dock.AttachServices(host, DockSettingsHost.Initialize(Dispatcher.CurrentDispatcher), () => { });
            dock.Show();
            target = new Window { Title = "Activation fixture", Width = 240, Height = 160 };
            target.Show();
            Assert.Single(WindowMatcher.FindWindows(entry));
            body(dock, host, entry, target);
        }
        finally
        {
            target?.Close();
            dock?.Close();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
