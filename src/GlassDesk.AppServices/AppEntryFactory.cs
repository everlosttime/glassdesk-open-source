using System.Globalization;
using GlassDesk.Core.Contracts;
using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;
using GlassDesk.Platform.Shell;

namespace GlassDesk.AppServices;

/// <summary>把路径或网址变成应用库记录。</summary>
/// <remarks>
/// 只负责「推导」——给一个来源，算出该存成什么。落库、去重与保存由
/// <see cref="ImportService"/> 统一处理，这样「开始」菜单批量导入与用户拖入单个文件
/// 走的是同一条推导逻辑，不会从两个入口得到两种记录。
/// </remarks>
public static class AppEntryFactory
{
    /// <summary>从一次导入请求造出一条记录。</summary>
    public static (AppEntry? Entry, ImportFailureReason Reason, string Message) TryCreate(ImportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.IsUrl ? CreateFromUrl(request) : CreateFromPath(request);
    }

    /// <summary>从一个已经确定的路径造记录，供「开始」菜单批量导入复用。</summary>
    public static (AppEntry? Entry, ImportFailureReason Reason, string Message) TryCreateFromPath(
        string path,
        string? displayNameOverride = null)
        => CreateFromPath(new ImportRequest { Source = path, DisplayName = displayNameOverride });

    private static (AppEntry?, ImportFailureReason, string) CreateFromUrl(ImportRequest request)
    {
        var url = request.Source?.Trim() ?? string.Empty;

        if (!InternetShortcutReader.IsSupportedUrl(url))
        {
            return (null, ImportFailureReason.UnsupportedType, $"不是可用的网页地址：{url}");
        }

        var entry = new AppEntry
        {
            Id = AppLibraryStore.NewId(),
            Kind = AppItemKind.Url,
            Target = url,
            DisplayName = ResolveName(request.DisplayName, DeriveUrlName(url)),
            IconSource = url,
        };

        return (entry, default, string.Empty);
    }

    private static (AppEntry?, ImportFailureReason, string) CreateFromPath(ImportRequest request)
    {
        var source = request.Source?.Trim() ?? string.Empty;

        if (source.Length == 0)
        {
            return (null, ImportFailureReason.NotFound, "没有给出路径。");
        }

        string full;

        try
        {
            full = Path.GetFullPath(source);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (null, ImportFailureReason.NotFound, $"路径无法解析：{source}");
        }

        bool isDirectory;
        bool exists;

        try
        {
            isDirectory = Directory.Exists(full);
            exists = isDirectory || File.Exists(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, ImportFailureReason.AccessDenied, $"读不到这个位置：{full}");
        }

        if (!exists)
        {
            // 导入时来源必须真实存在。条目后来「变」失效是另一回事（用户删掉了目标），
            // 那条路径由 IsTargetMissing 负责，见 LibraryMaintenance。
            return (null, ImportFailureReason.NotFound, $"找不到：{full}");
        }

        var kind = ItemKindDetector.Detect(full, isDirectory);

        return kind switch
        {
            AppItemKind.Shortcut => CreateFromShortcut(request, full),
            AppItemKind.Url => CreateFromInternetShortcut(request, full),
            _ => (CreatePlain(request, full, kind), default, string.Empty),
        };
    }

    /// <summary>
    /// 从 <c>.lnk</c> 造记录。
    /// </summary>
    /// <remarks>
    /// 分三种结果，差别在于「外壳还能不能打开它」：
    /// <list type="bullet">
    /// <item><b>读出了文件目标</b>：存目标、参数、工作目录、图标。这是绝大多数快捷方式。</item>
    /// <item><b>读不出文件目标但快捷方式本身是好的</b>：保留 <c>.lnk</c> 作为目标，
    /// 类型标为快捷方式。UWP 应用与 MSI 广告快捷方式就属于这一类——它们的目标是一个
    /// 应用标识而不是文件路径，「开始」菜单里这类条目占相当一部分。
    /// 把它们丢掉等于丢掉用户的半个开始菜单，而外壳照样能打开它们。</item>
    /// <item><b>连读都读不出来</b>：逐条报告失败，不导入。此时连外壳也无法打开它，
    /// 收下它只会得到一条按下没反应的记录。</item>
    /// </list>
    /// </remarks>
    private static (AppEntry?, ImportFailureReason, string) CreateFromShortcut(ImportRequest request, string shortcutPath)
    {
        var resolved = ShortcutResolver.Resolve(shortcutPath);
        var fallbackName = ItemKindDetector.DeriveDisplayName(shortcutPath, AppItemKind.Shortcut);

        if (resolved is null)
        {
            return (null, ImportFailureReason.ShortcutUnreadable, $"快捷方式读不出来：{shortcutPath}");
        }

        if (!resolved.HasResolvedTarget)
        {
            // 走到这里说明 ID 列表非空（否则 Resolve 会返回 null），只是它指向的
            // 东西没有文件路径——UWP 应用与 MSI 广告快捷方式就是这样。
            // 「开始」菜单里这类条目占相当一部分，丢掉它们等于丢掉用户的半个开始菜单，
            // 而外壳照样能打开它们，所以保留 .lnk 本身作为目标。
            var opaque = new AppEntry
            {
                Id = AppLibraryStore.NewId(),
                Kind = AppItemKind.Shortcut,
                Target = shortcutPath,
                DisplayName = ResolveName(request.DisplayName, fallbackName),
                Arguments = Nullify(resolved.Arguments),
                WorkingDirectory = Nullify(resolved.WorkingDirectory),

                // 图标来源用 .lnk 自己的路径：实测对快捷方式取图标时外壳会尊重它自定义的
                // 图标位置，所以这里得到的就是用户在菜单上看到的那个图标。
                IconSource = shortcutPath,

                // UWP 与打包应用的窗口归属靠这个身份判定（D-09/D-10），见 AppEntry 说明。
                AppUserModelId = resolved.Aumid,
            };

            return (opaque, default, string.Empty);
        }

        if (resolved.TargetIsUrl)
        {
            var urlEntry = new AppEntry
            {
                Id = AppLibraryStore.NewId(),
                Kind = AppItemKind.Url,
                Target = resolved.Target,
                DisplayName = ResolveName(request.DisplayName, fallbackName),
                Arguments = Nullify(resolved.Arguments),
                WorkingDirectory = Nullify(resolved.WorkingDirectory),
                IconSource = ResolveShortcutIconSource(resolved, shortcutPath),
            };

            return (urlEntry, default, string.Empty);
        }

        var target = ResolveShortcutTarget(resolved.Target, resolved.WorkingDirectory, shortcutPath);

        // 目标不存在也照样导入，只是先标成失效：用户可能插着一块还没接上的移动硬盘，
        // 也可能刚把程序卸载了又装回去。这正是 R-07 要求「保留条目而不是让它消失」的场景。
        var exists = ExistsQuietly(target);

        var entry = new AppEntry
        {
            Id = AppLibraryStore.NewId(),
            Kind = exists ? ItemKindDetector.Detect(target, IsDirectoryQuietly(target)) : AppItemKind.Program,
            Target = target,
            DisplayName = ResolveName(request.DisplayName, fallbackName),
            Arguments = Nullify(resolved.Arguments),
            WorkingDirectory = Nullify(resolved.WorkingDirectory),
            IconSource = ResolveShortcutIconSource(resolved, shortcutPath),
            IsTargetMissing = !exists,
            AppUserModelId = resolved.Aumid,
        };

        return (entry, default, string.Empty);
    }

    private static (AppEntry?, ImportFailureReason, string) CreateFromInternetShortcut(ImportRequest request, string path)
    {
        var url = InternetShortcutReader.ReadUrl(path);

        if (url is null)
        {
            // 后缀是 .url 但里面没有可用地址——要么文件坏了，要么它其实是别的东西。
            // 退回按普通文件处理比直接失败有用。
            return (CreatePlain(request, path, AppItemKind.File), default, string.Empty);
        }

        var entry = new AppEntry
        {
            Id = AppLibraryStore.NewId(),
            Kind = AppItemKind.Url,
            Target = url,
            DisplayName = ResolveName(request.DisplayName, ItemKindDetector.DeriveDisplayName(path, AppItemKind.Url)),

            // 网页的图标交给外壳按这个地址去取，而不是取那个 .url 文件的图标。
            IconSource = url,
        };

        return (entry, default, string.Empty);
    }

    private static AppEntry CreatePlain(ImportRequest request, string path, AppItemKind kind)
        => new()
        {
            Id = AppLibraryStore.NewId(),
            Kind = kind,
            Target = path,
            DisplayName = ResolveName(request.DisplayName, ItemKindDetector.DeriveDisplayName(path, kind)),
            IconSource = path,
        };

    /// <summary>
    /// 快捷方式的目标可能是相对路径。
    /// </summary>
    /// <remarks>
    /// <c>IShellLink::GetPath</c> 一般给出绝对路径，但当快捷方式是按相对路径存的时候
    /// （安装程序的常见做法），拿到的是相对于工作目录的形式。
    /// 不解析它的话，一条指向 <c>..\app.exe</c> 的记录会永远显示为失效。
    /// </remarks>
    private static string ResolveShortcutTarget(string target, string workingDirectory, string shortcutPath)
    {
        try
        {
            if (Path.IsPathFullyQualified(target))
            {
                return Path.GetFullPath(target);
            }

            // 先按工作目录拼，再按快捷方式所在目录拼——这正是 Windows 解析相对目标的顺序。
            if (!string.IsNullOrWhiteSpace(workingDirectory))
            {
                var candidate = Path.GetFullPath(Path.Combine(workingDirectory, target));
                if (ExistsQuietly(candidate))
                {
                    return candidate;
                }
            }

            return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(shortcutPath) ?? string.Empty, target));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return target;
        }
    }

    /// <summary>
    /// 决定这条记录的图标从哪儿来。
    /// </summary>
    /// <remarks>
    /// 优先用快捷方式自己写的图标位置：那才是用户在菜单上看到的那个图标。
    /// 自定义图标文件失效时退回目标文件；目标也不在时交给外壳读取快捷方式。
    /// </remarks>
    private static string ResolveShortcutIconSource(ShortcutInfo resolved, string shortcutPath)
    {
        if (!string.IsNullOrWhiteSpace(resolved.IconSource))
        {
            var (iconPath, _) = IconExtractor.SplitIconSource(resolved.IconSource);
            if (ExistsQuietly(iconPath)
                || string.Equals(resolved.IconSource, resolved.Target, StringComparison.OrdinalIgnoreCase))
            {
                return resolved.IconSource;
            }

            if (ExistsQuietly(resolved.Target))
            {
                return resolved.Target;
            }
        }

        return ExistsQuietly(shortcutPath) ? shortcutPath : resolved.Target;
    }

    private static bool ExistsQuietly(string path)
    {
        try
        {
            return File.Exists(path) || Directory.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsDirectoryQuietly(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string ResolveName(string? overrideName, string derived)
        => string.IsNullOrWhiteSpace(overrideName) ? derived : overrideName.Trim();

    /// <summary>
    /// 网址的默认名称取主机名：整条地址当名字太长，界面显示不下。
    /// </summary>
    /// <remarks>
    /// <b>非默认端口要带在名字里。</b>去重键把端口算进去之后，
    /// <c>http://localhost:8001/app</c> 与 <c>:8002</c> 是两条记录；
    /// 名称若仍只写主机，两条在界上都叫 <c>localhost</c>，等于把
    /// 「能区分」的成果又丢掉了。
    /// </remarks>
    private static string DeriveUrlName(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
        {
            return url;
        }

        return uri.IsDefaultPort
            ? uri.Host
            : $"{uri.Host}:{uri.Port.ToString(CultureInfo.InvariantCulture)}";
    }

    private static string? Nullify(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
