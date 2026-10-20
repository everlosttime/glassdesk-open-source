namespace GlassDesk.Core.Models;

/// <summary>
/// 应用库条目指向什么。
/// </summary>
public enum AppItemKind
{
    /// <summary>可执行文件。</summary>
    Program = 0,

    /// <summary>Windows 外壳快捷方式（.lnk）。</summary>
    Shortcut = 1,

    /// <summary>目录。</summary>
    Folder = 2,

    /// <summary>通过外壳关联打开的文档或其他文件。</summary>
    File = 3,

    /// <summary>在默认浏览器中打开的 http/https 地址。</summary>
    Url = 4,
}