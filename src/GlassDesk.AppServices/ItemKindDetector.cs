using GlassDesk.Core.Models;
using GlassDesk.Platform.Shell;

namespace GlassDesk.AppServices;

/// <summary>识别一样东西是什么，以及它该叫什么名字。</summary>
/// <remarks>
/// 单独拿出来是因为导入与「开始」菜单扫描都要用同一套判断，
/// 而两处如果各写一份，结果就是同一个文件从菜单进来和从拖放进来
/// 变成两条不同的记录。
/// </remarks>
public static class ItemKindDetector
{
    /// <summary>按扩展名当可执行程序处理的类型。</summary>
    /// <remarks>
    /// 只收真正会「启动一个程序」的扩展名。<c>.msi</c> 刻意不算：
    /// 它启动的是安装程序，把它标成程序会让「已运行就激活」去匹配安装进程。
    /// </remarks>
    private static readonly HashSet<string> ProgramExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".ps1",
    };

    /// <summary>Windows 快捷方式。</summary>
    public const string ShortcutExtension = ".lnk";

    /// <summary>Internet 快捷方式（纯文本的 <c>.url</c> 文件）。</summary>
    public const string InternetShortcutExtension = ".url";

    /// <summary>
    /// 判断一个路径属于哪一类。
    /// </summary>
    /// <remarks>
    /// 目录优先于扩展名——一个叫 <c>工具.exe</c> 的文件夹仍然是文件夹。
    /// </remarks>
    public static AppItemKind Detect(string path, bool isDirectory)
    {
        if (isDirectory)
        {
            return AppItemKind.Folder;
        }

        var extension = Path.GetExtension(path);

        if (string.Equals(extension, ShortcutExtension, StringComparison.OrdinalIgnoreCase))
        {
            return AppItemKind.Shortcut;
        }

        if (string.Equals(extension, InternetShortcutExtension, StringComparison.OrdinalIgnoreCase))
        {
            return AppItemKind.Url;
        }

        return ProgramExtensions.Contains(extension) ? AppItemKind.Program : AppItemKind.File;
    }

    /// <summary>
    /// 推导用户看到的名称。
    /// </summary>
    /// <remarks>
    /// 程序与快捷方式去掉扩展名：用户认的是「记事本」，不是「notepad.exe」。
    /// 文档与文件夹保留全名：<c>季度报告.xlsx</c> 里的扩展名本身就是有用的信息，
    /// 去掉后反而认不出来。
    /// </remarks>
    public static string DeriveDisplayName(string path, AppItemKind kind)
    {
        if (kind is AppItemKind.Program or AppItemKind.Shortcut or AppItemKind.Url or AppItemKind.Folder)
            return ShellDisplayNames.GetFileName(path, removeExtension: kind != AppItemKind.Folder);

        // 文档始终保留扩展名，不受 Explorer 的隐藏扩展名设置影响。
        var name = Path.GetFileName(path);

        if (string.IsNullOrWhiteSpace(name))
        {
            // 根目录（如 C:\）没有文件名部分，退回完整路径，总比空名字强。
            return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        return name;
    }
}
