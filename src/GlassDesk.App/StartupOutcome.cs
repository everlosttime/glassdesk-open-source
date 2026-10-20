namespace GlassDesk.App;

/// <summary>
/// 一次启动尝试的结果。
/// </summary>
/// <remarks>
/// 用枚举而不是布尔值，是因为这里有三种真实结局，而不是「成功/失败」两种。
/// 尤其是 <see cref="NothingToDo"/>：启动本身没有任何问题，只是没有事情可做，
/// 把它挤进 false 里会让调用方分不清「让位给别的实例」和「本来就无事可做」。
/// </remarks>
internal enum StartupOutcome
{
    /// <summary>本进程拥有界面，产品已经在运行。</summary>
    Started = 0,

    /// <summary>另一个实例持有界面，本次启动的意图已经转交给它。</summary>
    HandedOffToRunningInstance = 1,

    /// <summary>
    /// 本次启动要求的是「让正在运行的实例退出」，但当时没有实例在运行。
    /// 没有被通知的对象，也不该因此把界面拉起来。
    /// </summary>
    NothingToDo = 2,

    /// <summary>检测到旧版实例协议，已阻止新旧实例同时运行。</summary>
    LegacyInstanceBlocked = 3,

    /// <summary>无法取得用户数据共享租约，已在加载数据前阻止启动。</summary>
    UserDataLeaseUnavailable = 4,

    /// <summary>无法识别 Windows 当前主显示器，已在创建可见窗口前阻止启动。</summary>
    PrimaryDisplayUnavailable = 5,
}
