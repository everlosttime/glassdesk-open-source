namespace GlassDesk.App.Startup;

/// <summary>用于产出视觉样例的外观覆盖。</summary>
public enum AppearanceSample
{
    /// <summary>不覆盖，按设置文件来。</summary>
    None = 0,

    /// <summary>浅色 + 系统材质。</summary>
    Light = 1,

    /// <summary>深色 + 系统材质。</summary>
    Dark = 2,

    /// <summary>浅色 + 不请求系统材质，走自绘填充的降级路径。</summary>
    Degraded = 3,
}

/// <summary>
/// 阶段 3 引入的无界面应用库命令。
/// </summary>
/// <remarks>
/// 存在的意义与 <c>--selftest</c> 一样：让「导入四类条目、重启后仍在、都能打开」
/// 这类验收能在没有人工点界面的情况下<strong>真的跑一遍</strong>，
/// 并把结果写成可以贴进报告的文本。界面要到阶段 4/5 才接上，
/// 在那之前这些命令也是唯一能端到端验证导入与启动的途径。
/// </remarks>
public enum AppServiceVerb
{
    None = 0,

    /// <summary>导入给定的路径或网址。</summary>
    Import = 1,

    /// <summary>只扫描「开始」菜单并列出结果，不改动应用库。</summary>
    ScanStartMenu = 2,

    /// <summary>扫描「开始」菜单并把全部条目导入。</summary>
    ImportStartMenu = 3,

    /// <summary>列出应用库内容。</summary>
    ListLibrary = 4,

    /// <summary>复查失效目标并报告。</summary>
    CheckLibrary = 5,

    /// <summary>把一条记录重新定位到新路径。</summary>
    Relocate = 6,

    /// <summary>打开一条记录。</summary>
    Launch = 7,

    /// <summary>先尝试激活已有窗口，没有才启动。</summary>
    Activate = 8,

    /// <summary>用 WIC 解码每一个缓存图标，确认写出来的 PNG 真的能读。</summary>
    VerifyIcons = 9,

    /// <summary>清空图标缓存。</summary>
    ClearIconCache = 10,

    /// <summary>造出中文、空格、失效路径、带参数快捷方式等代表性样本。</summary>
    MakeSamples = 11,

    /// <summary>造出指定数量的合成条目，用于大库验收。</summary>
    SeedLibrary = 12,

    /// <summary>把缓存图标拼成一张联系表图片，供人工核对图标确实取到了。</summary>
    IconContactSheet = 13,

    /// <summary>跑一遍阶段 3 的全部验收检查。</summary>
    Phase3Acceptance = 14,

    /// <summary>探针 A：实测应用身份（.lnk 显式 / 窗口显式 / 进程包身份）的三种形态。</summary>
    ProbeAumid = 15,

    /// <summary>探针 B：实测 WinEvent 事件源能否覆盖窗口出现、消失与可见性变化。</summary>
    ProbeRunEvents = 16,

    /// <summary>跑一遍阶段 4 的全部验收检查（随批次累积）。</summary>
    Phase4Acceptance = 17,

    /// <summary>探针 C：实测拖放管线的注册归属（WPF / OLE）。</summary>
    ProbeDragDrop = 18,

    /// <summary>探针 D：实测「显示桌面」的三条候选实现路径。</summary>
    ProbeShowDesktop = 19,

    Phase5Acceptance = 20,

    Phase6Acceptance = 21,
}

/// <summary>本次启动被要求以什么方式运行。</summary>
public sealed class CommandLineOptions
{
    /// <summary>
    /// 运行无界面的环境自检，打印结果后退出。
    /// </summary>
    /// <remarks>
    /// 存在的意义是让构建产物在没有人工盯窗口的情况下也能被验证：它会报告
    /// 系统版本、有哪些毛玻璃能力可用、数据存放在哪里，以及
    /// 数据是否干净地加载、运行中的实例是否检测到了自己。它是第一阶段
    /// 「能启动并干净退出」检查的自动化形式。
    /// </remarks>
    public bool SelfTest { get; private init; }

    /// <summary>
    /// 输出显示与坐标空间诊断后退出。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="SelfTest"/> 分开，是因为两者关注的东西不会同时变化：自检盯的是
    /// 数据可靠性，诊断盯的是坐标与显示器。坐标错了不会报错，只会让窗口出现在错误的
    /// 位置，所以这件事值得一个专门的命令，并且输出要能直接贴进报告。
    /// </remarks>
    public bool Diagnose { get; private init; }

    /// <summary>启动时显示启动台。</summary>
    public bool ShowLauncher { get; private init; }

    /// <summary>启动时显示设置中心。</summary>
    public bool ShowSettings { get; private init; }

    /// <summary>由当前用户登录启动；不得打开启动台或唤起另一个实例。</summary>
    public bool Autostart { get; private init; }

    /// <summary>
    /// 强制以某种外观启动，用于产出可复现的视觉样例。
    /// </summary>
    /// <remarks>
    /// 阶段 2 的交付物是「浅色、深色及降级模式样例」。要拍出可比的样例，就必须能固定
    /// 外观，而不是依赖当时系统处于什么主题。它只覆盖本次运行，不写回设置文件。
    /// </remarks>
    public AppearanceSample Sample { get; private init; } = AppearanceSample.None;

    /// <summary>被要求输出帮助文本。</summary>
    public bool Help { get; private init; }

    /// <summary>
    /// 请求正在运行的实例退出。
    /// </summary>
    /// <remarks>
    /// 存在的意义是让产品能从脚本或终端停下，而不必去点托盘菜单
    /// 或强杀进程；构建与测试流程正是靠它来检查
    /// 退出是否干净。
    /// </remarks>
    public bool Exit { get; private init; }

    public string? AcceptanceDataRoot { get; private init; }

    public string? AcceptanceReport { get; private init; }

    /// <summary>所有没能识别的参数。之所以上报，是为了不让拼写错误被悄悄忽略。</summary>
    public IReadOnlyList<string> Unknown { get; private init; } = [];

    /// <summary>要执行的应用库命令；不是这一类启动时为 <see cref="AppServiceVerb.None"/>。</summary>
    public AppServiceVerb Verb { get; private init; } = AppServiceVerb.None;

    /// <summary>应用库命令的附加参数。</summary>
    public IReadOnlyList<string> VerbArguments { get; private init; } = [];

    /// <summary>本次启动是不是一条应用库命令。</summary>
    public bool IsAppServiceCommand => Verb != AppServiceVerb.None;

    public static CommandLineOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var selfTest = false;
        var diagnose = false;
        var showLauncher = false;
        var showSettings = false;
        var autostart = false;
        var help = false;
        var exit = false;
        string? acceptanceDataRoot = null;
        string? acceptanceReport = null;
        var unknown = new List<string>();
        var sample = AppearanceSample.None;
        var verb = AppServiceVerb.None;
        var verbArguments = new List<string>();

        for (var index = 0; index < args.Count; index++)
        {
            var raw = args[index];
            var token = raw.Trim().ToLowerInvariant();

            // 应用库命令带值：它后面的参数直到下一个开关都属于它。
            if (TryParseVerb(token, out var parsedVerb))
            {
                if (verb != AppServiceVerb.None)
                {
                    // 一次只做一件事。两条命令同时给出来是有歧义的，
                    // 与其猜一个，不如报出来。
                    unknown.Add(raw);
                    continue;
                }

                verb = parsedVerb;
                continue;
            }

            if (verb != AppServiceVerb.None && !token.StartsWith("--", StringComparison.Ordinal))
            {
                verbArguments.Add(raw);
                continue;
            }

            switch (token)
            {
                case "--acceptance-data-root":
                    if (index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                    { unknown.Add(raw); acceptanceDataRoot = string.Empty; }
                    else if (acceptanceDataRoot is not null) unknown.Add(raw);
                    else acceptanceDataRoot = args[++index];
                    break;

                case "--acceptance-report":
                    if (index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                    { unknown.Add(raw); acceptanceReport = string.Empty; }
                    else if (acceptanceReport is not null) unknown.Add(raw);
                    else acceptanceReport = args[++index];
                    break;
                case "--selftest":
                case "--self-test":
                    selfTest = true;
                    break;

                case "--diagnose":
                    diagnose = true;
                    break;

                case "--sample-light":
                    sample = AppearanceSample.Light;
                    break;

                case "--sample-dark":
                    sample = AppearanceSample.Dark;
                    break;

                case "--sample-degraded":
                    sample = AppearanceSample.Degraded;
                    break;

                case "--show-launcher":
                    showLauncher = true;
                    break;

                case "--show-settings":
                    showSettings = true;
                    break;

                case "--autostart":
                    autostart = true;
                    break;

                case "--exit":
                    exit = true;
                    break;

                case "--help":
                case "-h":
                case "/?":
                    help = true;
                    break;

                default:
                    unknown.Add(raw);
                    break;
            }
        }

        var phaseAcceptance = verb is AppServiceVerb.Phase3Acceptance or AppServiceVerb.Phase4Acceptance
            or AppServiceVerb.Phase5Acceptance or AppServiceVerb.Phase6Acceptance;
        if (autostart && (selfTest || diagnose || showLauncher || showSettings || exit || help
            || sample != AppearanceSample.None || verb != AppServiceVerb.None
            || acceptanceDataRoot is not null || acceptanceReport is not null || unknown.Count > 0))
        {
            unknown.Add("--autostart 必须单独使用，不能与其它参数组合");
        }

        if ((selfTest || phaseAcceptance || acceptanceDataRoot is not null || acceptanceReport is not null)
            && (new[] { selfTest, diagnose, exit, showLauncher, showSettings, help, sample != AppearanceSample.None, verb != AppServiceVerb.None }).Count(active => active) > 1)
        {
            unknown.Add("自检或验收不能与其它启动模式组合");
        }

        return new CommandLineOptions
        {
            SelfTest = selfTest,
            Diagnose = diagnose,
            ShowLauncher = showLauncher,
            ShowSettings = showSettings,
            Autostart = autostart,
            Sample = sample,
            Exit = exit,
            AcceptanceDataRoot = acceptanceDataRoot,
            AcceptanceReport = acceptanceReport,
            Help = help,
            Unknown = unknown,
            Verb = verb,
            VerbArguments = verbArguments,
        };
    }

    private static bool TryParseVerb(string token, out AppServiceVerb verb)
    {
        switch (token)
        {
            case "--import": verb = AppServiceVerb.Import; return true;
            case "--scan-start-menu": verb = AppServiceVerb.ScanStartMenu; return true;
            case "--import-start-menu": verb = AppServiceVerb.ImportStartMenu; return true;
            case "--list-library": verb = AppServiceVerb.ListLibrary; return true;
            case "--check-library": verb = AppServiceVerb.CheckLibrary; return true;
            case "--relocate": verb = AppServiceVerb.Relocate; return true;
            case "--launch": verb = AppServiceVerb.Launch; return true;
            case "--activate": verb = AppServiceVerb.Activate; return true;
            case "--verify-icons": verb = AppServiceVerb.VerifyIcons; return true;
            case "--clear-icon-cache": verb = AppServiceVerb.ClearIconCache; return true;
            case "--make-samples": verb = AppServiceVerb.MakeSamples; return true;
            case "--seed-library": verb = AppServiceVerb.SeedLibrary; return true;
            case "--icon-contact-sheet": verb = AppServiceVerb.IconContactSheet; return true;
            case "--phase3-acceptance": verb = AppServiceVerb.Phase3Acceptance; return true;
            case "--probe-aumid": verb = AppServiceVerb.ProbeAumid; return true;
            case "--probe-runevents": verb = AppServiceVerb.ProbeRunEvents; return true;
            case "--phase4-acceptance": verb = AppServiceVerb.Phase4Acceptance; return true;
            case "--phase5-acceptance": verb = AppServiceVerb.Phase5Acceptance; return true;
            case "--phase6-acceptance": verb = AppServiceVerb.Phase6Acceptance; return true;
            case "--probe-dragdrop": verb = AppServiceVerb.ProbeDragDrop; return true;
            case "--probe-showdesktop": verb = AppServiceVerb.ProbeShowDesktop; return true;

            default:
                verb = AppServiceVerb.None;
                return false;
        }
    }

    public static string HelpText =>
        """
        GlassDesk（工作名称）

        用法：GlassDesk.exe [选项]

          --selftest         运行环境自检并退出（不显示界面）
          --selftest --acceptance-data-root <新目录> --acceptance-report <新报告文件>
                             在隔离数据目录运行自检（构建与发布脚本使用）
          --diagnose         输出显示与坐标空间诊断后退出
          --diagnose --acceptance-data-root <新目录> --acceptance-report <新报告文件>
                             隔离诊断数据和报告（仍会创建诊断窗口，需可用测试桌面）
          --show-launcher    显示启动台（已在运行则通知现有实例）
          --show-settings    显示设置中心（已在运行则通知现有实例）
          --autostart        登录启动（已在运行则直接退出，不唤起界面）
          --sample-light     以浅色外观启动（用于产出样例，不写回设置）
          --sample-dark      以深色外观启动
          --sample-degraded  以不请求系统材质的降级外观启动
          --exit             通知正在运行的实例退出
          --help             显示本帮助

          --phase3-acceptance <联系表输出> <报告输出> <新数据目录>  阶段三验收
          --phase4-acceptance <报告输出> <新数据目录>                  阶段四验收
          --phase5-acceptance <报告输出> <新数据目录>                  阶段五命令验收
          --phase6-acceptance <报告输出> <新数据目录>                  阶段六交互与设置验收

        应用库命令（阶段 3，无界面）：

          --import <路径或网址>...   导入条目；可以是多个
          --scan-start-menu          扫描「开始」菜单并列出，不改动应用库
          --import-start-menu        扫描「开始」菜单并全部导入
          --list-library             列出应用库内容
          --check-library            复查失效目标并报告
          --relocate <条目 id> <新路径>  把一条记录重新定位到新位置
          --launch <条目 id>         打开一条记录
          --activate <条目 id>       优先激活已有窗口，没有才启动
          --verify-icons             用系统图像解码器读一遍缓存图标
          --icon-contact-sheet [文件]  把缓存图标拼成一张联系表图片
          --clear-icon-cache         清空图标缓存
          --make-samples [目录]      造出中文、空格、失效路径等代表性样本
          --seed-library <数量>      造出指定数量的合成条目

        不带选项启动时，程序坞会出现，启动台与设置中心可由托盘菜单打开。
        """;
}
