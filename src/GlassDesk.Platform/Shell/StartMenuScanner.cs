namespace GlassDesk.Platform.Shell;

/// <summary>「开始」菜单里的一个快捷方式。</summary>
/// <param name="Path"><c>.lnk</c> 文件本身的位置。</param>
/// <param name="Name">不含扩展名的名称，也就是菜单上显示的文字。</param>
/// <param name="Group">所在子目录（如「附件」）；直接在根下时为空串。</param>
/// <param name="FromCurrentUser">来自当前用户的菜单，而不是所有用户的那份。</param>
public sealed record StartMenuShortcut(string Path, string Name, string Group, bool FromCurrentUser);

/// <summary>一次扫描的结果。</summary>
/// <param name="Shortcuts">去重后的快捷方式。</param>
/// <param name="ScannedFileCount">看到的 <c>.lnk</c> 文件总数（去重之前）。</param>
/// <param name="DuplicateNameCount">因同名而让位给当前用户版本的数量。</param>
/// <param name="Roots">实际扫描的根目录，便于诊断。</param>
public sealed record StartMenuScanResult(
    IReadOnlyList<StartMenuShortcut> Shortcuts,
    int ScannedFileCount,
    int DuplicateNameCount,
    IReadOnlyList<string> Roots);

/// <summary>
/// 扫描「开始」菜单里的程序快捷方式。
/// </summary>
/// <remarks>
/// 两个根：所有用户的 <c>Programs</c> 与当前用户的 <c>Programs</c>。这与「开始」菜单
/// 自己看到的范围一致——只扫其中一个会漏掉一半应用。
///
/// <para>
/// <b>同名去重按「当前用户优先」。</b>这正是 Windows 自己的规则：用户菜单里的同名项
/// 覆盖机器范围的同名项。不这么做的话，安装程序给所有用户装的项与自己装的项会同时出现，
/// 界面上就是两条一模一样的「记事本」。
/// </para>
///
/// <para>
/// 去重只看文件名，不看目标——目标级去重是导入时 <c>AppEntryKey</c> 的职责，
/// 两者解决的是不同的问题：这里解决「菜单上看着重复」，那里解决「库里存着重复」。
/// </para>
/// </remarks>
public static class StartMenuScanner
{
    private const string ShortcutExtension = ".lnk";

    /// <summary>扫描默认的两个「开始」菜单目录。</summary>
    public static StartMenuScanResult Scan()
    {
        var roots = new List<string>();
        var candidates = new List<StartMenuShortcut>();

        // 先扫机器范围的，再扫用户自己的；后面的同名项覆盖前面的。
        Collect(KnownFolders.CommonPrograms, fromCurrentUser: false, roots, candidates);
        Collect(KnownFolders.Programs, fromCurrentUser: true, roots, candidates);

        var scanned = candidates.Count;

        // 用完整相对路径做键，让「Group\Name」这一对唯一。不同分组下的同名项是两项，
        // 不该被合并——那正是「开始」菜单组织应用的方式。
        var byKey = new Dictionary<string, StartMenuShortcut>(StringComparer.OrdinalIgnoreCase);

        foreach (var shortcut in candidates)
        {
            var fileName = Path.GetFileNameWithoutExtension(shortcut.Path);
            var key = shortcut.Group.Length == 0 ? fileName : $"{shortcut.Group}\\{fileName}";
            byKey[key] = shortcut;
        }

        var result = byKey.Values
            .OrderBy(item => item.Group, StringComparer.CurrentCulture)
            .ThenBy(item => item.Name, StringComparer.CurrentCulture)
            .ToList();

        return new StartMenuScanResult(result, scanned, scanned - result.Count, roots);
    }

    private static void Collect(string? root, bool fromCurrentUser, List<string> roots, List<StartMenuShortcut> into)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return;
        }

        roots.Add(root);

        IEnumerable<string> files;

        try
        {
            // 递归枚举「开始」菜单不会很深，但里面的目录链接可能成环，
            // 因此跳过重解析点，靠这一条避免无限递归。
            files = EnumerateShortcuts(root);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return;
        }

        foreach (var file in files)
        {
            var name = ShellDisplayNames.GetFileName(file, removeExtension: true);
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var group = DescribeGroup(root, file);
            into.Add(new StartMenuShortcut(file, name, group, fromCurrentUser));
        }
    }

    private static IEnumerable<string> EnumerateShortcuts(string root)
    {
        var pending = new Stack<string>([root]);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();

            string[] files;

            try
            {
                files = Directory.GetFiles(directory, "*" + ShortcutExtension);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }

            string[] children;

            try
            {
                children = Directory.GetDirectories(directory);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            foreach (var child in children)
            {
                var attributes = File.GetAttributes(child);

                // 目录链接/联接点会让递归成环。跳过而不是靠深度计数兜底。
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                pending.Push(child);
            }
        }
    }

    /// <summary>算出快捷方式相对于根目录的所属分组。</summary>
    private static string DescribeGroup(string root, string file)
    {
        var relative = Path.GetRelativePath(root, file);
        var separator = relative.LastIndexOf(Path.DirectorySeparatorChar);

        if (separator <= 0)
        {
            return string.Empty;
        }

        // 多级目录压成「一级 / 二级」，让界面能一行显示又有层次。
        return relative[..separator].Replace(Path.DirectorySeparatorChar, '/');
    }
}
