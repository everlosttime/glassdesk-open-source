using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GlassDesk.App;
using GlassDesk.App.Startup;
using GlassDesk.AppServices;
using GlassDesk.Core.Input;
using GlassDesk.Core.Models;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Storage;
using GlassDesk.Dock;
using GlassDesk.Launcher;
using GlassDesk.Platform.Displays;
using GlassDesk.Settings;
using GlassDesk.UI.Settings;
using GlassDesk.UI.Theming;

internal static class Program
{
    private const int WmHotkey = 0x0312;
    private static readonly List<object> Cases = [];
    private static readonly List<string> Screenshots = [];
    private static readonly JsonSerializerOptions JsonOptions = new()
    { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static int _failed;

    [STAThread]
    private static int Main(string[] args)
    {
        if (GlassDesk.UI.BackdropCaptureWorker.RunIfRequested(args, out var workerExit)) return workerExit;
        Cases.Clear();
        Screenshots.Clear();
        _failed = 0;
        if (!TryParse(args, out var report, out var suppliedData, out var argumentError))
        {
            Console.Error.WriteLine(argumentError);
            return 2;
        }
        var root = suppliedData is null ? Path.Combine(Path.GetTempPath(), "GlassDesk-P6-Wpf-" + Guid.NewGuid().ToString("N")) : Path.GetFullPath(suppliedData);
        var userRoot = AppPaths.ForCurrentUser().RootDirectory;
        if (DataDirectoryGuard.Overlaps(root, userRoot))
        {
            Console.Error.WriteLine("Refusing a data directory that overlaps the user's GlassDesk directory.");
            return 2;
        }
        if (DataDirectoryGuard.Overlaps(report, userRoot))
        {
            report = Path.Combine(Path.GetTempPath(), "GlassDesk-P6-refused-" + Guid.NewGuid().ToString("N") + ".json");
            Check("报告路径不会写入真实用户目录", false, "报告路径与用户数据隔离", "报告路径与用户数据重叠，改写到临时目录");
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/GlassDesk.UI;component/Theming/LiquidGlassTheme.xaml"),
        });
        var evidenceDirectory = Path.GetDirectoryName(Path.GetFullPath(report))!;
        Directory.CreateDirectory(evidenceDirectory);
        Directory.CreateDirectory(root);
        try
        {
            RunWpfAcceptance(app, root, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(report))!, "phase6-settings.png"));
        }
        catch (Exception ex)
        {
            Check("WPF验收Runner无未处理异常", false, "无异常", ex.ToString());
        }
        finally
        {
            ThemeService.Current?.Dispose();
            app.Shutdown();
            WriteReport(report, root);
            if (suppliedData is null && Directory.Exists(root))
            {
                try { Directory.Delete(root, true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        Console.WriteLine($"Phase 6 WPF: {Cases.Count - _failed} passed, {_failed} failed. Report: {Path.GetFullPath(report)}");
        return Cases.Count > 0 && _failed == 0 ? 0 : 1;
    }

    private static void RunWpfAcceptance(Application app, string root, string settingsScreenshot)
    {
        var paths = new AppPaths(root);
        paths.EnsureCreated();
        var data = new GlassDeskDataStore(paths);
        data.LoadAll();
        VerifyPhase6ReportPathProtection(root);
        data.Settings.Current.Launcher.Rows = 2;
        data.Settings.Current.Launcher.Columns = 2;
        data.Settings.Current.Display.DockDisplayId = null;
        data.Settings.Current.Display.SuppressTriggersWhenFullscreen = false;
        data.Settings.Save();
        for (var i = 0; i < 8; i++)
            data.Library.Add(new AppEntry { Id = $"p6-{i}", DisplayName = $"交互样本 {i}", Target = Path.Combine(root, $"missing-{i}"), IsTargetMissing = true });
        data.Library.Save();

        ThemeService.Initialize(app.Dispatcher).Update(data.Settings.Current.Appearance);
        using var services = new AppServiceHost(data);
        var primarySession = CapturePrimaryDisplaySession();
        var launcher = new LauncherWindow();
        launcher.BindPrimaryDisplay(primarySession);
        launcher.AttachServices(services, true);
        launcher.ApplySettings(data.Settings.Current.Launcher);
        SettingsWindow? settings = null;
        HwndSource? messageSource = null;
        InteractionController? controller = null;
        var actions = new List<InputAction>();
        var dispatchedIds = new List<int>();
        try
        {
            var sourceParameters = new HwndSourceParameters("GlassDesk.Phase6Acceptance.MessageWindow")
            {
                Width = 0, Height = 0, ParentWindow = new nint(-3), WindowStyle = 0,
            };
            messageSource = new HwndSource(sourceParameters);
            messageSource.AddHook((nint hwnd, int message, nint wParam, nint lParam, ref bool handled) =>
            {
                if (message == WmHotkey && controller is not null)
                {
                    dispatchedIds.Add(wParam.ToInt32());
                    controller.HandleHotkey(wParam.ToInt32());
                    handled = true;
                }
                return 0;
            });
            controller = new InteractionController(data, primarySession, messageSource.Handle, app.Dispatcher,
                action => action is InputAction.ToggleLauncher || launcher.IsVisible,
                action =>
                {
                    actions.Add(action);
                    switch (action)
                    {
                        case InputAction.ToggleLauncher:
                            if (launcher.IsVisible) launcher.Hide(); else { launcher.PrepareToShow(); launcher.Show(); }
                            break;
                        case InputAction.HideLauncher: launcher.Hide(); break;
                        case InputAction.NextPage: launcher.NavigatePage(1); break;
                        case InputAction.PreviousPage: launcher.NavigatePage(-1); break;
                        // Deliberately do not toggle Explorer: session-level Shell mutation is outside this WPF runner.
                    }
                    controller?.SetLauncherAvailability(launcher.IsVisible, launcher.IsVisible);
                }, message => { });
            Check("协调器初始化", controller.Initialize() is null, "初始化成功", "Initialize已调用");
            controller.SetLauncherAvailability(false, false);

            var desktopWasReadable = controller.ReadDesktopIcons().HasValue;
            var shortcuts = new ShortcutSettings
            {
                ToggleLauncher = Hotkey(13), HideLauncher = Hotkey(14), NextPage = Hotkey(15), PreviousPage = Hotkey(16),
                ToggleDesktopIcons = desktopWasReadable ? Hotkey(17) : new HotkeyDefinition(),
            };
            var applyError = controller.Apply(shortcuts, data.Settings.Current.Display);
            Check("五类操作配置通过生产设置服务", applyError is null
                && data.Settings.Current.Shortcuts.ToggleLauncher.Key == "F13"
                && data.Settings.Current.Shortcuts.HideLauncher.Key == "F14"
                && data.Settings.Current.Shortcuts.NextPage.Key == "F15"
                && data.Settings.Current.Shortcuts.PreviousPage.Key == "F16"
                && data.Settings.Current.Shortcuts.ToggleDesktopIcons.Enabled == desktopWasReadable,
                "四个窗口动作注册，桌面动作仅在能力可读时注册", applyError ?? $"桌面图标能力可读={desktopWasReadable}");
            if (!desktopWasReadable)
            {
                var before = File.ReadAllBytes(paths.SettingsFile);
                var requested = SettingsPageEditor.Clone(data.Settings.Current).Shortcuts;
                requested.ToggleDesktopIcons = Hotkey(17);
                var unsupported = controller.Apply(requested, data.Settings.Current.Display);
                Check("桌面图标能力未知时拒绝相关快捷键", unsupported is not null
                    && before.SequenceEqual(File.ReadAllBytes(paths.SettingsFile)),
                    "应用返回错误且磁盘设置不变", unsupported ?? "未返回错误");
            }
            var initialReload = new GlassDeskDataStore(paths);
            initialReload.LoadAll();
            Check("五类操作配置经新数据实例重载", initialReload.Settings.Current.Shortcuts.ToggleLauncher.Key == "F13"
                && initialReload.Settings.Current.Shortcuts.PreviousPage.Key == "F16"
                && initialReload.Settings.Current.Shortcuts.ToggleDesktopIcons.Enabled == desktopWasReadable,
                "热键设置重载与已应用值一致", $"Toggle={initialReload.Settings.Current.Shortcuts.ToggleLauncher.Key}; DesktopEnabled={initialReload.Settings.Current.Shortcuts.ToggleDesktopIcons.Enabled}");

            launcher.Show();
            Pump(TimeSpan.FromMilliseconds(120));
            controller.SetLauncherAvailability(true, true);
            var pageBefore = PageText(launcher);
            PostHotkeys(messageSource.Handle, [0x1000]);
            Pump(TimeSpan.FromMilliseconds(80));
            Check("真实WM_HOTKEY触发生产Toggle窗口动作", !launcher.IsVisible && actions.LastOrDefault() == InputAction.ToggleLauncher,
                "启动台变为隐藏", $"Visible={launcher.IsVisible}; action={actions.LastOrDefault()}");

            PostHotkeys(messageSource.Handle, [0x1001]);
            Pump(TimeSpan.FromMilliseconds(80));
            Check("启动台隐藏时Hide动作不消费", !launcher.IsVisible && actions.Count == 1,
                "无新的动作执行", $"Visible={launcher.IsVisible}; actions={actions.Count}");
            PostHotkeys(messageSource.Handle, [0x1002]);
            Pump(TimeSpan.FromMilliseconds(80));
            Check("启动台隐藏时NextPage动作不消费", !launcher.IsVisible && actions.Count == 1 && PageText(launcher) == pageBefore,
                "动作列表与页码不变", $"actions={actions.Count}; page={PageText(launcher)}");

            launcher.PrepareToShow();
            launcher.Show();
            Pump(TimeSpan.FromMilliseconds(100));
            controller.SetLauncherAvailability(true, true);
            var beforeNext = PageText(launcher);
            PostHotkeys(messageSource.Handle, [0x1002]);
            Pump(TimeSpan.FromMilliseconds(80));
            var afterNext = PageText(launcher);
            Check("真实WM_HOTKEY执行生产NextPage翻页", actions.LastOrDefault() == InputAction.NextPage
                && afterNext != beforeNext && afterNext.EndsWith("/ 2", StringComparison.Ordinal),
                "生产导航按钮逻辑到第2页", $"{beforeNext}→{afterNext}");
            PostHotkeys(messageSource.Handle, [0x1003]);
            Pump(TimeSpan.FromMilliseconds(80));
            Check("真实WM_HOTKEY执行生产PreviousPage翻页", actions.LastOrDefault() == InputAction.PreviousPage
                && PageText(launcher).StartsWith("1 / ", StringComparison.Ordinal),
                "生产导航按钮逻辑返回第1页", PageText(launcher));

            if (desktopWasReadable)
            {
                PostHotkeys(messageSource.Handle, [0x1004]);
                Pump(TimeSpan.FromMilliseconds(80));
                Check("真实WM_HOTKEY到达桌面图标生产动作路由", actions.LastOrDefault() == InputAction.ToggleDesktopIcons
                    && dispatchedIds.Contains(0x1004),
                    "WM_HOTKEY进入ToggleDesktopIcons动作映射", $"action={actions.LastOrDefault()}；Shell切换未执行");
            }

            VerifyHotkeyConflict(app, launcher, paths);

            settings = new SettingsWindow();
            settings.BindPrimaryDisplay(primarySession);
            settings.AttachDataStore(data, null);
            settings.InteractionSettingsApply = (shortcutsToApply, display) =>
            {
                return controller.Apply(shortcutsToApply, display);
            };
            settings.PositionOnPrimary();
            settings.Show();
            Pump(TimeSpan.FromMilliseconds(100));
            VerifySettingsNavigation(settings, data, paths);
            VerifyGlassTransparency(settings, data, paths, Path.Combine(Path.GetDirectoryName(settingsScreenshot)!, "glass-transparency.png"));
            var previousPointerSetting = data.Settings.Current.Display.LauncherFollowsPointer;
            RecordShortcut(settings, "ToggleLauncherHotkeyBox", Key.F13, ModifierKeys.Control);
            Check("快捷操作录制自动保存且保留旧显示器设置", data.Settings.Current.Display.LauncherFollowsPointer == previousPointerSetting
                && Reload(paths).Settings.Current.Display.LauncherFollowsPointer == previousPointerSetting
                && !Reload(paths).Settings.Current.Display.SuppressTriggersWhenFullscreen,
                "快捷键自动保存，跟随指针和全屏触发兼容字段保持原值",
                $"pointer={Reload(paths).Settings.Current.Display.LauncherFollowsPointer}; suppress={Reload(paths).Settings.Current.Display.SuppressTriggersWhenFullscreen}");

            var rows = data.Settings.Current.Launcher.Rows;
            data.Settings.Current.Shortcuts.HotCornerDwellMs = 777;
            data.Settings.Save();
            ((ListBox)settings.FindName("SettingsPageList")!).SelectedIndex = 3;
            ((Button)settings.FindName("ResetPageButton")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("Settings生产单页恢复默认保留其它页", data.Settings.Current.Launcher.Rows == rows
                && data.Settings.Current.Shortcuts.HotCornerDwellMs == 300,
                $"启动台行数保留{rows}、热角停留重置300", $"rows={data.Settings.Current.Launcher.Rows}; dwell={data.Settings.Current.Shortcuts.HotCornerDwellMs}");

            Pump(TimeSpan.FromMilliseconds(80));
            Capture(settings, settingsScreenshot);
            Screenshots.Add(Path.GetFullPath(settingsScreenshot));

            var bytesBeforeInvalid = File.ReadAllBytes(paths.SettingsFile);
            ((ShortcutRecorder)settings.FindName("ToggleLauncherHotkeyBox")!).Text = "invalid";
            RecordShortcut(settings, "NextPageHotkeyBox", Key.F15, ModifierKeys.Control);
            Check("Settings生产路由拒绝无效值且不写盘", bytesBeforeInvalid.SequenceEqual(File.ReadAllBytes(paths.SettingsFile))
                && !string.IsNullOrEmpty(((TextBlock)settings.FindName("InteractionSettingsStatus")!).Text),
                "settings.json字节不变并显示范围错误", ((TextBlock)settings.FindName("InteractionSettingsStatus")!).Text);

            VerifySaveFailure(settings, data, paths, controller);

            var bounds = DisplayService.GetWindowPixelBounds(new WindowInteropHelper(launcher).Handle);
            var workArea = primarySession.Display.WorkArea;
            var contained = bounds is { } b && workArea.Contains(b.X, b.Y)
                && b.Right <= workArea.Right && b.Bottom <= workArea.Bottom;
            Check("真实启动台物理窗口矩形位于启动时主显示器工作区", contained,
                "完整窗口边界落入Primary Monitor工作区", bounds?.ToString() ?? "无法读取物理边界");

            launcher.Hide();
            VerifyDockLauncherClickToggles(services, launcher, primarySession, Path.GetDirectoryName(settingsScreenshot)!);
        }
        finally
        {
            controller?.Dispose();
            settings?.Close();
            launcher.Close();
            messageSource?.Dispose();
        }
    }

    private static void VerifyDockLauncherClickToggles(AppServiceHost services, LauncherWindow launcher,
        PrimaryDisplaySession primarySession, string evidenceDirectory)
    {
        var settings = DockSettingsHost.Initialize(Application.Current.Dispatcher);
        var dock = new DockWindow();
        dock.BindPrimaryDisplay(primarySession);
        PixelRect? lastSourceBounds = null;
        var openRequests = 0;
        void OpenLauncher(PixelRect? sourceBounds)
        {
            openRequests++;
            lastSourceBounds = sourceBounds;
            launcher.RefreshDiagnostics();
            dock.SetLauncherContainerState(true);
            launcher.ShowWithTransition(sourceBounds ?? dock.GetLauncherButtonPixelBounds());
            launcher.Activate();
        }

        try
        {
            launcher.ContainerAnchorProvider = dock.GetLauncherButtonPixelBounds;
            launcher.IsVisibleChanged += OnVisibility;
            dock.AttachServices(services, settings,
                sourceBounds => AppHost.ToggleLauncherFromDock(launcher, sourceBounds, OpenLauncher));
            dock.Show();
            Pump(TimeSpan.FromMilliseconds(120));
            var button = (Button)dock.FindName("LauncherButton")!;
            var liveBounds = dock.GetLauncherButtonPixelBounds();
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(TimeSpan.FromMilliseconds(750));
            Check("Dock启动台按钮按实时位置打开", launcher.IsVisible && launcher.CanNavigatePages
                && lastSourceBounds == liveBounds,
                "启动台打开且动画起点等于Dock按钮当前矩形", $"Visible={launcher.IsVisible}; source={lastSourceBounds}; dock={liveBounds}");

            var iconImage = new RenderTargetBitmap(48, 64, 96, 96, PixelFormats.Pbgra32);
            iconImage.Render(button);
            var iconPixels = new byte[48 * 64 * 4];
            iconImage.CopyPixels(iconPixels, 48 * 4, 0);
            var visibleIconPixels = Enumerable.Range(0, 48 * 64).Count(index => iconPixels[index * 4 + 3] >= 128);
            Check("启动台展开后Dock按钮图标保持可见且可以点击", visibleIconPixels > 100 && button.IsEnabled && button.IsHitTestVisible,
                "展开后仍绘制启动台图标并接受点击", $"可见图标像素={visibleIconPixels}; Enabled={button.IsEnabled}; HitTest={button.IsHitTestVisible}");
            var dockScreenshot = Path.Combine(evidenceDirectory, "dock-launcher-button-expanded.png");
            Capture(dock, dockScreenshot);
            Screenshots.Add(Path.GetFullPath(dockScreenshot));

            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(TimeSpan.FromMilliseconds(750));
            Check("启动台已打开时再次点击Dock图标反向收回", !launcher.IsVisible,
                "启动台沿原过渡缩回Dock按钮并隐藏", $"Visible={launcher.IsVisible}");

            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(TimeSpan.FromMilliseconds(750));
            var requestsBeforeClose = openRequests;
            // Deactivation may start closing before the Dock's mouse-up Click.
            // That second click must complete the close, rather than reopen it.
            launcher.CloseWithTransition();
            var wasClosing = launcher.IsClosingContainer;
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(TimeSpan.FromMilliseconds(750));
            Check("失焦已开始收起时Dock点击不会反向重新打开", wasClosing && !launcher.IsVisible && openRequests == requestsBeforeClose,
                "继续收起直到隐藏，不再请求打开", $"此前收起={wasClosing}; Visible={launcher.IsVisible}; 打开请求={requestsBeforeClose}->{openRequests}");
        }
        finally
        {
            launcher.Hide();
            launcher.IsVisibleChanged -= OnVisibility;
            launcher.ContainerAnchorProvider = null;
            dock.Close();
        }

        void OnVisibility(object sender, DependencyPropertyChangedEventArgs e) => dock.SetLauncherContainerState(e.NewValue is true);
    }

    private static void VerifyHotkeyConflict(Application app, LauncherWindow launcher, AppPaths mainPaths)
    {
        var paths = new AppPaths(Path.Combine(mainPaths.RootDirectory, "native-transaction"));
        var data = new GlassDeskDataStore(paths);
        data.LoadAll();
        using var source = new HwndSource(new HwndSourceParameters("GlassDesk.Phase6.Conflict")
        { ParentWindow = new nint(-3), Width = 0, Height = 0, WindowStyle = 0 });
        InteractionController? controller = null;
        using var competitor = new HwndSource(new HwndSourceParameters("GlassDesk.Phase6.Competitor")
        { ParentWindow = new nint(-3), Width = 0, Height = 0, WindowStyle = 0 });
        controller = new InteractionController(data, CapturePrimaryDisplaySession(), source.Handle, app.Dispatcher,
            action => action == InputAction.ToggleLauncher || launcher.IsVisible,
            action =>
            {
                if (action != InputAction.ToggleLauncher) return;
                if (launcher.IsVisible) launcher.Hide(); else launcher.Show();
            }, _ => { });
        var received = 0;
        source.AddHook((nint hwnd, int message, nint wParam, nint lParam, ref bool handled) =>
        {
            if (message == WmHotkey && controller is not null)
            {
                received++;
                controller.HandleHotkey(wParam.ToInt32());
                handled = true;
            }
            return 0;
        });
        _ = controller.Initialize();
        var baseline = new ShortcutSettings { ToggleLauncher = Hotkey(18) };
        var initialError = controller.Apply(baseline, data.Settings.Current.Display);
        var baselineBytes = File.ReadAllBytes(paths.SettingsFile);
        using var registry = new NativeHotkeyRegistry(competitor.Handle);
        var contested = Hotkey(19);
        _ = HotkeyText.TryGetChord(contested, out var chord, out _);
        var acquired = registry.TryRegister(88, chord, out var osError);
        var candidate = new ShortcutSettings { ToggleLauncher = contested };
        var rejected = acquired ? controller.Apply(candidate, data.Settings.Current.Display) : "本机未能建立竞争注册，Win32错误=" + osError;
        Check("真实RegisterHotKey冲突保留旧注册、设置和生产动作", initialError is null && acquired && rejected is not null
            && data.Settings.Current.Shortcuts.ToggleLauncher.Key == "F18"
            && baselineBytes.SequenceEqual(File.ReadAllBytes(paths.SettingsFile)),
            "第二HWND注册冲突；设置内存、磁盘与原热键保持Ctrl+F18", $"初次应用={initialError ?? "成功"}; 冲突结果={rejected ?? "无错误"}");
        launcher.Hide();
        if (acquired) registry.Unregister(88);
        PostHotkeys(source.Handle, [0x1000]);
        Pump(TimeSpan.FromMilliseconds(80));
        Check("冲突后旧注册仍驱动生产窗口动作", received == 1 && launcher.IsVisible,
            "旧Ctrl+F18消息到达并显示启动台", $"消息数={received}; 窗口可见={launcher.IsVisible}");
        var afterRelease = controller.Apply(candidate, data.Settings.Current.Display);
        Check("解除真实注册冲突后相同组合可注册", afterRelease is null
            && data.Settings.Current.Shortcuts.ToggleLauncher.Key == "F19",
            "Ctrl+F19已应用并持久化", afterRelease ?? "第二HWND释放后生产服务注册成功");
        controller.Dispose();
        controller = null;
        using var afterClose = new NativeHotkeyRegistry(source.Handle);
        _ = HotkeyText.TryGetChord(contested, out chord, out _);
        var canRegisterAfterClose = afterClose.TryRegister(90, chord, out var closeError);
        Check("协调器关闭后释放相同热键注册", canRegisterAfterClose,
            "同一HWND可再次注册Ctrl+F19", canRegisterAfterClose ? "注册成功" : $"Win32错误={closeError}");
        if (canRegisterAfterClose) afterClose.Unregister(90);
        controller?.Dispose();
    }

    private static void VerifySaveFailure(SettingsWindow settings, GlassDeskDataStore data, AppPaths paths, InteractionController controller)
    {
        var oldKey = data.Settings.Current.Shortcuts.ToggleLauncher.Key;
        var before = File.ReadAllBytes(paths.SettingsFile);
        settings.InteractionSettingsApply = (shortcuts, display) =>
        {
            using var locked = new FileStream(paths.SettingsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return controller.Apply(shortcuts, display);
        };
        RecordShortcut(settings, "ToggleLauncherHotkeyBox", Key.F23, ModifierKeys.Control | ModifierKeys.Alt);
        var status = ((TextBlock)settings.FindName("InteractionSettingsStatus")!).Text;
        Check("Settings生产应用遇到真实文件独占锁时回滚", data.Settings.Current.Shortcuts.ToggleLauncher.Key == oldKey
            && before.SequenceEqual(File.ReadAllBytes(paths.SettingsFile))
            && status.Contains("保存失败，已恢复原设置及快捷键：", StringComparison.Ordinal),
            "共享设置和磁盘字节回到保存前状态", $"跟随指针={data.Settings.Current.Display.LauncherFollowsPointer}; status={((TextBlock)settings.FindName("InteractionSettingsStatus")!).Text}");
    }

    private static void VerifyPhase6ReportPathProtection(string root)
    {
        var protectedRoot = Path.Combine(root, "protected-user-data");
        var protectedPaths = new AppPaths(protectedRoot);
        protectedPaths.EnsureCreated();
        const string sentinel = "preserve-settings-sentinel";
        File.WriteAllText(protectedPaths.SettingsFile, sentinel);
        var error = new StringWriter();
        var previous = Console.Error;
        int exitCode;
        try
        {
            Console.SetError(error);
            exitCode = Phase6Acceptance.Run(protectedPaths,
                [protectedPaths.SettingsFile, Path.Combine(root, "safe-acceptance-data")]);
        }
        finally
        {
            Console.SetError(previous);
        }

        Check("阶段六 CLI 拒绝将报告写入用户数据目录", exitCode == 2
            && File.ReadAllText(protectedPaths.SettingsFile) == sentinel
            && error.ToString().Contains("报告路径", StringComparison.Ordinal),
            "返回 2、设置文件字节不变并报告拒绝原因",
            $"exit={exitCode}; sentinelPreserved={File.ReadAllText(protectedPaths.SettingsFile) == sentinel}; stderr={error}");
    }

    private static GlassDeskDataStore Reload(AppPaths paths)
    {
        var result = new GlassDeskDataStore(paths);
        result.LoadAll();
        return result;
    }

    private static HotkeyDefinition Hotkey(int functionKey) => new()
    { Enabled = true, Control = true, Key = "F" + functionKey.ToString(System.Globalization.CultureInfo.InvariantCulture) };

    private static void PostHotkeys(nint hwnd, IEnumerable<int> ids)
    {
        foreach (var id in ids)
            if (!PostMessage(hwnd, WmHotkey, new nint(id), 0))
                throw new InvalidOperationException($"PostMessage(WM_HOTKEY,{id}) failed: {Marshal.GetLastWin32Error()}");
    }

    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint hwnd, int message, nint wParam, nint lParam);

    private static void VerifyGlassTransparency(SettingsWindow settings, GlassDeskDataStore data, AppPaths paths, string screenshot)
    {
        var sampling = GlassDesk.UI.Controls.LiquidGlassDebugState.SamplingParameters;
        var weights = GlassDesk.UI.Controls.LiquidGlassDebugState.WeightParameters;
        var brdf = GlassDesk.UI.Controls.LiquidGlassDebugState.BrdfParameters;
        var dispersion = GlassDesk.UI.Controls.LiquidGlassDebugState.DispersionParameters;
        foreach (var (controlName, launcher) in new[] { ("DockTransparencySlider", false), ("LauncherTransparencySlider", true) })
        {
            double Read(GlassDesk.Core.Models.Settings.AppearanceSettings appearance) => launcher
                ? appearance.LauncherGlassTransparency : appearance.DockGlassTransparency;
            double ReadOther(GlassDesk.Core.Models.Settings.AppearanceSettings appearance) => launcher
                ? appearance.DockGlassTransparency : appearance.LauncherGlassTransparency;
            var otherBefore = ReadOther(data.Settings.Current.Appearance);
            var slider = (Slider)settings.FindName(controlName)!;
            foreach (var transparency in new[] { 1d, 0d, .5 })
            {
                slider.Value = transparency;
                Pump(TimeSpan.FromMilliseconds(100));
                var expected = transparency;
                Check($"玻璃透明度{transparency:P0}实时应用并保存",
                    Math.Abs(Read(data.Settings.Current.Appearance) - expected) < 1e-9
                    && Math.Abs(Read(ThemeService.Current!.Appearance) - expected) < 1e-9
                    && Math.Abs(Read(Reload(paths).Settings.Current.Appearance) - expected) < 1e-9
                    && ReadOther(data.Settings.Current.Appearance) == otherBefore
                    && settings.Opacity == 1,
                    "两窗口透明度独立保存，窗口Opacity保持1", $"control={controlName}; transparency={Read(data.Settings.Current.Appearance)}; opacity={settings.Opacity}");
            }
            Check("透明度滑块保留Face Inner Outer与光学参数",
                sampling == GlassDesk.UI.Controls.LiquidGlassDebugState.SamplingParameters
                && weights == GlassDesk.UI.Controls.LiquidGlassDebugState.WeightParameters
                && brdf == GlassDesk.UI.Controls.LiquidGlassDebugState.BrdfParameters
                && dispersion == GlassDesk.UI.Controls.LiquidGlassDebugState.DispersionParameters,
                "采样、权重、高光和色散保持原值", "已比较滑块调节前后参数");
            var before = File.ReadAllBytes(paths.SettingsFile);
            var previousClarity = Read(data.Settings.Current.Appearance);
            using (var locked = new FileStream(paths.SettingsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                slider.Value = .75;
            Check("透明度保存失败时恢复滑块与实时预览",
                Read(data.Settings.Current.Appearance) == previousClarity
                && Read(ThemeService.Current!.Appearance) == previousClarity
                && Math.Abs(slider.Value - previousClarity) < 1e-9
                && before.SequenceEqual(File.ReadAllBytes(paths.SettingsFile)),
                "设置、预览、滑块和磁盘均恢复原值", $"stored={Read(data.Settings.Current.Appearance)}; live={Read(ThemeService.Current!.Appearance)}");
        }
        ((ListBox)settings.FindName("SettingsPageList")!).SelectedIndex = 0;
        Pump(TimeSpan.FromMilliseconds(200));
        Capture(settings, screenshot);
        Screenshots.Add(Path.GetFullPath(screenshot));
    }

    private static void RecordShortcut(SettingsWindow settings, string name, Key key, ModifierKeys modifiers)
    {
        var recorder = (ShortcutRecorder)settings.FindName(name)!;
        recorder.BeginRecording();
        recorder.CaptureKey(key, modifiers);
    }

    private static void VerifySettingsNavigation(SettingsWindow settings, GlassDeskDataStore data, AppPaths paths)
    {
        var list = (ListBox)settings.FindName("SettingsPageList")!;
        var scroll = (ScrollViewer)settings.FindName("SettingsScroll")!;
        var reset = (Button)settings.FindName("ResetPageButton")!;
        var sections = new (string page, string heading)[]
        {
            ("外观", "AppearanceSectionHeading"), ("Dock", "DockSectionHeading"),
            ("启动台", "LauncherSectionHeading"), ("快捷操作", "ShortcutsSectionHeading"),
            ("桌面", "DesktopSectionHeading"), ("备份恢复", "BackupSectionHeading"),
            ("赞助支持", "SponsorshipSectionHeading"),
            ("关于", "AboutSectionHeading"),
        };
        foreach (var (page, headingName) in sections.Concat(sections.Reverse()))
        {
            var heading = (FrameworkElement)settings.FindName(headingName)!;
            var target = scroll.VerticalOffset + heading.TranslatePoint(new Point(), scroll).Y - 12;
            scroll.ScrollToVerticalOffset(Math.Max(0, target));
            Pump(TimeSpan.FromMilliseconds(70));
            var offset = scroll.VerticalOffset;
            Check($"设置滚动同步分类：{page}，offset={offset:0}", Equals(list.SelectedItem, page),
                page, list.SelectedItem?.ToString() ?? "未选择");
            Pump(TimeSpan.FromMilliseconds(40));
            Check($"设置同步高亮不反向跳转：{page}，offset={offset:0}", Math.Abs(scroll.VerticalOffset - offset) < .5,
                "滚动位置保持", $"{offset:0.0}→{scroll.VerticalOffset:0.0}");
        }
        scroll.ScrollToBottom();
        Pump(TimeSpan.FromMilliseconds(70));
        Check("设置滚到底部选中关于且禁用恢复默认", Equals(list.SelectedItem, "关于") && !reset.IsEnabled,
            "关于、禁用恢复默认", $"{list.SelectedItem}; reset={reset.IsEnabled}");

        foreach (var page in new[] { "Dock", "启动台", "快捷操作", "桌面", "备份恢复", "赞助支持", "关于", "外观" })
        {
            list.SelectedItem = page;
            Pump(TimeSpan.FromMilliseconds(90));
            var heading = (FrameworkElement)settings.FindName(sections.Single(s => s.page == page).heading)!;
            var y = heading.TranslatePoint(new Point(), scroll).Y;
            Check($"设置点击分类正确定位并稳定高亮：{page}", Equals(list.SelectedItem, page)
                && (Math.Abs(y - 12) < 1 || (page == "外观" && scroll.VerticalOffset < .5)
                    || Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < .5),
                "所选分类标题定位到顶部或滚动边界", $"{list.SelectedItem}; headingY={y:0.0}; offset={scroll.VerticalOffset:0.0}");
        }
        var dockHeading = (FrameworkElement)settings.FindName("DockSectionHeading")!;
        scroll.ScrollToVerticalOffset(scroll.VerticalOffset + dockHeading.TranslatePoint(new Point(), scroll).Y - 24);
        Pump(TimeSpan.FromMilliseconds(70));
        var beforeWheel = scroll.VerticalOffset;
        scroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
            { RoutedEvent = Mouse.MouseWheelEvent });
        Pump(TimeSpan.FromMilliseconds(70));
        Check("设置滚轮跨过标题时同步分类且不回跳", Equals(list.SelectedItem, "Dock")
            && scroll.VerticalOffset > beforeWheel,
            "滚轮向下跨过Dock标题并选中Dock", $"{list.SelectedItem}; {beforeWheel:0.0}→{scroll.VerticalOffset:0.0}");
        var oldHeight = settings.Height;
        settings.Height = 480;
        Pump(TimeSpan.FromMilliseconds(70));
        foreach (var (page, headingName) in sections)
        {
            var heading = (FrameworkElement)settings.FindName(headingName)!;
            scroll.ScrollToVerticalOffset(scroll.VerticalOffset + heading.TranslatePoint(new Point(), scroll).Y - 12);
            Pump(TimeSpan.FromMilliseconds(70));
            Check($"设置尺寸变化后滚动分类仍对应：{page}", Equals(list.SelectedItem, page), page,
                list.SelectedItem?.ToString() ?? "未选择");
        }
        settings.Height = oldHeight;
        Pump(TimeSpan.FromMilliseconds(70));
        list.SelectedItem = "Dock";
        list.SelectedItem = "关于";
        list.SelectedItem = "桌面";
        Pump(TimeSpan.FromMilliseconds(100));
        Check("连续切换分类只定位最后一个目标", Equals(list.SelectedItem, "桌面")
            && Math.Abs(((FrameworkElement)settings.FindName("DesktopSectionHeading")!).TranslatePoint(new Point(), scroll).Y - 12) < 1,
            "桌面保持选中且定位正确", list.SelectedItem?.ToString() ?? "未选择");
        var apply = settings.InteractionSettingsApply;
        var applyCalls = 0;
        settings.InteractionSettingsApply = (shortcuts, display) => { applyCalls++; return apply?.Invoke(shortcuts, display); };
        try
        {
            list.SelectedItem = "桌面";
            Pump(TimeSpan.FromMilliseconds(90));
            var before = data.Settings.Current.Shortcuts.ToggleLauncher.Key;
            reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("桌面恢复默认不进入快捷操作或显示器应用路由", reset.IsEnabled && applyCalls == 0
                && Reload(paths).Settings.Current.Shortcuts.ToggleLauncher.Key == before,
                "桌面可恢复且不调用快捷操作服务", $"enabled={reset.IsEnabled}; applyCalls={applyCalls}");
            list.SelectedItem = "备份恢复";
            Pump(TimeSpan.FromMilliseconds(90));
            var bytes = File.ReadAllBytes(paths.SettingsFile);
            reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("备份恢复分类禁止页默认恢复及写盘", !reset.IsEnabled && applyCalls == 0
                && bytes.SequenceEqual(File.ReadAllBytes(paths.SettingsFile)),
                "按钮禁用且数据不变", $"enabled={reset.IsEnabled}; applyCalls={applyCalls}");
        }
        finally { settings.InteractionSettingsApply = apply; }
    }

    private static string PageText(LauncherWindow window) => ((TextBlock)window.FindName("PageText")!).Text;

    private static bool TryParse(string[] args, out string report, out string? dataDirectory, out string error)
    {
        report = Path.Combine(Environment.CurrentDirectory, "docs", "evidence", "阶段6", "phase6-wpf.json");
        dataDirectory = null;
        error = string.Empty;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--report" or "--data-dir")
            {
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                { error = $"Missing value for {args[i]}. Usage: --report <json> [--data-dir <temp>]"; return false; }
                if (args[i] == "--report") report = Path.GetFullPath(args[++i]);
                else dataDirectory = Path.GetFullPath(args[++i]);
                continue;
            }
            error = $"Unknown argument: {args[i]}. Usage: --report <json> [--data-dir <temp>]";
            return false;
        }
        return true;
    }

    private static void Check(string name, bool okay, string expected, string actual)
    {
        if (!okay) _failed++;
        Cases.Add(new { name, driver = "production WPF routes / native registration / isolated files", expected, actual,
            status = okay ? "Passed" : "Failed", timestamp = DateTimeOffset.Now });
    }

    private static PrimaryDisplaySession CapturePrimaryDisplaySession() =>
        new(DisplayService.Primary() ?? throw new InvalidOperationException("Windows Primary Monitor is unavailable."));

    private static void WriteReport(string report, string root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
        File.WriteAllText(report, JsonSerializer.Serialize(new
        {
            timestamp = DateTimeOffset.Now,
            artifact = typeof(InteractionController).Assembly.FullName,
            environment = Environment.OSVersion.ToString(),
            machine = Environment.MachineName,
            dataDirectory = root,
            driver = "real WPF windows and controls; this process's RegisterHotKey/WM_HOTKEY; no system input injection",
            screenshots = Screenshots,
            supersedes = "phase6-wpf-initial.json",
            correctionsSinceInitialRun = new[]
            {
                "首次报告的隐藏翻页失败来自Runner仍将canNavigate传true；现Runner将隐藏状态同步为false。",
                "首次报告的上一页失败来自断言误判：实际页码1 / 2曾被错误识别为非第1页；现检查明确以1 /开头。",
                "首次报告的写盘失败项实际停留在无效输入校验，未尝试保存；现先恢复有效停留值，再锁定settings.json并要求报告包含保存失败详情。",
            },
            notRun = new[] { "真实键盘/鼠标侧键/热角指针输入", "Explorer桌面图标切换", "显示器物理拔插" },
            passed = Cases.Count - _failed,
            failed = _failed,
            exitCode = _failed == 0 && Cases.Count > 0 ? 0 : 1,
            cases = Cases,
        }, JsonOptions));
    }

    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var output = File.Create(path);
        encoder.Save(output);
    }
}
