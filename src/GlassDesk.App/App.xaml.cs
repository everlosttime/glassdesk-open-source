using System.Windows;
using GlassDesk.App.Startup;
using GlassDesk.Core.Storage;
using GlassDesk.UI.Theming;

namespace GlassDesk.App;

/// <summary>
/// 应用入口点。
/// </summary>
/// <remarks>
/// 有意保持很薄。它只决定以哪种模式运行，然后把控制权交给
/// <see cref="AppHost"/>；产品行为的一切都在那里或更下层。
/// </remarks>
public partial class App : Application, IDisposable
{
    private AppHost? _host;
    private CrashDiagnostics? _crashDiagnostics;
    private GlassDesk.Platform.Storage.AcceptanceWorkspace? _pendingDiagnosticWorkspace;
    private bool _shaderFailureSubscribed;

    /// <summary>
    /// 释放宿主及其拥有的一切。
    /// </summary>
    /// <remarks>
    /// 之所以实现它而不是只交给 <see cref="OnExit"/>，是为了让可释放字段的
    /// 归属对读者和静态分析器都明确。
    /// </remarks>
    public void Dispose()
    {
        if (_shaderFailureSubscribed)
        {
            System.Windows.Media.Effects.PixelShader.InvalidPixelShaderEncountered -= OnBackdropShaderFailure;
            _shaderFailureSubscribed = false;
        }
        _host?.Dispose();
        _host = null;
        _pendingDiagnosticWorkspace?.Dispose();
        _pendingDiagnosticWorkspace = null;
        _crashDiagnostics?.Dispose();
        _crashDiagnostics = null;
        GC.SuppressFinalize(this);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var options = CommandLineOptions.Parse(e.Args);
        if (!GlassDesk.Platform.WindowsCapabilities.IsSupported)
        {
            MessageBox.Show("此测试版本需要 Windows 11（Build 22000 及以上）。", "GlassDesk", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(2); return;
        }
        System.Windows.Media.Effects.PixelShader.InvalidPixelShaderEncountered += OnBackdropShaderFailure;
        _shaderFailureSubscribed = true;

        if (options.Autostart && options.Unknown.Count > 0)
        {
            Console.Error.WriteLine("--autostart 不能与其它启动参数组合；拒绝执行。");
            Shutdown(2);
            return;
        }

        if (options.SelfTest && (options.Verb != AppServiceVerb.None || options.Diagnose || options.Exit
            || options.ShowLauncher || options.ShowSettings || options.Sample != AppearanceSample.None || options.Help))
        {
            Console.Error.WriteLine("--selftest 不能与其它启动或应用服务模式组合。");
            Shutdown(2);
            return;
        }

        if (options.Unknown.Count > 0 && (options.SelfTest || options.Verb is AppServiceVerb.Phase3Acceptance
            or AppServiceVerb.Phase4Acceptance or AppServiceVerb.Phase5Acceptance or AppServiceVerb.Phase6Acceptance))
        {
            Console.Error.WriteLine("自检或阶段验收参数冲突或无法识别；拒绝启动验收动作。");
            Shutdown(2);
            return;
        }

        var hasIsolationArguments = options.AcceptanceDataRoot is not null || options.AcceptanceReport is not null;
        if (hasIsolationArguments && !AcceptanceEntryPoint.IsValidIsolatedRun(options))
        {
            Console.Error.WriteLine("验收隔离参数只允许与独立的 --selftest 或 --diagnose 及完整的数据目录、报告参数一起使用。");
            Shutdown(2);
            return;
        }

        if (options.Help)
        {
            ReportWriter.Write(CommandLineOptions.HelpText, null);
            Shutdown(0);
            return;
        }

        var paths = AppPaths.ForCurrentUser();

        if (hasIsolationArguments)
        {
            if (options.Diagnose)
            {
                _pendingDiagnosticWorkspace = AcceptanceEntryPoint.Prepare(paths, options.AcceptanceDataRoot!,
                    [new("report", options.AcceptanceReport!)]);
                if (_pendingDiagnosticWorkspace is null) { Shutdown(2); return; }
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () =>
                {
                    var workspace = _pendingDiagnosticWorkspace;
                    _pendingDiagnosticWorkspace = null;
                    if (workspace is null) return;
                    var exitCode = AcceptanceEntryPoint.Run(workspace, prepared =>
                    {
                        ThemeService.Initialize(Dispatcher);
                        return DiagnosticRunner.Run(new AppPaths(prepared.DataRoot), prepared.GetOutput("report"));
                    });
                    Shutdown(exitCode);
                });
                return;
            }

            var isolatedExitCode = AcceptanceEntryPoint.Run(
                paths,
                options.AcceptanceDataRoot!,
                [new GlassDesk.Platform.Storage.AcceptanceOutput("report", options.AcceptanceReport!)],
                workspace =>
                {
                    ThemeService.Initialize(Dispatcher);
                    return SelfTest.Run(new AppPaths(workspace.DataRoot), workspace.GetOutput("report"));
                });
            Shutdown(isolatedExitCode);
            return;
        }

        if (options.IsAppServiceCommand)
        {
            // 应用库命令完全不需要界面：导入、启动、复查都只碰数据与系统。
            // 因此这里同步跑完就退出，不创建任何窗口，也不初始化主题。
            var exitCode = AppServiceRunner.Run(paths, options);
            Shutdown(exitCode);
            return;
        }

        if (options.SelfTest)
        {
            // 自检会报告解析后的主题与调色板，所以即使不显示任何窗口，
            // 主题服务也必须先存在。
            ThemeService.Initialize(Dispatcher);

            var exitCode = SelfTest.Run(paths);
            Shutdown(exitCode);
            return;
        }

        if (options.Diagnose)
        {
            // 诊断要读 WPF 的 DIP 值，也要把真实窗口摆到屏幕上再读回位置，
            // 因此不能在这里同步跑：此刻窗口还没被系统定位，读回的是旧值。
            // 放到消息循环空闲时执行，跑完再关掉。
            ThemeService.Initialize(Dispatcher);

            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                () =>
                {
                    var exitCode = DiagnosticRunner.Run(paths);
                    Shutdown(exitCode);
                });

            return;
        }

        if (options.Unknown.Count > 0)
        {
            // 报告而不是忽略，免得敲错的开关看起来像是生效了。
            ReportWriter.Write(
                $"忽略无法识别的参数：{string.Join(' ', options.Unknown)}{Environment.NewLine}{CommandLineOptions.HelpText}",
                null);
        }

        _crashDiagnostics = new CrashDiagnostics(this, paths.LogDirectory);
        _host = new AppHost(this, options);

        switch (_host.Start())
        {
            case StartupOutcome.Started:
                break;

            case StartupOutcome.NothingToDo:
                // 本次启动只要求「退出」，而当时没有实例在运行。
                // 没有别的实例可通知，更不该因此把界面拉起来。
                ReportWriter.Write("没有正在运行的 GlassDesk 实例，无需退出。", null);
                Shutdown(0);
                break;

            case StartupOutcome.LegacyInstanceBlocked:
            case StartupOutcome.UserDataLeaseUnavailable:
            case StartupOutcome.PrimaryDisplayUnavailable:
                Shutdown(2);
                break;

            default:
                // 另一个实例已经持有 UI，并且已被告知该做什么。
                Shutdown(0);
                break;
        }
    }

    private static void OnBackdropShaderFailure(object? sender, EventArgs e) => GlassDesk.UI.BackdropRecovery.UseBasicRendering();

    protected override void OnExit(ExitEventArgs e)
    {
        Dispose();
        base.OnExit(e);
    }
}
