using System.Globalization;
using GlassDesk.Core.Models;

namespace GlassDesk.Core.Storage;

/// <summary>
/// 推导用于在导入时识别重复项的身份。
/// </summary>
/// <remarks>
/// 有意与 <see cref="AppEntry.Id"/> 分开。id 不透明且永久；
/// 键则从目标重新计算，这样同一个程序导入两次也能被
/// 识别出来，即使两条记录创建于不同会话、其间还改过
/// 显示名称。
///
/// <para>
/// Windows 路径比较忽略大小写，所以键会折叠大小写。参数参与
/// 构成键，因为指向同一个可执行文件、参数不同的两个快捷方式是真正
/// 不同的两次启动动作。
/// </para>
///
/// <para>
/// <b>键只在本进程内推导，从不写入文件。</b>每次加载都会用当时的算法
/// 重新计算一遍（见 <see cref="AppLibraryStore.RebuildIndexes"/>），
/// 因此改算法不需要迁移既有数据。
/// </para>
/// </remarks>
public static class AppEntryKey
{
    public static string Compute(AppEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var target = NormalizeTarget(entry.Target, entry.Kind);
        var arguments = (entry.Arguments ?? string.Empty).Trim();
        return arguments.Length == 0 ? target : $"{target}\u0000{arguments}";
    }

    private static string NormalizeTarget(string target, AppItemKind kind)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return string.Empty;
        }

        var trimmed = target.Trim();

        if (kind == AppItemKind.Url)
        {
            return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
                ? NormalizeUrl(uri)
                : trimmed.ToLowerInvariant();
        }

        try
        {
            // 把「..」、正斜杠与短名折叠成一种规范形式。
            return Path.GetFullPath(trimmed).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // 不是可用的路径。退回按去除首尾空白后的形式比较，让导入仍能给出
            // 合理结果而不是抛异常。
            return trimmed.ToLowerInvariant();
        }
    }

    /// <summary>
    /// 把一个绝对地址折叠成可比较的形式。
    /// </summary>
    /// <remarks>
    /// 协议与主机折叠大小写；路径保持原样，因为 URL 路径区分大小写。
    ///
    /// <para>
    /// <b>端口必须保留。</b>曾用 <see cref="Uri.Host"/> 折叠地址，它不含端口，
    /// 于是 <c>http://localhost:8001/app</c> 与 <c>http://localhost:8002/app</c>
    /// 被当成同一条——那是两个不同的服务，第二条会被当成重复而拒绝导入。
    /// </para>
    ///
    /// <para>
    /// 只有「显式写出的默认端口」与「省略端口」被视为相同：
    /// <c>https://example.com/</c> 与 <c>https://example.com:443/</c> 是同一个地址的
    /// 两种写法，不该被当成两条。
    /// </para>
    ///
    /// <para>
    /// 刻意<strong>不</strong>把 userinfo（<c>user@host</c>）纳入键：启动台里几乎不会出现
    /// 带凭据的地址，而把它折进去属于没有实测需求的假设。同一条地址写了不同大小写的
    /// userinfo 时会被当成两条——多一条记录比悄悄合并掉一条安全。
    /// </para>
    /// </remarks>
    private static string NormalizeUrl(Uri uri)
    {
        var scheme = uri.Scheme.ToLowerInvariant();
        var host = uri.Host.ToLowerInvariant();
        var port = uri.IsDefaultPort ? string.Empty : $":{uri.Port.ToString(CultureInfo.InvariantCulture)}";

        return $"{scheme}://{host}{port}{uri.PathAndQuery}{uri.Fragment}";
    }
}