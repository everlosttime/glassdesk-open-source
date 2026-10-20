using System.Text.Json.Serialization;

namespace GlassDesk.Core.Models;

/// <summary>
/// 启动台布局：有序的页面列表，每个页面持有有序的槽位列表。
/// </summary>
/// <remarks>
/// 结构上就不可能出现嵌套。一个槽位要么直接引用应用，
/// 要么是分组；分组只持有应用 id，绝不持有其他槽位。这在结构上
/// 满足了计划书「分组只有一层」的规则，而不是依赖校验
/// 代码事后捕捉环。
/// </remarks>
public sealed class LauncherLayout
{
    public List<LauncherPage> Pages { get; set; } = [];

    /// <summary>Version 2 records explicit launcher membership, independently of Dock.</summary>
    public int SchemaVersion { get; set; }

    /// <summary>Read-only migration input for pre-version-2 layouts; never persisted again.</summary>
    [JsonIgnore]
    public List<string> ExcludedAppIds { get; set; } = [];

    [JsonPropertyName("excludedAppIds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? LegacyExcludedAppIds
    {
        get => null;
        set => ExcludedAppIds = value ?? [];
    }
}

public sealed class LauncherPage
{
    public string Id { get; set; } = string.Empty;

    public List<LauncherSlot> Slots { get; set; } = [];
}

public enum LauncherSlotKind
{
    /// <summary>该槽位显示单个应用。</summary>
    App = 0,

    /// <summary>该槽位显示文件夹式的应用分组。</summary>
    Group = 1,
}

public sealed class LauncherSlot
{
    public LauncherSlotKind Kind { get; set; } = LauncherSlotKind.App;

    /// <summary>当 <see cref="Kind"/> 为 <see cref="LauncherSlotKind.App"/> 时的应用库 id。</summary>
    public string? ItemId { get; set; }

    /// <summary>当 <see cref="Kind"/> 为 <see cref="LauncherSlotKind.Group"/> 时的分组内容。</summary>
    public LauncherGroup? Group { get; set; }
}

/// <summary>
/// 显示在单个网格槽位背后的一组具名应用。
/// </summary>
public sealed class LauncherGroup
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 成员应用的应用库 id。只有 id——分组不能再包含分组。
    /// </summary>
    public List<string> ItemIds { get; set; } = [];
}
