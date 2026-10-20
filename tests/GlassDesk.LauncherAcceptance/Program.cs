using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GlassDesk.AppServices;
using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;
using GlassDesk.Launcher;
using GlassDesk.Platform.Displays;
using GlassDesk.Settings;
using GlassDesk.UI.Controls;
using GlassDesk.UI.Theming;

internal static partial class Program
{
    private static readonly List<object> Cases = [];
    private static int _failed;

    [STAThread]
    private static int Main(string[] args)
    {
        if (GlassDesk.UI.BackdropCaptureWorker.RunIfRequested(args, out var workerExit)) return workerExit;
        if (args.Contains("--appearance-customization")) return VerifyAppearanceCustomization(args);
        if (args.Contains("--dock-builtin-entries")) return VerifyDockBuiltinEntries(args);
        if (args.Contains("--desktop-icon-continuity")) return VerifyDockBuiltinEntries(args);
        if (args.Contains("--fullscreen-fixture")) return RunPerformanceFullscreenFixture(args);
        if (args.Contains("--full-host-performance")) return VerifyFullHostPerformance(args);
        if (args.Contains("--full-host-input")) return VerifyFullHostInput(args);
        var report = Argument(args, "--report") ?? Path.Combine(Environment.CurrentDirectory, "phase5-wpf.json");
        var supplied = Argument(args, "--data-dir");
        var root = supplied is null ? Path.Combine(Path.GetTempPath(), "GlassDesk-P5-Wpf-" + Guid.NewGuid().ToString("n")) : Path.GetFullPath(supplied);
        var real = AppPaths.ForCurrentUser().RootDirectory;
        if (root.Equals(real, StringComparison.OrdinalIgnoreCase) || root.StartsWith(real + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("Refusing to use the user's GlassDesk data directory.");
            return 2;
        }
        Directory.CreateDirectory(root);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/GlassDesk.UI;component/Theming/LiquidGlassTheme.xaml", UriKind.Absolute)
        });
        var evidenceDirectory = Path.GetDirectoryName(Path.GetFullPath(report))!;
        Directory.CreateDirectory(evidenceDirectory);
        LauncherWindow? window = null;
        SettingsWindow? settingsWindow = null;
        var invalidShaders = 0;
        EventHandler shaderFailure = (_, _) => invalidShaders++;
        System.Windows.Media.Effects.PixelShader.InvalidPixelShaderEncountered += shaderFailure;
        try
        {
            var data = new GlassDeskDataStore(new AppPaths(root));
            data.LoadAll();
            ThemeService.Initialize(app.Dispatcher).Update(data.Settings.Current.Appearance);
            for (var i = 0; i < 36; i++)
                data.Library.Add(new AppEntry { Id = $"wpf-{i:00}", DisplayName = i == 35 ? "启动台" : $"App {i:00}",
                    Target = Path.Combine(root, $"missing-{i}"), IsTargetMissing = true });
            data.Library.Save();
            using var host = new AppServiceHost(data);
            var primarySession = new PrimaryDisplaySession(DisplayService.Primary()
                ?? throw new InvalidOperationException("Windows Primary Monitor is unavailable."));
            window = new LauncherWindow();
            if (args.Contains("--glass-input-continuity") || args.Contains("--ios-manual") || args.Contains("--manual-scripted-input") || args.Contains("--high-rate-drag-input") || args.Contains("--ios-input") || args.Contains("--wide-drag-input") || args.Contains("--irregular-drag-input") || args.Contains("--sustained-drag-input")
                || args.Contains("--capture-drag-input") || args.Contains("--launcher-dock-input")
                || args.Contains("--continuous-backdrop") || args.Contains("--desktop-backdrop") || args.Contains("--folder-interactions"))
            {
                typeof(LauncherWindow).GetField("_keepOpenWhileModalCount", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, 1);
                window.ShowInTaskbar = true;
            }
            if (Argument(args, "--isolated-worker") is { } workerExecutable)
                UseIsolatedBackdrop(window, workerExecutable);
            window.BindPrimaryDisplay(primarySession);
            window.AttachServices(host, true);
            window.ApplySettings(data.Settings.Current.Launcher);
            window.Show();
            Pump(TimeSpan.FromMilliseconds(200));
            if (args.Contains("--expect-basic-background"))
            {
                var surface = (LiquidGlassSurface)window.FindName("GlassSurface")!;
                Check("采集不可用时在5秒内提供基础背景", "compatibility/basic",
                    PumpUntil(() => surface.BackdropSource == GlassBackdropSource.Basic, TimeSpan.FromSeconds(5)),
                    "Basic，输入仍可用", surface.BackdropSource.ToString());
            }
            var grid = (System.Windows.Controls.Primitives.UniformGrid)window.FindName("TileGrid")!;
            var search = (TextBox)window.FindName("SearchBox")!;
            var pageText = (TextBlock)window.FindName("PageText")!;
            var next = (Button)window.FindName("NextPage")!;
            if (args.Contains("--folder-interactions"))
            {
                VerifyFolderInteractions(window, grid, data, evidenceDirectory, Argument(args, "--folder-icons-dir"));
                return _failed == 0 ? 0 : 1;
            }
            Check("默认网格首屏 35 项", "L-07/L-08", grid.Children.Count == 35, "35", grid.Children.Count.ToString());
            if (args.Contains("--glass-input-continuity"))
            {
                VerifyGlassInputContinuity(window, grid, evidenceDirectory, Argument(args, "--fixture-exe"));
                return _failed == 0 ? 0 : 1;
            }
            if (args.Contains("--launcher-dock-input"))
            {
                VerifyLauncherDockInput(window, grid, data, Argument(args, "--dock-assembly") ?? throw new ArgumentException("--dock-assembly is required"), evidenceDirectory);
                return _failed == 0 ? 0 : 1;
            }
            if (args.Contains("--capture-drag-input"))
            {
                WithManualDragTiming(window, evidenceDirectory, () => VerifyCaptureDragInput(window, grid, data, evidenceDirectory), true);
                return _failed == 0 ? 0 : 1;
            }
            if (args.Contains("--ole-render-wake"))
            {
                // Compatibility alias for the R8 test-kit command; local dragging no longer uses that OLE loop.
                VerifyCaptureDragInput(window, grid, data, evidenceDirectory);
                return _failed == 0 ? 0 : 1;
            }
            if (args.Contains("--high-rate-drag-input"))
            {
                WithManualDragTiming(window, evidenceDirectory, () =>
                    VerifyWideDragInput(window, grid, data, evidenceDirectory, irregular: true, sustained: true, highRatePackets: true), true);
                return _failed == 0 ? 0 : 1;
            }
            if (args.Contains("--input-device-probe-check"))
            {
                VerifyInputDeviceProbe(window, grid, data, evidenceDirectory);
                return _failed == 0 ? 0 : 1;
            }
            if (args.Contains("--manual-scripted-input"))
            {
                WithManualDragTiming(window, evidenceDirectory, () =>
                {
                    Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                    {
                        try { VerifyWideDragInput(window, grid, data, evidenceDirectory, irregular: true, sustained: true); }
                        finally { File.WriteAllText(Path.Combine(evidenceDirectory, "stop-manual"), ""); }
                    }));
                    var ended = WaitForManualAcceptance(evidenceDirectory, TimeSpan.FromSeconds(60));
                    Check("持续真实输入经过手动验收等待后正常结束", "regression/manual-stop", ended, "结束标记生效", ended.ToString());
                }, args.Contains("--input-device-diagnostics"));
                return _failed == 0 ? 0 : 1;
            }
            if (args.Contains("--manual-stop-responsiveness"))
            {
                VerifyManualStopResponsiveness(window, evidenceDirectory);
                return _failed == 0 ? 0 : 1;
            }
            if (args.Contains("--drag-motion-load"))
            {
                VerifyDragMotionLoad(window, grid, evidenceDirectory);
                return _failed == 0 ? 0 : 1;
            }
            if (args.Contains("--drag-cadence"))
            {
                VerifyDragCadence(window, grid, evidenceDirectory);
                return _failed == 0 ? 0 : 1;
            }
            if (args.Contains("--wide-drag-input") || args.Contains("--irregular-drag-input") || args.Contains("--sustained-drag-input"))
            {
                WithManualDragTiming(window, evidenceDirectory, () =>
                    VerifyWideDragInput(window, grid, data, evidenceDirectory,
                        args.Contains("--irregular-drag-input") || args.Contains("--sustained-drag-input"), args.Contains("--sustained-drag-input")));
                return _failed == 0 ? 0 : 1;
            }
            if (args.Contains("--wide-drag"))
            {
                VerifyWideDrag(window, grid, data, evidenceDirectory);
                return _failed == 0 ? 0 : 1;
            }
            if (args.Contains("--rapid-interaction"))
            {
                VerifyRapidInteraction(window, grid, data, evidenceDirectory);
                return _failed == 0 ? 0 : 1;
            }
            if (args.Contains("--compatibility-stress"))
            {
                VerifyCompatibilityStress(window, pageText, evidenceDirectory);
                return _failed == 0 ? 0 : 1;
            }
            if (args.Contains("--continuous-backdrop") || args.Contains("--desktop-backdrop"))
            {
                VerifyContinuousBackdrop(window, Argument(args, "--moving-fixture"), evidenceDirectory,
                    args.Contains("--desktop-backdrop"), int.TryParse(Argument(args, "--sample-seconds"), out var seconds) ? Math.Clamp(seconds, 6, 180) : 6);
                return _failed == 0 ? 0 : 1;
            }
            if (args.Contains("--performance-only"))
            {
                VerifyAsyncPageIcons(window, grid, data, root);
                VerifyLivePaging(window, grid, evidenceDirectory);
                return _failed == 0 ? 0 : 1;
            }
            Capture(window, Path.Combine(evidenceDirectory, "phase5-launcher.png"));
            if (args.Contains("--ios-input"))
            {
                VerifyIosNativeInput(window, grid, data, evidenceDirectory);
                return _failed == 0 ? 0 : 1;
            }
            if (args.Contains("--ios-manual"))
            {
                typeof(LauncherWindow).GetField("_keepOpenWhileModalCount", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, 1);
                window.Activate();
                WithManualDragTiming(window, evidenceDirectory, () =>
                    WaitForManualAcceptance(evidenceDirectory, TimeSpan.FromMinutes(10)), args.Contains("--input-device-diagnostics"));
                return 0;
            }
            if (args.Contains("--ios-drag"))
            {
                VerifyIosDrag(window, grid, data, evidenceDirectory);
                return _failed == 0 ? 0 : 1;
            }
            if (args.Contains("--mouse-only"))
            {
                var mouseWorkspace = new LauncherWorkspace(host);
                var mouseEdit = mouseWorkspace.BeginEdit();
                mouseEdit.Draft = LauncherLayoutEditor.CreateGroup(mouseEdit.Draft, "wpf-00", "wpf-01", "mouse-group", 35);
                mouseWorkspace.Commit(mouseEdit);
                window.RefreshLibrary();
                window.Activate();
                VerifyMouseOnlyTiles(window, grid, search);
                return _failed == 0 ? 0 : 1;
            }
            var display = primarySession.Display;
            {
                var source = new PixelRect(display.WorkArea.X + 24, display.WorkArea.Bottom - 88, 48, 64);
                window.CloseWithTransition();
                var windowHandle = new WindowInteropHelper(window).Handle;
                var expectedDestinationBounds = DisplayService.GetWindowPixelBounds(windowHandle);
                var requiredAnimationBounds = expectedDestinationBounds is { } destinationBounds
                    ? Union(source, destinationBounds)
                    : (PixelRect?)null;
                PixelRect? expectedAnimationHostBounds = null;
                var renderFrames = 0;
                var longestFrameGap = TimeSpan.Zero;
                long previousFrame = 0;
                var animationHostStayedFixed = true;
                var activeTransformFrames = 0;
                var sampleTransitionFrames = false;
                EventHandler sampleRender = (_, _) =>
                {
                    if (!sampleTransitionFrames) return;
                    var now = Stopwatch.GetTimestamp();
                    if (previousFrame != 0)
                        longestFrameGap = TimeSpan.FromSeconds(Math.Max(longestFrameGap.TotalSeconds,
                            Stopwatch.GetElapsedTime(previousFrame, now).TotalSeconds));
                    previousFrame = now;
                    renderFrames++;
                    if (window.IsVisible && !window.CanNavigatePages)
                    {
                        activeTransformFrames++;
                        if (expectedAnimationHostBounds is { } fixedHost
                            && DisplayService.GetWindowPixelBounds(windowHandle) != fixedHost)
                            animationHostStayedFixed = false;
                    }
                };
                CompositionTarget.Rendering += sampleRender;
                previousFrame = 0;
                sampleTransitionFrames = true;
                var openingElapsed = Stopwatch.StartNew();
                window.ShowWithTransition(source);
                var openingHostBounds = DisplayService.GetWindowPixelBounds(windowHandle);
                Check("启动台过渡宿主包含按钮与面板并预留回弹空间", "L-01",
                    openingHostBounds is { } openingHost && requiredAnimationBounds is { } required
                    && openingHost.X <= required.X && openingHost.Y <= required.Y
                    && openingHost.Right >= required.Right && openingHost.Bottom >= required.Bottom,
                    requiredAnimationBounds?.ToString() ?? "读取按钮与目标面板包络", openingHostBounds?.ToString() ?? "无法读取窗口矩形");
                expectedAnimationHostBounds = openingHostBounds;
                var expectedClip = expectedAnimationHostBounds is { } animationHost
                    ? new Rect((source.X - animationHost.X) * 96.0 / display.Dpi,
                        (source.Y - animationHost.Y) * 96.0 / display.Dpi,
                        source.Width * 96.0 / display.Dpi,
                        source.Height * 96.0 / display.Dpi)
                    : Rect.Empty;
                var actualClip = ((Border)window.FindName("ContainerTransformClipRoot")!).Clip?.Bounds ?? Rect.Empty;
                Check("启动台动画首帧轮廓精准对齐 Dock 按钮", "L-01",
                    Math.Abs(actualClip.X - expectedClip.X) < .2 && Math.Abs(actualClip.Y - expectedClip.Y) < .2
                    && Math.Abs(actualClip.Width - expectedClip.Width) < .2 && Math.Abs(actualClip.Height - expectedClip.Height) < .2,
                    expectedClip.ToString(), actualClip.ToString());
                var openingCompleted = PumpUntil(() => window.IsVisible && window.CanNavigatePages,
                    TimeSpan.FromSeconds(2));
                openingElapsed.Stop();
                sampleTransitionFrames = false;
                Check("启动台动画期间宿主矩形保持稳定", "L-01", animationHostStayedFixed && activeTransformFrames > 0,
                    "动画帧期间矩形不变", $"活跃帧 {activeTransformFrames}；稳定 {animationHostStayedFixed}；渲染帧 {renderFrames}");
                Check("启动台共享过渡打开完成", "L-01", window.IsVisible && window.CanNavigatePages,
                    "窗口可见且动画结束", $"可见 {window.IsVisible}；可翻页 {window.CanNavigatePages}；渲染帧 {renderFrames}；最长帧间隔 {longestFrameGap.TotalMilliseconds:0} ms；等待 {openingElapsed.ElapsedMilliseconds} ms");
                Check("启动台打开后保留透明宿主供反向变形", "L-01",
                    DisplayService.GetWindowPixelBounds(windowHandle) == expectedAnimationHostBounds,
                    expectedAnimationHostBounds?.ToString() ?? "读取透明宿主矩形",
                    DisplayService.GetWindowPixelBounds(windowHandle)?.ToString() ?? "无法读取窗口矩形");
                Check("启动台打开过渡不长时间阻塞界面", "L-01",
                    openingCompleted && openingElapsed.Elapsed < TimeSpan.FromMilliseconds(1200),
                    "过渡在 1200 ms 内结束", $"完成 {openingCompleted}；耗时 {openingElapsed.ElapsedMilliseconds} ms；最长渲染帧间隔 {longestFrameGap.TotalMilliseconds:0} ms");
                previousFrame = 0;
                sampleTransitionFrames = true;
                var closingElapsed = Stopwatch.StartNew();
                long closedAt = -1;
                DependencyPropertyChangedEventHandler captureClose = (_, e) => { if (e.NewValue is false) closedAt = closingElapsed.ElapsedMilliseconds; };
                window.IsVisibleChanged += captureClose;
                window.CloseWithTransition();
                var closeDispatchMs = closingElapsed.ElapsedMilliseconds;
                var closingCompleted = PumpUntil(() => !window.IsVisible, TimeSpan.FromSeconds(2));
                window.IsVisibleChanged -= captureClose;
                closingElapsed.Stop();
                sampleTransitionFrames = false;
                Check("启动台共享过渡反向关闭完成", "L-01", closingCompleted && !window.IsVisible,
                    "窗口缩回按钮后隐藏", $"可见 {window.IsVisible}；渲染帧 {renderFrames}；最长帧间隔 {longestFrameGap.TotalMilliseconds:0} ms");
                Check("关闭后保留透明过渡宿主供下次复用", "L-01",
                    DisplayService.GetWindowPixelBounds(windowHandle) == expectedAnimationHostBounds,
                    expectedAnimationHostBounds?.ToString() ?? "读取过渡宿主矩形",
                    DisplayService.GetWindowPixelBounds(windowHandle)?.ToString() ?? "无法读取窗口矩形");
                Check("启动台关闭更快并在弹簧稳定后隐藏", "L-01",
                    closingCompleted && closedAt >= 250 && closedAt < 600 && closeDispatchMs < 150,
                    "隐藏事件250..600ms（含调度余量），同步调用<150ms", $"完成 {closingCompleted}；轮询耗时 {closingElapsed.ElapsedMilliseconds} ms；同步调用 {closeDispatchMs} ms；隐藏事件 {closedAt} ms；最长渲染帧间隔 {longestFrameGap.TotalMilliseconds:0} ms");
                var surface = (LiquidGlassSurface)window.FindName("GlassSurface")!;
                surface.Visibility = Visibility.Collapsed;
                renderFrames = 0;
                longestFrameGap = TimeSpan.Zero;
                previousFrame = 0;
                sampleTransitionFrames = true;
                var withoutGlassElapsed = Stopwatch.StartNew();
                window.ShowWithTransition(source);
                Check("再次打开复用相同宿主矩形", "L-01",
                    DisplayService.GetWindowPixelBounds(windowHandle) == expectedAnimationHostBounds,
                    expectedAnimationHostBounds?.ToString() ?? "读取过渡宿主矩形",
                    DisplayService.GetWindowPixelBounds(windowHandle)?.ToString() ?? "无法读取窗口矩形");
                var withoutGlassCompleted = PumpUntil(() => window.IsVisible && window.CanNavigatePages,
                    TimeSpan.FromSeconds(2));
                withoutGlassElapsed.Stop();
                sampleTransitionFrames = false;
                Check("关闭液态玻璃时启动台过渡完成", "L-01", withoutGlassCompleted
                    && window.IsVisible && window.CanNavigatePages,
                    "窗口可见且动画结束", $"可见 {window.IsVisible}；可翻页 {window.CanNavigatePages}；渲染帧 {renderFrames}；最长帧间隔 {longestFrameGap.TotalMilliseconds:0} ms；耗时 {withoutGlassElapsed.ElapsedMilliseconds} ms");
                window.CloseWithTransition();
                PumpUntil(() => !window.IsVisible, TimeSpan.FromSeconds(2));
                surface.Visibility = Visibility.Visible;
                CompositionTarget.Rendering -= sampleRender;
                window.Show();
                Pump(TimeSpan.FromMilliseconds(100));
            }
            RecordTransitionPerformance(window, evidenceDirectory);
            Check("动画折射着色器无无效字节码报告", "L-01", invalidShaders == 0,
                "0 个无效着色器事件", invalidShaders.ToString());
            VerifyUnifiedContainer(window, grid, evidenceDirectory);
            VerifyPageSlide(window, grid, evidenceDirectory);
            next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("生产翻页按钮到末页", "L-08", grid.Children.Count == 1 && pageText.Text == "2 / 2", "1 项、2 / 2", $"{grid.Children.Count} 项、{pageText.Text}");
            search.Text = "qidongtai";
            Check("真实 TextBox 全拼过滤", "L-03", grid.Children.Count == 1 && pageText.Text == "1 / 1", "命中 1 项", $"{grid.Children.Count} 项、{pageText.Text}");
            search.Clear();
            Check("清空搜索恢复浏览页", "L-21", pageText.Text == "2 / 2", "2 / 2", pageText.Text);
            Check("打开时搜索框焦点", "L-19", Keyboard.FocusedElement == search, "搜索框有焦点", Keyboard.FocusedElement?.GetType().Name ?? "无焦点");
            var edit = new LauncherWorkspace(host).BeginEdit();
            edit.Draft = LauncherLayoutEditor.CreateGroup(edit.Draft, "wpf-00", "wpf-01", "wpf-group", 35);
            new LauncherWorkspace(host).Commit(edit);
            window.RefreshLibrary();
            Check("真实窗口呈现分组并保持单层", "L-12/L-16", grid.Children.Count == 35,
                "35 个顶层槽位", $"{grid.Children.Count} 个顶层槽位");
            VerifyMouseOnlyTiles(window, grid, search);
            var groupPanel = (Border)window.FindName("GroupPanel")!;
            ((Button)grid.Children[0]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("生产分组按钮展开面板", "L-12/L-14", groupPanel.Visibility == Visibility.Visible,
                "分组面板可见", groupPanel.Visibility.ToString());
            FindVisual<Button>(groupPanel).Single(button => Equals(button.Content, "返回"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("鼠标返回关闭分组", "L-21", groupPanel.Visibility == Visibility.Collapsed && window.IsVisible,
                "面板关闭且窗口仍在", $"面板 {groupPanel.Visibility}、窗口 {window.IsVisible}");
            settingsWindow = new SettingsWindow();
            settingsWindow.BindPrimaryDisplay(primarySession);
            settingsWindow.AttachDataStore(data, null);
            settingsWindow.LauncherSettingsChanged += (_, changed) => window.ApplySettings(changed);
            settingsWindow.PositionOnPrimary();
            settingsWindow.Show();
            ((Slider)settingsWindow.FindName("LauncherRowsSlider")!).Value = 4;
            ((Slider)settingsWindow.FindName("LauncherColumnsSlider")!).Value = 5;
            Check("设置页即时更改网格容量", "L-07", grid.Children.Count == 20,
                "20 项", $"{grid.Children.Count} 项");
            var settingsReload = new GlassDeskDataStore(new AppPaths(root));
            settingsReload.LoadAll();
            Check("启动台设置重载保持", "L-07/L-23", settingsReload.Settings.Current.Launcher.Rows == 4
                && settingsReload.Settings.Current.Launcher.Columns == 5, "4 行、5 列",
                $"{settingsReload.Settings.Current.Launcher.Rows} 行、{settingsReload.Settings.Current.Launcher.Columns} 列");
            ((ListBox)settingsWindow.FindName("SettingsPageList")!).SelectedIndex = 2;
            Pump(TimeSpan.FromMilliseconds(60));
            Capture(settingsWindow, Path.Combine(evidenceDirectory, "phase5-settings.png"));
            var beforeDrag = File.ReadAllBytes(data.Layouts.LauncherFilePath);
            typeof(LauncherWindow).GetField("_dragEdit", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(window, new LauncherWorkspace(host).BeginEdit());
            Check("路由拖拽预览不写盘", "R-08", beforeDrag.SequenceEqual(File.ReadAllBytes(data.Layouts.LauncherFilePath)),
                "原文件不变", "已核对文件字节");
            var destination = (Button)grid.Children[0];
            var payload = new DataObject("GlassDesk.Launcher.Slot", "wpf-02");
            var drop = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null, args: [payload, DragDropKeyStates.None, DragDropEffects.Move, destination, new Point(1, 1)], culture: null)!;
            drop.RoutedEvent = DragDrop.DropEvent;
            destination.RaiseEvent(drop);
            var afterDrag = LauncherLayoutRules.AppIds(data.Layouts.Launcher);
            Check("生产 Drop 路由跨页排序落盘", "L-09/L-11/R-08", afterDrag.Count == 36 && afterDrag.Distinct().Count() == 36
                && !beforeDrag.SequenceEqual(File.ReadAllBytes(data.Layouts.LauncherFilePath)),
                "36 个唯一 Id 且文件变化", $"{afterDrag.Count} 个；文件变化 {!beforeDrag.SequenceEqual(File.ReadAllBytes(data.Layouts.LauncherFilePath))}");
            var newFile = Path.Combine(root, "中文 空格.txt");
            File.WriteAllText(newFile, "sample");
            var fileData = new DataObject(DataFormats.FileDrop, new[] { newFile });
            var fileDrop = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null, args: [fileData, DragDropKeyStates.None, DragDropEffects.Copy, window, new Point(5, 5)], culture: null)!;
            fileDrop.RoutedEvent = DragDrop.DropEvent;
            window.RaiseEvent(fileDrop);
            Check("生产文件 Drop 路由导入共享库", "L-17", data.Library.Items.Count == 37
                && LauncherLayoutRules.AppIds(data.Layouts.Launcher).Count == 37,
                "库与布局均 37 项", $"库 {data.Library.Items.Count} 项，布局 {LauncherLayoutRules.AppIds(data.Layouts.Launcher).Count} 项");
            Pump(TimeSpan.FromMilliseconds(60));
            window.UpdateLayout();
            var centerDropTarget = grid.Children.OfType<Button>().First(button => button.Tag is string id
                && id.StartsWith("wpf-", StringComparison.Ordinal) && id != "wpf-04");
            var orderBeforeCenterDrop = LauncherLayoutRules.AppIds(data.Layouts.Launcher).ToArray();
            var groupsBeforeCenterDrop = data.Layouts.Launcher.Pages.SelectMany(page => page.Slots)
                .Count(slot => slot.Kind == LauncherSlotKind.Group);
            typeof(LauncherWindow).GetField("_dragEdit", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(window, new LauncherWorkspace(host).BeginEdit());
            var centerDropData = new DataObject("GlassDesk.Launcher.Slot", "wpf-04");
            var centerDrop = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null, args: [centerDropData, DragDropKeyStates.None, DragDropEffects.Move, centerDropTarget,
                    new Point(centerDropTarget.ActualWidth / 2, centerDropTarget.ActualHeight / 2)], culture: null)!;
            centerDrop.RoutedEvent = DragDrop.DropEvent;
            centerDropTarget.RaiseEvent(centerDrop);
            var orderAfterCenterDrop = LauncherLayoutRules.AppIds(data.Layouts.Launcher).ToArray();
            var groupsAfterCenterDrop = data.Layouts.Launcher.Pages.SelectMany(page => page.Slots)
                .Count(slot => slot.Kind == LauncherSlotKind.Group);
            Check("生产 Drop 路由中心落点只调整顺序，不创建分组", "L-09/L-12", orderAfterCenterDrop.Length == 37
                && orderAfterCenterDrop.Distinct().Count() == 37 && !orderBeforeCenterDrop.SequenceEqual(orderAfterCenterDrop)
                && groupsAfterCenterDrop == groupsBeforeCenterDrop,
                "37 个唯一应用顺序变化且分组数不变", $"唯一应用 {orderAfterCenterDrop.Distinct().Count()}，分组 {groupsBeforeCenterDrop} → {groupsAfterCenterDrop}，顺序变化 {!orderBeforeCenterDrop.SequenceEqual(orderAfterCenterDrop)}");
            window.RefreshLibrary();
            var beforeFailure = File.ReadAllBytes(data.Layouts.LauncherFilePath);
            var orderBeforeFailure = LauncherLayoutRules.AppIds(data.Layouts.Launcher).ToArray();
            typeof(LauncherWindow).GetField("_dragEdit", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(window, new LauncherWorkspace(host).BeginEdit());
            var failureTarget = grid.Children.OfType<Button>().First(button => button.Tag is string id && id != "wpf-05");
            using (File.Open(data.Layouts.LauncherFilePath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var failureData = new DataObject("GlassDesk.Launcher.Slot", "wpf-05");
                var failureDrop = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
                    binder: null, args: [failureData, DragDropKeyStates.None, DragDropEffects.Move, failureTarget, new Point(1, 1)], culture: null)!;
                failureDrop.RoutedEvent = DragDrop.DropEvent;
                failureTarget.RaiseEvent(failureDrop);
            }
            Check("生产 Drop 保存失败后窗口仍加载且完整回滚", "R-08", beforeFailure.SequenceEqual(File.ReadAllBytes(data.Layouts.LauncherFilePath))
                && orderBeforeFailure.SequenceEqual(LauncherLayoutRules.AppIds(data.Layouts.Launcher)) && window.IsLoaded,
                "文件、共享布局不变且窗口仍加载", $"文件不变 {beforeFailure.SequenceEqual(File.ReadAllBytes(data.Layouts.LauncherFilePath))}，窗口已加载 {window.IsLoaded}");
        }
        catch (Exception ex)
        {
            _failed++;
            Cases.Add(new { name = "runner", ids = "L-01-L-25", driver = "WPF window", expected = "no exception",
                actual = ex.ToString(), status = "Failed" });
        }
        finally
        {
            System.Windows.Media.Effects.PixelShader.InvalidPixelShaderEncountered -= shaderFailure;
            window?.Close();
            settingsWindow?.Close();
            ThemeService.Current?.Dispose();
            app.Shutdown();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
            File.WriteAllText(report, JsonSerializer.Serialize(new
            {
                timestamp = DateTimeOffset.Now, artifact = typeof(LauncherWindow).Assembly.FullName,
                productFileVersion = FileVersionInfo.GetVersionInfo(typeof(LauncherWindow).Assembly.Location).FileVersion,
                launcherSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(typeof(LauncherWindow).Assembly.Location))).ToLowerInvariant(),
                dragImplementation = typeof(LauncherWindow).GetField("_logicalIconDrag", BindingFlags.Instance | BindingFlags.NonPublic) is not null
                    ? "launcher mouse capture; OLE for cross-window transfer" : "full-session OLE",
                invalidShaderCount = invalidShaders,
                environment = Environment.OSVersion.ToString(), dataDirectory = root,
                screenshots = args.Contains("--folder-interactions") ? Directory.GetFiles(evidenceDirectory, "*-live.png")
                    : new[] { Path.Combine(evidenceDirectory, "phase5-launcher.png"), Path.Combine(evidenceDirectory, "phase5-settings.png") },
                driver = args.Contains("--glass-input-continuity") ? "Native pointer movement and mouse buttons, production captured drag, routed page buttons"
                    : args.Contains("--launcher-dock-input") || args.Contains("--capture-drag-input") || args.Contains("--ios-input") || args.Contains("--ole-render-wake") || args.Contains("--high-rate-drag-input") || args.Contains("--input-device-probe-check") || args.Contains("--wide-drag-input") || args.Contains("--irregular-drag-input") || args.Contains("--sustained-drag-input") || args.Contains("--manual-scripted-input") || args.Contains("--folder-interactions") ? "SendInput mouse/keyboard, production capture and cross-window OLE paths"
                    : args.Contains("--ios-manual") ? "physical device/manual input, production capture and cross-window OLE paths"
                    : "real WPF controls and routed events; no system input injection",
                passed = Cases.Count - _failed, failed = _failed, cases = Cases,
            }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            if (supplied is null && Directory.Exists(root))
                try { Directory.Delete(root, true); } catch (IOException) { }
        }
        Console.WriteLine($"Phase 5 WPF: {Cases.Count - _failed} passed, {_failed} failed. Report: {Path.GetFullPath(report)}");
        return _failed == 0 ? 0 : 1;
    }

    private static string? Argument(string[] args, string key)
    {
        var index = Array.IndexOf(args, key);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static void Check(string name, string ids, bool passed, string expected, string actual)
    {
        if (!passed) _failed++;
        Cases.Add(new { name, ids, driver = ids == "IOS-INPUT" ? "system mouse/keyboard input" : "production WPF route/control state", expected, actual,
            status = passed ? "Passed" : "Failed", timestamp = DateTimeOffset.Now });
    }

    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        // This timer bounds the test phase; it must still fire when production
        // rendering keeps lower dispatcher priorities busy.
        var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static bool PumpUntil(Func<bool> completed, TimeSpan timeout)
    {
        var elapsed = Stopwatch.StartNew();
        var result = completed();
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            result = completed();
            if (result || elapsed.Elapsed >= timeout)
            {
                timer.Stop();
                frame.Continue = false;
            }
        };
        if (!result)
        {
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
        return result;
    }

    private static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path);
        encoder.Save(output);
    }

    private static PixelRect Union(PixelRect left, PixelRect right)
    {
        var x = Math.Min(left.X, right.X);
        var y = Math.Min(left.Y, right.Y);
        return new PixelRect(x, y, Math.Max(left.Right, right.Right) - x, Math.Max(left.Bottom, right.Bottom) - y);
    }
}
