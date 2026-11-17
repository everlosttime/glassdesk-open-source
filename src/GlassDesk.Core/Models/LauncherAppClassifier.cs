namespace GlassDesk.Core.Models;

public sealed record LauncherCategory(string Id, string Name);

public static class LauncherCategoryCatalog
{
    public const string Uncategorized = "uncategorized";
    public static IReadOnlyList<LauncherCategory> All { get; } = Array.AsReadOnly<LauncherCategory>(
    [
        new("folders", "文件夹"), new("ppt", "PPT"), new("word", "Word"), new("excel", "Excel"),
        new("text", "TXT"), new("pdf", "PDF"), new("images", "图片"), new("media", "音视频"),
        new("archives", "压缩包"), new("programs", "应用程序"), new("websites", "网站"), new(Uncategorized, "其他"),
    ]);

    // Existing groups keep their stable identity and user-selected names. Only new choices use All.
    private static readonly LauncherCategory[] LegacyCategories =
    [
        new("office-study", "办公学习"), new("social", "社交通讯"), new("games", "游戏"),
        new("development", "开发工具"), new("design", "图像设计"), new("browsers", "浏览器"), new("system", "系统工具"),
    ];

    public static LauncherCategory? Find(string? id)
        => All.FirstOrDefault(category => category.Id == id) ?? LegacyCategories.FirstOrDefault(category => category.Id == id);
}

/// <summary>Classification is based on the resolved item kind and target extension, without reading file contents.</summary>
public sealed record LauncherClassificationInput
{
    public AppItemKind? Kind { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string Target { get; init; } = string.Empty;
    public string? Arguments { get; init; }
    public string? AppUserModelId { get; init; }
    public string? Description { get; init; }
    public string? ProductName { get; init; }
    public string? FileDescription { get; init; }
    public string? CompanyName { get; init; }
}

public sealed record LauncherClassificationResult(string CategoryId, string Reason, int RuleVersion = 1);

public static class LauncherAppClassifier
{
    public const int CurrentRuleVersion = 2;

    public static LauncherClassificationResult Classify(LauncherClassificationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Kind == AppItemKind.Folder) return Result("folders", "文件夹");
        var target = input.Target ?? string.Empty;
        if (input.Kind == AppItemKind.Url || IsWebsite(target)) return Result("websites", "网站地址");
        if (target.StartsWith("steam://", StringComparison.OrdinalIgnoreCase)
            || target.StartsWith("com.epicgames.launcher://", StringComparison.OrdinalIgnoreCase))
            return Result("programs", "应用程序启动入口");

        var category = Path.GetExtension(target).ToLowerInvariant() switch
        {
            ".ppt" or ".pptx" or ".pptm" or ".pps" or ".ppsx" or ".ppsm" or ".pot" or ".potx" or ".potm" or ".odp" or ".dps" or ".dpt" => "ppt",
            ".doc" or ".docx" or ".docm" or ".dot" or ".dotx" or ".dotm" or ".rtf" or ".odt" or ".ott" or ".wps" or ".wpt" => "word",
            ".xls" or ".xlsx" or ".xlsm" or ".xlsb" or ".xlt" or ".xltx" or ".xltm" or ".csv" or ".ods" or ".et" or ".ett" => "excel",
            ".txt" or ".text" or ".log" or ".md" or ".markdown" => "text",
            ".pdf" => "pdf",
            ".jpg" or ".jpeg" or ".png" or ".bmp" or ".gif" or ".webp" or ".avif" or ".heic" or ".heif" or ".tif" or ".tiff" or ".ico" or ".svg" or ".psd" or ".ai" or ".eps" => "images",
            ".mp3" or ".wav" or ".flac" or ".aac" or ".m4a" or ".ogg" or ".opus" or ".wma" or ".ape" or ".aiff" or ".mid" or ".midi" or ".amr"
                or ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".webm" or ".flv" or ".m4v" or ".mpeg" or ".mpg" or ".ts" or ".m2ts" or ".3gp" => "media",
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".bz2" or ".xz" or ".tgz" or ".tbz2" or ".txz" or ".zst" or ".cab" or ".lz" or ".lzma" => "archives",
            ".exe" or ".com" or ".bat" or ".cmd" or ".ps1" or ".vbs" or ".msi" or ".msp" or ".msix" or ".appx" or ".msixbundle" or ".appxbundle" or ".lnk" => "programs",
            _ => null,
        };
        if (category is not null) return Result(category, "按文件类型分类");
        if (input.Kind is AppItemKind.Program or AppItemKind.Shortcut || !string.IsNullOrWhiteSpace(input.AppUserModelId))
            return Result("programs", "应用程序启动入口");
        return Result(LauncherCategoryCatalog.Uncategorized, "其他文件类型");
    }

    private static bool IsWebsite(string target)
        => Uri.TryCreate(target, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static LauncherClassificationResult Result(string category, string reason) => new(category, reason, CurrentRuleVersion);
}
