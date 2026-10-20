using System.Text;

namespace GlassDesk.AppServices;

/// <summary>读 <c>.url</c> 文件（Internet 快捷方式）。</summary>
/// <remarks>
/// 这是纯文本的 INI 形式，内容形如：
/// <code>
/// [InternetShortcut]
/// URL=https://example.com/
/// </code>
///
/// <para>
/// 它存在的意义是「网页快捷方式」要有一个文件形态——用户从浏览器把地址拖到桌面
/// 得到的就是这个，而不是 <c>.lnk</c>。不认它的话，
/// 从浏览器拖过来的东西会被当成一个普通文件。
/// </para>
///
/// <para>
/// 自己解析而不是调 <c>GetPrivateProfileString</c>：后者对 UTF-8 支持含糊，
/// 而这些文件的编码并不统一（实测有 ANSI，也有 UTF-8）。
/// </para>
/// </remarks>
public static class InternetShortcutReader
{
    private const string UrlKey = "URL=";

    /// <summary>读出 <c>.url</c> 里的地址；读不出合法地址时返回 null。</summary>
    public static string? ReadUrl(string path)
    {
        string[] lines;

        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        foreach (var raw in lines)
        {
            var line = raw.Trim();

            if (line.StartsWith(';') || line.StartsWith('#'))
            {
                continue;
            }

            if (!line.StartsWith(UrlKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = line[UrlKey.Length..].Trim();
            return IsSupportedUrl(value) ? value : null;
        }

        return null;
    }

    /// <summary>这个地址是我们支持的「网页」吗。</summary>
    /// <remarks>
    /// 只认 http 与 https。<c>file:</c>、<c>mailto:</c>、<c>steam:</c> 之类虽然也是合法 URI，
    /// 但它们不是网页，混进来会让「网页」这一类失去意义——
    /// 而且它们该由关联程序处理，不该被标成浏览器条目。
    /// </remarks>
    public static bool IsSupportedUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    /// <summary>把一段文字写成 <c>.url</c> 文件的内容，供导入网页时落盘使用。</summary>
    public static string Compose(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        var builder = new StringBuilder();
        builder.AppendLine("[InternetShortcut]");
        builder.Append("URL=").AppendLine(url.Trim());
        return builder.ToString();
    }
}