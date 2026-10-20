using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassDesk.AppServices;
using GlassDesk.Core.Contracts;
using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;
using GlassDesk.Dock;
using GlassDesk.Launcher;
using GlassDesk.Platform;
using GlassDesk.UI.Settings;
using GlassDesk.UI.Theming;

namespace GlassDesk.UiAcceptance;

// This runner drives production WPF handlers/routed events, NOT injected hardware input.
// Keep it outside the shipping app. It never opens user documents or minimizes other apps.
internal static class Program
{
    private static readonly List<object> Results = [];
    // Evidence is read by people: the default encoder would turn every Chinese character
    // into \uXXXX.
    private static readonly JsonSerializerOptions ReportJson = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private static int failures;
    private static string root = "";
    private static DockWindow dock = null!;
    private static Window target = null!;
    private static GlassDeskDataStore data = null!;
    private static AppServiceHost host = null!;
    private static DockSettingsHost settings = null!;
    private static AppEntry sample = null!;
    private static string CopyTarget(string folder = "新目标 中文")
    {
        var directory = Path.Combine(root, folder);
        Directory.CreateDirectory(directory);
        foreach (var source in Directory.GetFiles(AppContext.BaseDirectory))
        {
            var destination = Path.Combine(directory, Path.GetFileName(source));
            if (!File.Exists(destination)) File.Copy(source, destination);
        }
        return Path.Combine(directory, "GlassDesk.UiAcceptance.exe");
    }

    [STAThread]
    public static int Main(string[] args)
    {
        if (GlassDesk.UI.BackdropCaptureWorker.RunIfRequested(args, out var workerExit)) return workerExit;
        if (args.Length == 2 && args[0] == "--drag-source")
        {
            var sourceApp = new Application();
            var label = new TextBlock { Text = "拖拽这个文件到 Dock：\n" + Path.GetFileName(args[1]),
                FontSize = 26, Margin = new Thickness(50), Padding = new Thickness(30), Background = Brushes.LightBlue };
            var sourceWindow = new Window { Title = "GlassDesk isolated file drag source", WindowState = WindowState.Maximized,
                Content = new StackPanel { Children = { label } } };
            label.MouseMove += (_, e) =>
            {
                if (e.LeftButton == MouseButtonState.Pressed)
                    DragDrop.DoDragDrop(label, new DataObject(DataFormats.FileDrop, new[] { args[1] }), DragDropEffects.Copy);
            };
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(90) };
            timer.Tick += (_, _) => sourceApp.Shutdown(); timer.Start();
            return sourceApp.Run(sourceWindow);
        }
        if (args.Contains("--owned-target"))
        {
            var targetApp = new Application();
            var owned = new Window { Title = "GlassDesk isolated launch target", Width = 280, Height = 130 };
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            timer.Tick += (_, _) => targetApp.Shutdown(); timer.Start();
            return targetApp.Run(owned);
        }
        var reportIndex = Array.IndexOf(args, "--report");
        var report = reportIndex >= 0 && reportIndex + 1 < args.Length ? Path.GetFullPath(args[reportIndex + 1])
            : Path.GetFullPath("phase4-ui.json");
        root = Path.Combine(Path.GetTempPath(), "GlassDesk-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        using var watchdog = new System.Threading.Timer(_ =>
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new
            {
                timestamp = DateTimeOffset.Now, failed = 1, status = "timeout",
                detail = "UI acceptance exceeded 120 seconds; no interactive input was supplied or a scenario hung.",
                dataDirectory = root
            }, ReportJson));
            Environment.Exit(2);
        }, null, TimeSpan.FromSeconds(120), System.Threading.Timeout.InfiniteTimeSpan);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            try
            {
                // --setup-only：只建好状态（数据库、程序坞、样本），不跑那 15 条路由事件用例。
                // 桌面驱动接管原生选择器时需要它：那些用例与「选择器能不能被真实鼠标操作」
                // 是两件事，混在一起跑会让每一步的失败原因变得说不清。
                await Run(includeCases: !args.Contains("--setup-only"));
                if (args.Contains("--picker")) await NativePicker();
                if (args.Contains("--drag")) await NativeDrag();
            }
            catch (Exception ex) { Record("runner", false, ex.ToString()); }
            finally
            {
                foreach (Window window in app.Windows.Cast<Window>().ToArray()) window.Close();
                host?.Dispose();
                Directory.CreateDirectory(Path.GetDirectoryName(report)!);
                File.WriteAllText(report, JsonSerializer.Serialize(new
                {
                    timestamp = DateTimeOffset.Now, dataDirectory = root,
                    driver = "production WPF handlers and routed events; no hardware input injection",
                    passed = Results.Count - failures, failed = failures, checks = Results,
                    externalCoverage = "Native picker, Explorer drag, packaged/UWP activation, physical hotplug and global desktop action are NOT asserted by this runner."
                }, ReportJson));
                Console.WriteLine($"Report: {report}; passed={Results.Count - failures}, failed={failures}");
                app.Shutdown(failures == 0 ? 0 : 1);
            }
        };
        return app.Run();
    }

    private static async Task NativeDrag()
    {
        var path = Path.Combine(root, "跨进程 中文 空格.txt"); File.WriteAllText(path, "cross process OLE");
        using var source = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, ArgumentList = { "--drag-source", path }
        })!;
        try
        {
            await Case("real cross-process CF_HDROP gesture preserves Chinese space path", async () =>
            {
                var timeout = Stopwatch.StartNew();
                while (!host.Library.Items.Any(x => x.Target == path))
                {
                    Require(timeout.Elapsed < TimeSpan.FromSeconds(80), "no external drop received before deadline");
                    await Task.Delay(100);
                }
                var imported = host.Library.Items.Single(x => x.Target == path);
                var reload = new GlassDeskDataStore(data.Paths); reload.LoadAll();
                Require(reload.Layouts.Dock.PinnedItemIds.Contains(imported.Id), "cross-process drop not persisted");
            });
        }
        finally { if (!source.HasExited) source.Kill(); }
    }

    private static async Task NativePicker()
    {
        // 这里刻意【不】再改程序坞的窗口属性（任务栏可见性、标题、尺寸、位置）。
        // 早先为了迁就只能按坐标猜测的驱动，这个夹具把程序坞放大到 1100x750
        // 并让它出现在任务栏上；那已经改动了生产窗口形态，读数就不再是生产数据。
        // 现在接管它的是 tests/GlassDesk.DesktopAcceptance：那个驱动按进程 + 窗口类
        // 寻找原生对话框，不依赖宿主窗口的大小与任务栏可见性。
        var replacement = CopyTarget("重新定位 新目标");
        sample.Arguments = "--owned-target";
        data.Layouts.Dock.PinnedItemIds = [sample.Id]; data.Layouts.SaveDock(); dock.RefreshTiles();
        Console.WriteLine("PICKER_REPLACEMENT=" + replacement);
        await Case("native picker relocation and real click launch of isolated executable", async () =>
        {
            Call("RelocateFromDialog", sample);
            await Task.Delay(150);
            var entry = host.Library.Find(sample.Id)!;
            Require(entry.Target == replacement && !entry.IsTargetMissing, "picker did not relocate to known replacement");
            var tile = Descendants<Border>(dock).First(x => x.DataContext is DockTileModel m && m.EntryId == sample.Id);
            Require(Descendants<Image>(tile).Any(x => x.Source is not null && x.Opacity == 1), "replacement icon missing or dimmed");
            Call("OnTileClicked", dock, sample.Id);

            // 轮询等窗口出现，不用固定等待：开发机上 800 ms 足够，慢速虚拟机里不够——
            // 客户机里这条断言连续三轮都因为等得太短而失败（重定位本身已经成功）。
            var appeared = false;
            for (var attempt = 0; attempt < 40 && !appeared; attempt++)
            {
                await Task.Delay(250);
                appeared = GlassDesk.Platform.Shell.WindowLocator.FindWindowsForImage(replacement).Count > 0;
            }

            var candidates = GlassDesk.Platform.Shell.WindowLocator.FindWindowsForImage(replacement);
            Require(appeared && candidates.Count > 0, "relocated executable window did not appear");
            foreach (var candidate in candidates) WindowControl.Close(candidate.Handle);
        });
    }

    private static async Task Run(bool includeCases = true)
    {
        data = new GlassDeskDataStore(new AppPaths(Path.Combine(root, "data")));
        data.LoadAll(); host = new AppServiceHost(data);
        var file = Path.Combine(root, "中文 空格.txt"); File.WriteAllText(file, "acceptance");
        sample = host.Import.ImportItems([new ImportRequest { Source = file }]).Added.Single();
        data.Layouts.Dock.PinnedItemIds.Add(sample.Id); data.Layouts.SaveDock();
        ThemeService.Initialize(Application.Current.Dispatcher).Update(data.Settings.Current.Appearance);
        settings = DockSettingsHost.Initialize(Application.Current.Dispatcher);
        settings.Update(data.Settings.Current.Dock);
        target = new Window { Title = "GlassDesk acceptance owned target", Width = 280, Height = 150, Left = 60, Top = 60 };
        target.Show();
        var launcher = new LauncherWindow();
        dock = new DockWindow();
        dock.AttachServices(host, settings, () => { launcher.Show(); launcher.Activate(); });
        dock.Show(); await Task.Delay(200);

        // --setup-only 走到这里就停：状态已经建好，接管原生选择器或拖拽的驱动
        // 需要的正是这个状态，而不是这 15 条路由事件用例。
        if (!includeCases) return;

        await Case("production Dock keeps noactivate and no-taskbar", () =>
        {
            Require(WindowStyles.IsNoActivate(Handle(dock)) && !dock.ShowInTaskbar, "production window styles changed");
            return Task.CompletedTask;
        });
        await Case("context menu cancellation restores previous foreground", async () =>
        {
            await ForegroundTarget();
            Call("OnTileContextMenuRequested", dock, sample.Id);
            await Task.Delay(150);
            var menu = Roots<ContextMenu>().Single(x => x.IsOpen);
            menu.IsOpen = false;
            await Task.Delay(200);
            Require(WindowControl.GetForegroundWindow() == Handle(target), "closing menu left foreground on Dock/another window");
        });
        await Case("chooser Escape closes and restores foreground", async () =>
        {
            await ForegroundTarget();
            Call("ShowWindowChooser", Candidates(target)); await Task.Delay(100);
            var list = Roots<ListBox>().Single();
            RaiseKey(list, Key.Escape);

            // 断言的是同一个可观察结果（弹层关闭 + 前台归还原窗口），只是不再用固定等待去
            // 卡时间：这台开发机上 200 ms 够，慢速虚拟机里不够——实测在客户机里这条用例
            // 连续三轮都因为固定 200 ms 太短而失败。改成轮询等待，慢环境多等一会儿，
            // 产品真的没关弹层时依然会失败。
            Require(await UntilOrFalse(() => !Roots<ListBox>().Any()), "Escape did not close chooser");
            Require(await UntilOrFalse(() => WindowControl.GetForegroundWindow() == Handle(target)), "Escape did not restore target");
        });
        await Case("chooser Enter activates selected real HWND", async () =>
        {
            var second = new Window { Title = "GlassDesk acceptance second target", Width = 220, Height = 120 };
            try
            {
                second.Show(); await ForegroundTarget();
                Call("ShowWindowChooser", Candidates(target, second)); await Task.Delay(100);
                var list = Roots<ListBox>().Single(); list.SelectedIndex = 1;
                RaiseKey(list, Key.Enter); await Task.Delay(200);
                Require(WindowControl.GetForegroundWindow() == Handle(second), "selected second window was not foreground");
                Require(!Roots<ListBox>().Any(), "chooser remained open");
            }
            finally { second.Close(); }
        });
        await Case("chooser clicking rendered title activates its row", async () =>
        {
            await ForegroundTarget();
            Call("ShowWindowChooser", Candidates(target)); await Task.Delay(100);
            var list = Roots<ListBox>().Single();
            var text = Descendants<TextBlock>(list).First(x => x.Text == target.Title);
            text.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = Mouse.PreviewMouseUpEvent });
            await Task.Delay(200);
            Require(!Roots<ListBox>().Any() && WindowControl.GetForegroundWindow() == Handle(target), $"title route failed: list={Roots<ListBox>().Count()}, foreground={WindowControl.GetForegroundWindow()}, expected={Handle(target)}");
        });
        await Case("chooser cancellation does not steal foreground from outside window", async () =>
        {
            var outside = new Window { Title = "GlassDesk acceptance outside", Width = 220, Height = 100 };
            try
            {
                outside.Show(); await ForegroundTarget();
                Call("ShowWindowChooser", Candidates(target)); await Task.Delay(100);
                var list = Roots<ListBox>().Single();
                outside.Activate();
                RaiseKey(list, Key.Escape); await Task.Delay(200);
                Require(WindowControl.GetForegroundWindow() == Handle(outside), "cancel stole foreground from outside target");
            }
            finally { outside.Close(); }
        });
        await Case("real Dock save excludes built-ins and survives reload", () =>
        {
            Call("SaveDockOrder", (object)new List<string> { "entry:launcher", sample.Id, sample.Id, "entry:recyclebin" });
            var reload = new GlassDeskDataStore(data.Paths); reload.LoadAll();
            Require(reload.Layouts.Dock.PinnedItemIds.SequenceEqual([sample.Id]), "saved order contains non-library ids");
            return Task.CompletedTask;
        });
        await Case("old contaminated layout cleaned on actual Dock attach", () =>
        {
            data.Layouts.Dock.PinnedItemIds = ["entry:launcher", sample.Id, "entry:showdesktop"];
            data.Layouts.SaveDock(); dock.AttachServices(host, settings, () => { launcher.Show(); });
            var reload = new GlassDeskDataStore(data.Paths); reload.LoadAll();
            Require(reload.Layouts.Dock.PinnedItemIds.SequenceEqual([sample.Id]), "migration failed");
            return Task.CompletedTask;
        });
        await Case("missing target is dimmed and click presents failure without shell dialog", () =>
        {
            File.Delete(file); host.Maintenance.RefreshMissingTargets(); Call("RefreshTiles");
            var tile = Descendants<Border>(dock).First(x => x.DataContext is DockTileModel m && m.EntryId == sample.Id);
            Require(Descendants<Image>(tile).Any(x => x.Opacity < 1), "missing target not visually dimmed");
            Call("OnTileClicked", dock, sample.Id);
            var status = (TextBlock)dock.FindName("StatusText");
            Require(status.Text.Contains("目标") || status.Text.Contains("定位"), "missing failure notice not presented");
            return Task.CompletedTask;
        });
        await Case("launcher entry opens actual launcher via callback", () =>
        {
            Call("OnTileClicked", dock, "entry:launcher");
            Require(launcher.IsVisible, "launcher did not appear"); launcher.Hide();
            return Task.CompletedTask;
        });
        await Case("icon size setting updates shown Dock without restart", async () =>
        {
            await Task.Delay(150);
            settings.Settings.IconSize = 64; settings.Refresh(); await Task.Delay(100);
            var tile = Descendants<Border>(dock).First(x => x.DataContext is DockTileModel m && m.EntryId == sample.Id);
            var image = Descendants<Image>(tile).Single();
            Require(image.ActualWidth >= 64 && image.ActualWidth <= 86.5, $"rendered icon width={image.ActualWidth}, expected 64..86.4");
        });
        await Case("folder panel Escape closes and returns foreground", async () =>
        {
            var folder = host.Import.ImportItems([new ImportRequest { Source = root }]).Added.Single();
            await ForegroundTarget(); Call("ShowFolderPanel", folder); await Task.Delay(100);
            var panel = Roots<Border>().Single(x => x.Child is ScrollViewer && x.Parent is Popup);
            RaiseKey(panel, Key.Escape); await Task.Delay(150);
            Require(!Roots<Border>().Any(x => x.Child is ScrollViewer && x.Parent is Popup), "folder panel stayed open after Escape");
            Require(WindowControl.GetForegroundWindow() == Handle(target), "folder panel did not restore previous foreground");
        });
        await Case("file-drop routed event imports Chinese space path and repins duplicate", () =>
        {
            var dropped = Path.Combine(root, "拖入 中文 空格.txt"); File.WriteAllText(dropped, "drop test");
            var payload = new DataObject(DataFormats.FileDrop, new[] { dropped });
            void Drop()
            {
                var e = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
                    null, [payload, DragDropKeyStates.None, DragDropEffects.Copy, dock, new Point(20, 20)], null)!;
                e.RoutedEvent = DragDrop.DropEvent; dock.RaiseEvent(e);
            }
            Drop();
            var imported = host.Library.Items.Single(x => x.Target == dropped);
            Require(data.Layouts.Dock.PinnedItemIds.Contains(imported.Id), "drop did not pin imported item");
            Call("OnUnpinRequested", dock, imported.Id); Drop();
            var reload = new GlassDeskDataStore(data.Paths); reload.LoadAll();
            Require(reload.Layouts.Dock.PinnedItemIds.Contains(imported.Id), "duplicate drop did not repin");
            Require(reload.Library.Items.Count(x => x.Target == dropped) == 1, "duplicate drop duplicated library entry");
            return Task.CompletedTask;
        });
        await Case("real Dock launches isolated process then activates same window and updates running dot", async () =>
        {
            var executable = CopyTarget();
            var entry = host.Import.ImportItems([new ImportRequest { Source = executable }]).Added.Single();
            entry.Arguments = "--owned-target";
            data.Layouts.Dock.PinnedItemIds.Add(entry.Id); data.Layouts.SaveDock();
            host.Running.Start(); dock.RefreshTiles();
            uint ownedPid = 0;
            try
            {
                Call("OnTileClicked", dock, entry.Id);
                ownedPid = host.Launch.LastLaunchedProcessId;
                Require(ownedPid != 0, "launch did not record owned PID");
                await Until(() => GlassDesk.Platform.Shell.WindowLocator.FindWindowsForImage(executable).Count == 1);
                var window = GlassDesk.Platform.Shell.WindowLocator.FindWindowsForImage(executable).Single();
                WindowControl.Minimize(window.Handle);
                await Until(() => WindowControl.IsMinimized(window.Handle));
                Call("OnTileClicked", dock, entry.Id);
                await Until(() => WindowControl.GetForegroundWindow() == window.Handle);
                Require(GlassDesk.Platform.Shell.WindowLocator.FindWindowsForImage(executable).Count == 1, "second click launched duplicate");
                await Until(() => host.Running.IsRunning(entry));
                dock.RefreshTiles();
                var tile = Descendants<Border>(dock).First(x => x.DataContext is DockTileModel m && m.EntryId == entry.Id);
                Require(Descendants<System.Windows.Shapes.Ellipse>(tile).Single().Visibility == Visibility.Visible, "running dot not visible");
                WindowControl.Close(window.Handle);
                await Until(() => !host.Running.IsRunning(entry));
                dock.RefreshTiles();
                tile = Descendants<Border>(dock).First(x => x.DataContext is DockTileModel m && m.EntryId == entry.Id);
                Require(Descendants<System.Windows.Shapes.Ellipse>(tile).Single().Visibility != Visibility.Visible, "running dot remained after target closed");
            }
            finally
            {
                if (ownedPid != 0)
                {
                    try
                    {
                        using var process = Process.GetProcessById((int)ownedPid);
                        if (!process.HasExited && string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase)) process.Kill();
                    }
                    catch (ArgumentException) { }
                }
            }
        });
        await Case("unpin through real menu persists but retains library entry", async () =>
        {
            Call("OnTileContextMenuRequested", dock, sample.Id); await Task.Delay(50);
            var menu = Roots<ContextMenu>().Single(x => x.IsOpen);
            var remove = menu.Items.OfType<MenuItem>().SingleOrDefault(x => Equals(x.Header, "从 Dock 移除"))
                ?? throw new InvalidOperationException("menu headers=" + string.Join("|", menu.Items.OfType<MenuItem>().Select(x => x.Header)));
            remove.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); menu.IsOpen = false;
            var reload = new GlassDeskDataStore(data.Paths); reload.LoadAll();
            Require(!reload.Layouts.Dock.PinnedItemIds.Contains(sample.Id) && reload.Library.Find(sample.Id) is not null,
                "unpin deleted library or failed persistence");
        });
    }

    private static async Task ForegroundTarget()
    {
        target.Activate(); await Task.Delay(100);
        Require(WindowControl.GetForegroundWindow() == Handle(target), "precondition: owned target cannot become foreground");
    }
    private static List<WindowCandidate> Candidates(params Window[] windows) => windows.Select(w => new WindowCandidate
        { Handle = Handle(w), Title = w.Title }).ToList();
    private static nint Handle(Window w) => new WindowInteropHelper(w).Handle;
    private static void RaiseKey(UIElement element, Key key) => element.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
        PresentationSource.FromVisual(element), Environment.TickCount, key) { RoutedEvent = Keyboard.KeyDownEvent });
    private static void Call(string name, params object?[] arguments) => typeof(DockWindow)
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(dock, arguments);
    private static IEnumerable<T> Roots<T>() where T : DependencyObject => PresentationSource.CurrentSources
        .OfType<HwndSource>().Where(s => s.RootVisual is not null).ToArray().SelectMany(s => Descendants<T>(s.RootVisual));
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var item in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return item;
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Until(Func<bool> predicate)
    {
        var timeout = Stopwatch.StartNew();
        while (!predicate())
        {
            Require(timeout.Elapsed < TimeSpan.FromSeconds(8), "timed out waiting for window/tracker state");
            await Task.Delay(50);
        }
    }

    /// <summary>
    /// 等到条件成立；超时返回 false 而不是抛异常，让调用方决定要报什么。
    /// </summary>
    /// <remarks>
    /// 给「弹层关闭」「前台归还」这类断言用：可观察结果本身没变，只是不再假设环境的处理速度。
    /// 开发机上几十毫秒就够，慢速虚拟机里要几百毫秒到几秒，固定等待会把慢环境判成产品缺陷。
    /// </remarks>
    private static async Task<bool> UntilOrFalse(Func<bool> predicate, int timeoutMs = 8000)
    {
        var timeout = Stopwatch.StartNew();

        while (timeout.ElapsedMilliseconds < timeoutMs)
        {
            if (predicate())
            {
                return true;
            }

            await Task.Delay(50);
        }

        return predicate();
    }
    private static async Task Case(string name, Func<Task> action)
    {
        try { await action(); Record(name, true, "verified"); }
        catch (Exception ex)
        {
            string? capture = null;
            try
            {
                capture = Path.Combine(root, $"failure-{Results.Count + 1}.png");
                var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(dock.ActualWidth)),
                    Math.Max(1, (int)Math.Ceiling(dock.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(dock);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(capture); png.Save(stream);
            }
            catch (Exception captureError) { capture = "capture unavailable: " + captureError.Message; }
            Record(name, false, ex + Environment.NewLine + "Dock WPF render: " + capture);
        }
        finally
        {
            foreach (var menu in Roots<ContextMenu>().ToArray()) menu.IsOpen = false;
            foreach (var popup in Roots<FrameworkElement>().Select(x => x.Parent).OfType<Popup>().Distinct().ToArray()) popup.IsOpen = false;
            await Task.Delay(50);
        }
    }
    private static void Record(string name, bool pass, string detail)
    {
        if (!pass) failures++;
        Results.Add(new { name, pass, detail }); Console.WriteLine($"{(pass ? "PASS" : "FAIL")} {name}: {detail}");
    }
}
