using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using GlassDesk.App.Startup;
using GlassDesk.App.Tray;
using GlassDesk.AppServices;
using GlassDesk.Core.Models;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Storage;
using GlassDesk.Dock;
using GlassDesk.Launcher;
using GlassDesk.Platform;
using GlassDesk.Platform.Displays;
using GlassDesk.Platform.Installation;
using GlassDesk.Platform.Startup;
using GlassDesk.Platform.Storage;
using GlassDesk.Settings;
using GlassDesk.UI.Settings;
using GlassDesk.UI.Theming;
// WPF 自带 System.Windows.ThemeMode，与设置模型里的同名枚举冲突，用别名固定到我们自己那个。
using ThemeMode = GlassDesk.Core.Models.ThemeMode;

namespace GlassDesk.App;

/// <summary>
/// 掌管应用的整个生命周期：单实例、数据、主题、托盘以及三个窗口。
/// </summary>
/// <remarks>
/// 一切与操作系统打交道的东西都在这里创建，再交给各个窗口，
/// 这样窗口就不会自己去取路径、句柄或注册表。
/// </remarks>
internal sealed partial class AppHost : IDisposable
{
    /// <summary>
    /// 本产品的实例标识。
    /// </summary>
    /// <remarks>
    /// 取名时特意避开 MacDock 的 <c>MacDock_SingleInstance_7E3A9B1F</c>，
    /// 也避开启动台的 Electron 锁，好让 GlassDesk
    /// 能与已装好的其中任何一个同时运行。
    /// </remarks>
    private const string InstanceName = "GlassDesk.Host";

    /// <summary>用于接收托盘消息的隐藏窗口的窗口类名。</summary>
    private const string MessageWindowName = "GlassDesk.TrayMessageWindow";

    /// <summary>HWND_MESSAGE：一种只为接收消息而存在的窗口。</summary>
    private static readonly nint MessageOnlyWindow = new(-3);

    private readonly Application _application;
    private readonly CommandLineOptions _options;

    private SingleInstanceGuard? _guard;
    private UserDataLease? _userDataLease;
    private GlassDeskDataStore? _data;
    private AppServiceHost? _services;
    private DataLoadReport? _loadReport;
    private HwndSource? _messageWindow;
    private TrayIcon? _tray;
    private nint _trayIconHandle;
    private MissingTargetReport? _missingReport;
    private PrimaryDisplaySession? _primaryDisplaySession;
    private IDisposable? _backdropDiagnostics;

    private DockWindow? _dock;
    private LauncherWindow? _launcher;
    private SettingsWindow? _settings;
    private DockSettingsHost? _dockSettings;
    private bool _shuttingDown;
    private bool _disposed;
    private readonly DeferredCommandGate _deferredCommandGate = new();

    public AppHost(Application application, CommandLineOptions options)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(options);

        _application = application;
        _options = options;
    }

    /// <summary>
    /// 把产品启动起来。
    /// </summary>
    /// <returns>本次启动的结果，决定宿主进程接下来该做什么。</returns>
    public StartupOutcome Start()
    {
        _guard = SingleInstanceGuard.Acquire(InstanceName);

        if (!_guard.IsFirstInstance)
        {
            // UI 归另一个实例所有。把本次启动的意图转交过去，然后让位。
            HandOffToRunningInstance();
            return StartupOutcome.HandedOffToRunningInstance;
        }

        // 新版首实例也持有旧版使用的互斥体名称，阻止旧版随后启动并认为自己是首实例。
        // 此时还没有加载或创建用户数据；若旧锁已存在或无法确认，释放新版锁后安全退出。
        if (!_guard.TryAcquireLegacyProtocolLease(out var legacyStatus))
        {
            _guard.Dispose();
            _guard = null;

            MessageBox.Show(
                StartupIntent.GetLegacyInstanceBlockMessage(legacyStatus),
                "GlassDesk 启动受阻",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return StartupOutcome.LegacyInstanceBlocked;
        }

        // 首个实例，但本次启动只要求「退出」。
        //
        // --exit 的语义是「让正在运行的那个退出」。既然没有别的实例，就没有任何事可做；
        // 这里曾经会继续把整个产品启动起来，于是没有实例时执行 --exit 反而弹出了程序坞，
        // 与用户的要求正好相反。
        if (StartupIntent.ShouldExitWithoutExistingInstance(_options))
        {
            return StartupOutcome.NothingToDo;
        }

        if (!UserDataLease.TryAcquireShared(AppPaths.ForCurrentUser().RootDirectory, out _userDataLease))
        {
            MessageBox.Show(
                "无法取得 GlassDesk 用户数据的运行租约。数据可能正在维护，或当前文件夹不可访问；为保护数据，本次启动已停止。请稍后重试。",
                "GlassDesk 数据暂不可用",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return StartupOutcome.UserDataLeaseUnavailable;
        }

        _guard.CommandReceived += OnCommandReceived;
        _guard.StartListening();

        if (DisplayService.Primary() is not { } startupPrimary)
        {
            MessageBox.Show("无法读取 Windows 当前主显示器。GlassDesk 未创建任何可见窗口。",
                "GlassDesk 启动受阻", MessageBoxButton.OK, MessageBoxImage.Warning);
            return StartupOutcome.PrimaryDisplayUnavailable;
        }
        _primaryDisplaySession = new PrimaryDisplaySession(startupPrimary);

        var theme = ThemeService.Initialize(_application.Dispatcher);

        _data = new GlassDeskDataStore(AppPaths.ForCurrentUser());
        _loadReport = _data.LoadAll();
        _backdropDiagnostics = GlassDesk.UI.BackdropRecovery.BeginAutomaticDiagnostics(_data.Paths.LogDirectory);

        // 应用服务（导入、启动、图标、失效复查）在这里建一次，
        // 之后由各个窗口共用一个实例。界面上没有任何一处直接碰文件、进程或窗口句柄，
        // 这条结构规则从阶段 3 起就是靠这个入口维持的。
        _services = new AppServiceHost(_data);

        // 先把「目标还在不在」复查一遍。用户可能在上次运行之后删掉了某个程序，
        // 或者把移动硬盘拔了；条目不删、只标记，所以这一步是幂等的。
        // 只有状态真的变了才会写文件，因此正常启动不会改动应用库。
        _missingReport = _services.Maintenance.RefreshMissingTargets();

        // 在创建任何窗口之前先采用已存的外观设置，这样第一次绘制就是对的。
        var appearance = _data.Settings.Current.Appearance;
        ApplySampleOverride(appearance, _options.Sample);
        theme.Update(appearance);

        // 程序坞设置的通知口同理：在窗口创建之前装好，
        // Dock 第一次刷新时看到的就是真实设置而不是默认值。
        // 之后设置一变，从这里推给已打开的 Dock（D-16/D-17 即时生效）。
        _dockSettings = DockSettingsHost.Initialize(_application.Dispatcher);
        _dockSettings.Update(_data.Settings.Current.Dock);

        // 运行状态跟踪也要在磁贴构建之前完成初始枚举：
        // 「Dock 启动之前就在运行的应用」只有这样才能在第一次绘制时就有状态点。
        _services.Running.Start();

        CreateMessageWindow();
        CreateTrayIcon();

        CreateWindows();
        InitializePerformanceCoordinator();
        InitializeInteractionController();

        theme.Changed += OnThemeChanged;

        WriteStartupLog();

        InitializeUpdates();

        return StartupOutcome.Started;
    }

    /// <summary>
    /// 按样例要求覆盖这次运行的外观。
    /// </summary>
    /// <remarks>
    /// 只改内存里的这一份，**从不写回设置文件**：这是为了拍出可复现的样例，
    /// 不是替用户做决定。要求产出浅色、深色、降级三种样例时，
    /// 必须能固定输入，否则系统主题一变，前后两张图就不可比了。
    /// </remarks>
    private static void ApplySampleOverride(AppearanceSettings appearance, AppearanceSample sample)
    {
        ArgumentNullException.ThrowIfNull(appearance);

        switch (sample)
        {
            case AppearanceSample.Light:
                appearance.Theme = ThemeMode.Light;
                appearance.UseSystemBackdrop = true;
                appearance.ReduceTransparency = false;
                break;

            case AppearanceSample.Dark:
                appearance.Theme = ThemeMode.Dark;
                appearance.UseSystemBackdrop = true;
                appearance.ReduceTransparency = false;
                break;

            case AppearanceSample.Degraded:
                // 关键在 UseSystemBackdrop = false：它让窗口不去请求材质，
                // 从而走「材质不可用」那条自绘填充路径——也就是旧系统上的样子。
                appearance.Theme = ThemeMode.Light;
                appearance.UseSystemBackdrop = false;
                appearance.ReduceTransparency = false;
                break;

            default:
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _deferredCommandGate.Close();

        DisposeUpdates();

        if (ThemeService.Current is { } theme)
        {
            theme.Changed -= OnThemeChanged;
        }

        if (_guard is not null)
        {
            _guard.CommandReceived -= OnCommandReceived;
        }

        DisposeInteractionController();
        DisposePerformanceCoordinator();
        _backdropDiagnostics?.Dispose(); _backdropDiagnostics = null;

        _tray?.Dispose();
        _tray = null;

        _services?.Dispose();
        _services = null;

        _messageWindow?.Dispose();
        _messageWindow = null;

        _userDataLease?.Dispose();
        _userDataLease = null;

        _guard?.Dispose();
        _guard = null;

        ThemeService.Current?.Dispose();
        _disposed = true;
    }

    /// <summary>在启动时绑定的主显示器上显示启动台，可从 Dock 图标开始过渡。</summary>
    private void ShowLauncher() => ShowLauncher(null);

    private void ShowLauncher(PixelRect? sourceBounds)
    {
        if (_launcher is null || _primaryDisplaySession?.IsAvailable != true)
        {
            return;
        }

        RequestPerformanceActivation();
        try
        {
            _launcher.RefreshDiagnostics();
            _dock?.SetLauncherContainerState(true);
            _launcher.ShowWithTransition(sourceBounds ?? _dock?.GetLauncherButtonPixelBounds());
            _launcher.Activate();
        }
        finally
        {
            _performanceActivationPending = false;
            ReconcilePerformance();
        }
    }

    private void HideLauncher() => _launcher?.CloseWithTransition();

    private void ShowSettings()
    {
        if (_settings is null || _primaryDisplaySession?.IsAvailable != true)
        {
            return;
        }

        _settings.RefreshDiagnostics(_loadReport);
        _settings.RefreshStartupRegistration();
        _settings.RefreshInstallationStatus(() =>
            new InstallationLocator().Inspect(Environment.ProcessPath ?? string.Empty));

        if (!_settings.IsVisible)
        {
            _settings.PositionOnPrimary();
            _settings.Show();
        }

        _settings.Activate();
    }

    private void ExitApplication()
    {
        // 必须放行窗口关闭，关停才能走完，
        // 否则下面的关闭处理函数会把它拦住。
        _deferredCommandGate.Close();
        _shuttingDown = true;

        // 显式关停：启动台和设置窗口平时常是隐藏的，
        // 若用默认的 OnLastWindowClose 模式，它们一关闭程序就结束了。
        _application.Shutdown();
    }

    private void HandOffToRunningInstance()
    {
        // 登录启动只确保常驻实例存在；绝不把“显示启动台”的意图发给旧实例。
        var command = StartupIntent.GetDuplicateInstanceCommand(_options);
        if (command is null) return;

        // 同步等待：进程马上就要退出，没有别的事可做。
        // 超时是为了覆盖正在运行的实例忙于启动的情况。
        SingleInstanceGuard.TrySendCommandAsync(InstanceName, command, TimeSpan.FromSeconds(3))
            .GetAwaiter()
            .GetResult();
    }

    private void CreateWindows()
    {
        _dock = new DockWindow();
        _launcher = new LauncherWindow();
        var primaryDisplay = _primaryDisplaySession
            ?? throw new InvalidOperationException("Primary Display must be captured before creating GlassDesk windows.");
        _dock.BindPrimaryDisplay(primaryDisplay);
        _launcher.BindPrimaryDisplay(primaryDisplay);
        _launcher.ContainerAnchorProvider = () => _dock?.GetLauncherButtonPixelBounds();
        _launcher.IsVisibleChanged += (_, e) => _dock?.SetLauncherContainerState(e.NewValue is true);
        _settings = new SettingsWindow();
        _settings.BindPrimaryDisplay(primaryDisplay);

        _settings.AttachDataStore(_data!, _loadReport);
        _settings.AttachStartupRegistration(
            new StartupRegistration(),
            Environment.ProcessPath ?? string.Empty);
        _settings.AttachInstallation(new InstallationLocator().Inspect(Environment.ProcessPath ?? string.Empty));
        _dock.AttachServices(_services!, _dockSettings!, (PixelRect? source) =>
            ToggleLauncherFromDock(_launcher, source, ShowLauncher));
        _dock.ToggleDesktopIcons = () => ExecuteInteraction(GlassDesk.Core.Input.InputAction.ToggleDesktopIcons);
        _launcher.AttachServices(_services!, _loadReport?.LibraryStatus != LoadStatus.Unrecoverable);
        _launcher.InternalDragStarted += (_, _) => _dock?.RaiseAboveLauncherDuringDrag();
        _launcher.InternalDragFinished += (_, _) => _launcher?.RaiseAboveDockAfterDrag();
        _services!.LibraryChanged += (_, _) =>
        {
            if (_application.Dispatcher.CheckAccess())
            {
                RefreshLibraryForPerformance();
            }
            else
                _application.Dispatcher.BeginInvoke(RefreshLibraryForPerformance);
        };
        _settings.LauncherSettingsChanged += (_, settings) => _launcher?.ApplySettings(settings);

        _launcher.ApplySettings(_data!.Settings.Current.Launcher);

        var icon = CreateWindowIcon();
        foreach (Window window in new Window[] { _dock, _launcher, _settings })
        {
            window.Icon = icon;

            // 一次误触的关闭请求（比如在程序坞上按 Alt+F4）不能把产品带下线；
            // 退出是托盘菜单的职责。
            window.Closing += OnWindowClosing;
        }

        _dock.Show();

        if (StartupIntent.ShouldOpenLauncher(_options))
        {
            ShowLauncher();
        }

        if (StartupIntent.ShouldOpenSettings(_options))
        {
            ShowSettings();
        }
    }

    private static BitmapSource CreateWindowIcon()
    {
        return TrayIconArtwork.RenderBitmap(32);
    }

    private void CreateMessageWindow()
    {
        var parameters = new HwndSourceParameters(MessageWindowName)
        {
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
            WindowStyle = 0,
            ParentWindow = MessageOnlyWindow,
        };

        _messageWindow = new HwndSource(parameters);
        _messageWindow.AddHook(OnMessageWindowProc);
    }

    private void CreateTrayIcon()
    {
        if (_messageWindow is null)
        {
            return;
        }

        _trayIconHandle = TrayIconArtwork.CreateHandle(32);

        _tray = new TrayIcon(_messageWindow.Handle);
        _tray.Show(_trayIconHandle, "GlassDesk");
    }

    private nint OnMessageWindowProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == 0x0312)
        {
            if (_settings?.TryCaptureRegisteredHotkey(lParam) != true)
            {
                var key = (uint)((lParam.ToInt64() >> 16) & 0xFFFF);
                if (_performance?.IsSuppressionRequested == true && _launcher?.IsVisible != true && key is 5 or 6 or 0x100 or 0x101)
                { handled = true; return 0; }
                _interaction?.HandleHotkey(wParam.ToInt32());
            }
            handled = true;
            return 0;
        }
        if (msg != TrayIcon.CallbackMessage)
        {
            return 0;
        }

        switch (TrayIcon.Classify((int)lParam))
        {
            case TrayEvent.Activate:
            case TrayEvent.DoubleClick:
                ShowLauncher();
                handled = true;
                break;

            case TrayEvent.ContextMenu:
                ShowTrayMenu(hwnd);
                handled = true;
                break;

            default:
                break;
        }

        return 0;
    }

    private void ShowTrayMenu(nint hwnd)
    {
        var menu = new ContextMenu
        {
            Placement = PlacementMode.MousePoint,
            PlacementTarget = _dock,
            StaysOpen = false,
        };

        menu.Items.Add(CreateMenuItem("打开启动台", ShowLauncher));
        menu.Items.Add(CreateMenuItem("隐藏启动台", HideLauncher));
        var gameSleep = new MenuItem { Header = "游戏休眠", IsCheckable = true, IsChecked = RuntimeManualGameSleep };
        gameSleep.Click += (_, _) => SetManualGameSleep(gameSleep.IsChecked);
        menu.Items.Add(gameSleep);
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("设置…", ShowSettings));
        menu.Items.Add(CreateMenuItem("打开数据文件夹", OpenDataFolder));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("退出", ExitApplication));

        // 托盘菜单只有在所属窗口先到前台之后，才会在点击外部时收起；
        // 外壳不会主动激活我们那个只收消息的窗口。
        WindowActivation.TryBringToForeground(hwnd);

        menu.IsOpen = true;
    }

    private static MenuItem CreateMenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private void OpenDataFolder()
    {
        if (_data is null)
        {
            return;
        }

        try
        {
            _data.Paths.EnsureCreated();

            // 通过外壳启动资源管理器是打开文件夹的官方做法，
            // 也免得为此去依赖某个创建进程的辅助函数。
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _data.Paths.RootDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // 打开文件夹只是个便利功能，做不成也不能打扰应用本身。
            AppendToLog($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] 无法打开数据文件夹：{ex.Message}");
        }
    }

    private void ExportBackdropDiagnostics()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出背景诊断（不包含屏幕画面）", Filter = "JSON 报告|*.json",
            FileName = "GlassDesk-background-diagnostics.json", AddExtension = true, DefaultExt = ".json"
        };
        if (dialog.ShowDialog() != true) return;
        try { GlassDesk.UI.BackdropRecovery.ExportDiagnosticReport(dialog.FileName); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { ReportInteractionError("无法导出背景诊断：" + error.Message); }
    }

    private void OnCommandReceived(object? sender, string command)
    {
        if (_deferredCommandGate.IsClosed || _shuttingDown || _application.Dispatcher.HasShutdownStarted || _application.Dispatcher.HasShutdownFinished)
        {
            return;
        }

        // 管道回调在后台线程上到达；而每个窗口操作都必须跑在
            // 调度线程上。
        _application.Dispatcher.BeginInvoke(() => _deferredCommandGate.TryRun(() =>
            {
                if (_shuttingDown || _application.Dispatcher.HasShutdownStarted || _application.Dispatcher.HasShutdownFinished)
                {
                    return;
                }

                switch (command)
                {
                    case InstanceCommands.ShowLauncher:
                        ShowLauncher();
                        break;

                    case InstanceCommands.HideLauncher:
                        HideLauncher();
                        break;

                    case InstanceCommands.ShowSettings:
                        ShowSettings();
                        break;

                    case InstanceCommands.Exit:
                        ExitApplication();
                        break;

                    default:
                        break;
                }
            }));
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        // 这个标志是按浅色或深色背景分别绘制的，所以必须重新渲染。
        if (_tray is null)
        {
            return;
        }

        _tray.Update(TrayIconArtwork.CreateHandle(32), "GlassDesk");

        var icon = CreateWindowIcon();
        foreach (Window window in new Window[] { _dock!, _launcher!, _settings! })
        {
            window.Icon = icon;
        }
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_shuttingDown)
        {
            // 这是一次真正的退出：放行窗口关闭。
            return;
        }

        // 绝不能让一次误触的关闭请求——比如在程序坞上按 Alt+F4——结束整个会话。
        // 退出是托盘菜单的职责。
        e.Cancel = true;

        if (sender is Window window)
        {
            if (window is LauncherWindow launcher)
                launcher.CloseWithTransition();
            else
                window.Hide();
        }
    }

    /// <summary>
    /// 把启动结果记录下来，包括任何数据加载提示，就放在数据旁边。
    /// </summary>
    private void WriteStartupLog()
    {
        if (_data is null)
        {
            return;
        }

        var lines = new List<string>
        {
            $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] GlassDesk 启动",
            $"  版本：{typeof(AppHost).Assembly.GetName().Version}",
            $"  数据目录：{_data.Paths.RootDirectory}",
            $"  {WindowsCapabilities.Describe()}",
            $"  {ThemeService.Current?.Describe()}",
            $"  应用库条目：{_data.Library.Items.Count}",
        };

        if (_missingReport is not null)
        {
            lines.Add(
                $"  目标失效：{_missingReport.Missing} 条（检查 {_missingReport.Checked} 条，"
                + $"新发现 {_missingReport.NewlyBroken.Count} 条，恢复 {_missingReport.Recovered.Count} 条）");
        }

        var withArguments = _data.Library.Items.Count(entry => entry is not null && !string.IsNullOrEmpty(entry.Arguments));
        lines.Add($"  带启动参数：{withArguments} 条");
        lines.Add($"  缓存图标：{_services?.Icons.FileCount ?? 0} 个");

        if (_loadReport?.HasNotices == true)
        {
            lines.Add("  数据加载提示：");
            lines.AddRange(_loadReport.Notices.Select(notice => $"    · {notice}"));
        }

        if (_loadReport?.PreservedFiles.Count > 0)
        {
            lines.Add("  已保留的损坏文件：");
            lines.AddRange(_loadReport.PreservedFiles.Select(path => $"    · {path}"));
        }

        AppendToLog(lines);
    }

    /// <summary>向启动日志追加若干行，绝不让写日志拖垮应用。</summary>
    private void AppendToLog(params IEnumerable<string> lines)
    {
        if (_data is null)
        {
            return;
        }

        try
        {
            _data.Paths.EnsureCreated();
            File.AppendAllLines(Path.Combine(_data.Paths.LogDirectory, "startup.log"), lines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 写日志绝不能妨碍启动。
        }
    }
}
