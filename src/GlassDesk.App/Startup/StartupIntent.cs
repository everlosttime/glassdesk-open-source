using GlassDesk.Platform;

namespace GlassDesk.App.Startup;

/// <summary>把命令行启动意图转换成首实例窗口和重复实例 IPC 行为。</summary>
internal static class StartupIntent
{
    public static string? GetDuplicateInstanceCommand(CommandLineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.Autostart)
            return null;

        return options switch
        {
            { Exit: true } => InstanceCommands.Exit,
            { ShowSettings: true } => InstanceCommands.ShowSettings,
            { ShowLauncher: true } => InstanceCommands.ShowLauncher,
            _ => InstanceCommands.ShowLauncher,
        };
    }

    public static bool ShouldOpenLauncher(CommandLineOptions options) =>
        !options.Autostart && options.ShowLauncher;

    public static bool ShouldOpenSettings(CommandLineOptions options) =>
        !options.Autostart && options.ShowSettings;

    public static bool ShouldExitWithoutExistingInstance(CommandLineOptions options) =>
        options.Exit;

    public static string GetLegacyInstanceBlockMessage(LegacyInstanceStatus status) => status switch
    {
        LegacyInstanceStatus.Running => "检测到仍在运行的旧版 GlassDesk 实例。为避免新旧版本同时修改同一份用户数据，请先从旧版托盘菜单退出后重试。",
        LegacyInstanceStatus.Unknown => "无法确认旧版 GlassDesk 实例是否仍在运行。为保护用户数据，本次启动已阻止；请确认旧版完全退出后重试。",
        _ => string.Empty,
    };
}
