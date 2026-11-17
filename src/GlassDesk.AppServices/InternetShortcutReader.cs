using System.Text;
using System.Globalization;
using GlassDesk.Platform.Shell;

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
    internal const int MaximumShortcutBytes = 1024 * 1024;

    /// <summary>读出 <c>.url</c> 里的地址；读不出合法地址时返回 null。</summary>
    public static string? ReadUrl(string path)
    {
        var lines = ReadShortcutLines(path);
        if (lines is null) return null;

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

    /// <summary>Reads an explicit existing local icon from the InternetShortcut section.</summary>
    public static string? ReadIconSource(string shortcutPath)
    {
        try
        {
            var lines = ReadShortcutLines(shortcutPath);
            if (lines is null) return null;
            string? iconFile = null;
            string? iconIndex = null;
            var inSection = false;
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
                if (line.StartsWith('['))
                {
                    var end = line.IndexOf(']');
                    inSection = end > 1 && line[1..end].Trim().Equals("InternetShortcut", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!inSection) continue;
                var separator = line.IndexOf('=');
                if (separator <= 0) continue;
                var key = line[..separator].Trim();
                var value = line[(separator + 1)..].Trim();
                if (key.Equals("IconFile", StringComparison.OrdinalIgnoreCase)) iconFile = value;
                else if (key.Equals("IconIndex", StringComparison.OrdinalIgnoreCase)) iconIndex = value;
            }
            if (string.IsNullOrWhiteSpace(iconFile)) return null;

            // Check the entire filename first: commas and numeric suffixes are legal filename characters.
            var path = ResolveLocalIconFile(iconFile, shortcutPath);
            int? embeddedIndex = null;
            if (path is null)
            {
                var split = IconExtractor.SplitIconSource(iconFile);
                if (split.Index is null) return null;
                path = ResolveLocalIconFile(split.Path, shortcutPath);
                embeddedIndex = split.Index;
            }
            if (path is null) return null;
            var index = int.TryParse(Unquote(iconIndex ?? string.Empty), NumberStyles.Integer, CultureInfo.InvariantCulture, out var explicitIndex)
                ? explicitIndex : embeddedIndex ?? 0;
            // An explicit zero disambiguates a filename that itself ends with a comma and number.
            return index != 0 || IconExtractor.SplitIconSource(path).Index is not null
                ? path + "," + index.ToString(CultureInfo.InvariantCulture) : path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string? ResolveLocalIconFile(string value, string shortcutPath)
    {
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(Unquote(value));
            if (expanded.StartsWith(@"\\", StringComparison.Ordinal)) return null;
            if (Uri.TryCreate(expanded, UriKind.Absolute, out var uri))
            {
                if (!uri.IsFile || uri.IsUnc) return null;
                if (expanded.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) expanded = uri.LocalPath;
            }
            var directory = Path.GetDirectoryName(Path.GetFullPath(shortcutPath))!;
            var fullPath = Path.GetFullPath(expanded, directory);
            return File.Exists(fullPath) ? fullPath : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string Unquote(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length >= 2 && trimmed[0] == trimmed[^1] && trimmed[0] is '\"' or '\''
            ? trimmed[1..^1].Trim() : trimmed;
    }

    internal static string[]? ReadShortcutLines(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = stream.Length;
            if (length > MaximumShortcutBytes) return null;
            var bytes = new byte[(int)length];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1) return null;
            using var content = new MemoryStream(bytes, writable: false);
            using var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
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
