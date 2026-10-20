namespace GlassDesk.Core.Models;

/// <summary>
/// 共享应用库中的一条记录。
/// </summary>
/// <remarks>
/// Dock 与启动台都按 <see cref="Id"/> 引用这些记录，两者都不持有各自的副本。
/// 把某项从 Dock 移除只是取消固定——
/// 记录仍留在这里，直到用户明确从库中删除它。
///
/// 属性刻意不标为 <c>required</c>，而是带有无害的默认值。
/// 缺少某个字段的设置文件仍应能加载，好让用户自行修复，
/// 而不是抛出反序列化异常、丢掉整个库。
/// 用 <see cref="IsUsable"/> 判断一条记录是否有意义。
/// </remarks>
public sealed class AppEntry
{
    /// <summary>
    /// 稳定的身份标识。导入时生成一次，此后永不改写，这样显示名或目标变化时布局
    /// 仍指向正确的记录。
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>用户可见的名称。导入时默认为文件名。</summary>
    public string DisplayName { get; set; } = string.Empty;

    public AppItemKind Kind { get; set; } = AppItemKind.Program;

    /// <summary>绝对路径；当 <see cref="Kind"/> 为 <see cref="AppItemKind.Url"/> 时为 URL。</summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>启动时传入的命令行参数。没有参数时为 null。</summary>
    public string? Arguments { get; set; }

    /// <summary>被启动进程的工作目录。为 null 表示「交给 shell 决定」。</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// 目标快捷方式上显式设置的应用身份（<c>System.AppUserModel.ID</c>）。
    /// </summary>
    /// <remarks>
    /// 导入 <c>.lnk</c> 时顺带读出（快捷方式解析器在解析目标的同时读取属性存储）。
    /// 打包应用与 UWP 的窗口不归快捷方式指向的进程所有，按映像路径找窗口必失败；
    /// 这个身份是快捷方式与已运行窗口之间唯一可靠的纽带（阶段 4 探针实测，
    /// 见 <c>docs/evidence/阶段4-探针实测输出.txt</c>）。没有显式身份的条目保持 null，
    /// 匹配退回按进程映像路径。
    /// </remarks>
    public string? AppUserModelId { get; set; }

    /// <summary>
    /// 原始图标的来源，例如可执行文件路径或 shell 图标位置。
    /// 保留它是为了在缓存被清空后能重新提取图标。
    /// </summary>
    public string? IconSource { get; set; }

    /// <summary>用户自选的图标文件。优先于 <see cref="IconSource"/>。</summary>
    public string? CustomIconPath { get; set; }

    /// <summary>
    /// 用户额外添加的搜索词。用于纠正自动拼音生成读错的情况，
    /// 尤其是多音字。
    /// </summary>
    public List<string> SearchAliases { get; set; } = [];

    /// <summary>
    /// 供搜索使用的完整拼音。
    /// </summary>
    /// <remarks>
    /// 导入时<strong>不</strong>填充，字段保持为 null；阶段 5 实现搜索时遍历全库补齐。
    /// 理由见 <c>docs/阶段3-交付报告.md</c> 第六节：填对需要一份完整的汉字→拼音表，
    /// 而多音字的读音正确性是阶段 5 自己的问题（功能清单明确不承诺自动读音全对）。
    /// 现在填一份覆盖不全的表，等于给阶段 5 留一个「看着已经支持、其实不全」的坑。
    /// </remarks>
    public string? PinyinIndex { get; set; }

    /// <summary>各音节的首字母，用于缩写搜索。在此之前为 null。</summary>
    public string? PinyinInitials { get; set; }

    /// <summary>
    /// 目标找不到时置位。条目会保留并显示为失效，以便用户重新定位，
    /// 而不是被悄悄丢弃。
    /// </summary>
    public bool IsTargetMissing { get; set; }

    public DateTimeOffset AddedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// 记录指向的内容可以打开时为 true。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="IsUsable"/> 分开，是因为 id 由我们分配，而 target 要由调用方提供。
    /// 导入时检查本属性；从文件读回时检查 <see cref="IsUsable"/>，因为没有 id 的记录
    /// 无法被布局引用。
    /// </remarks>
    public bool HasLaunchTarget => !string.IsNullOrWhiteSpace(Target);

    /// <summary>记录可以存储并被布局引用时为 true。</summary>
    public bool IsUsable => !string.IsNullOrWhiteSpace(Id) && HasLaunchTarget;
}