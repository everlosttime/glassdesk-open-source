using System.IO;
using System.Text.Json.Nodes;

namespace GlassDesk.DesktopAcceptance;

/// <summary>库里一条记录的只读视图。</summary>
internal sealed record LibraryEntry(string Id, string DisplayName, string Target, bool IsTargetMissing);

/// <summary>
/// 直接读产品写下的数据文件。
/// </summary>
/// <remarks>
/// 这是驱动唯一「往产品内部看」的地方，而且看的是落盘的公开文档，不是内存状态：
/// <c>apps.json</c>、<c>dock-layout.json</c> 的字段名由产品自己声明
/// （<c>AppLibraryData</c> / <c>DockLayout</c>，camelCase）。
/// 这样驱动不需要引用产品代码，却能独立核对「产品到底把什么写进去了」。
/// </remarks>
internal static class ProductLibrary
{
    /// <summary>产品的真实数据目录。</summary>
    internal static string DefaultRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GlassDesk");

    internal static string AppsFile(string root) => Path.Combine(root, "apps.json");

    internal static string DockLayoutFile(string root) => Path.Combine(root, "dock-layout.json");

    /// <summary>
    /// 这个数据目录里是不是已经有真实的应用库。
    /// </summary>
    /// <remarks>
    /// 拖入验证<strong>会往应用库里写东西</strong>。真实用户的数据目录里已有条目时，
    /// 宁可整轮阻塞，也不能在别人的库上加一条测试记录。
    /// </remarks>

    internal static IReadOnlyList<LibraryEntry> ReadEntries(string root)
    {
        var file = AppsFile(root);
        if (!File.Exists(file))
        {
            return [];
        }

        var document = JsonNode.Parse(File.ReadAllText(file));
        var items = document?["items"] as JsonArray;
        if (items is null)
        {
            return [];
        }

        var entries = new List<LibraryEntry>();

        foreach (var item in items)
        {
            if (item is null)
            {
                continue;
            }

            entries.Add(new LibraryEntry(
                item["id"]?.GetValue<string>() ?? string.Empty,
                item["displayName"]?.GetValue<string>() ?? string.Empty,
                item["target"]?.GetValue<string>() ?? string.Empty,
                item["isTargetMissing"]?.GetValue<bool>() ?? false));
        }

        return entries;
    }

    internal static IReadOnlyList<string> ReadPinned(string root)
    {
        var file = DockLayoutFile(root);
        if (!File.Exists(file))
        {
            return [];
        }

        var document = JsonNode.Parse(File.ReadAllText(file));
        var pinned = document?["pinnedItemIds"] as JsonArray;

        return pinned is null
            ? []
            : [.. pinned.Select(node => node?.GetValue<string>() ?? string.Empty)];
    }

    /// <summary>
    /// 库里那些<strong>不是本驱动创建</strong>的条目。
    /// </summary>
    /// <remarks>
    /// 原先的护栏只问「库是不是空的」，实测下来太粗：同一驱动的第二轮会被第一轮留下的条目
    /// 拦住。护栏的真正目的是「绝不往用户的真实数据里写」，所以判据改成看条目的目标路径
    /// 落在哪里——本驱动只在自己 <c>%TEMP%\GlassDesk-桌面驱动-*</c> 的工作目录里造文件，
    /// 目标在别处的一律视为用户的真实数据。
    /// </remarks>
    internal static IReadOnlyList<LibraryEntry> ForeignEntries(string root, string allowedTargetPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(allowedTargetPrefix);

        return
        [
            .. ReadEntries(root).Where(entry =>
                !entry.Target.StartsWith(allowedTargetPrefix, StringComparison.OrdinalIgnoreCase)),
        ];
    }

    /// <summary>产品的数据目录与文件在此刻的样子，写进证据里。</summary>
    internal static string Describe(string root)
    {
        var entries = ReadEntries(root);
        var pinned = ReadPinned(root);

        return $"数据目录 {root}；库 {entries.Count} 条；固定 {pinned.Count} 条"
            + $"；apps.json {(File.Exists(AppsFile(root)) ? "在" : "不在")}";
    }
}