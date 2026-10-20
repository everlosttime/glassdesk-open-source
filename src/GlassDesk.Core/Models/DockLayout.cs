namespace GlassDesk.Core.Models;

/// <summary>
/// Dock 排列。与应用库分开存储，这样重排
/// Dock 绝不会损坏 <see cref="AppEntry"/>。
/// </summary>
public sealed class DockLayout
{
    /// <summary>
    /// Dock 中显示的应用库条目 id，按顺序排列。库中已不存在的
    /// id 属于过期项，应当跳过而不是当作错误——用户可能已经
    /// 删掉了该应用，Dock 只需相应缩短。
    /// </summary>
    public List<string> PinnedItemIds { get; set; } = [];
}