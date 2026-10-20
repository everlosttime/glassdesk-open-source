namespace GlassDesk.Core.Models.Settings;

/// <summary>启动台的网格、窗口与搜索行为。</summary>
public sealed class LauncherSettings
{
    /// <summary>每页的网格行数。</summary>
    public int Rows { get; set; } = 5;

    /// <summary>每页的网格列数。</summary>
    public int Columns { get; set; } = 7;

    /// <summary>
    /// 窗口模式下占工作区的比例。计划书要求大约
    /// 三分之二，居中。
    /// </summary>
    public double WindowWorkAreaRatio { get; set; } = 0.66;

    /// <summary>铺满整个工作区，而不是显示居中的面板。</summary>
    public bool FullscreenMode { get; set; }

    /// <summary>
    /// 启用后，启动台打开时将光标放进搜索框。默认关闭。
    /// </summary>
    public bool FocusSearchOnOpen { get; set; }

    /// <summary>
    /// 再次打开时恢复上次停留的页面，关闭则总是从第一页开始。
    /// </summary>
    public bool RememberLastPage { get; set; } = true;
}
