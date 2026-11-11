using System.IO;

namespace GlassDesk.DesktopAcceptance;

/// <summary>
/// 一次驱动运行的共享上下文：产物路径、工作目录与报告。
/// </summary>
/// <remarks>
/// 所有临时文件都落在 <c>%TEMP%\GlassDesk-桌面驱动-&lt;本次唯一&gt;</c> 下，
/// 不往桌面、文档或产品数据目录里放任何东西。
/// </remarks>
internal sealed class DriverContext
{
    internal DriverContext(string productPath, string fixturePath, string workRoot, Report report)
    {
        ProductPath = productPath;
        FixturePath = fixturePath;
        WorkRoot = workRoot;
        Report = report;
    }

    internal string ProductPath { get; }

    internal string FixturePath { get; }

    internal string WorkRoot { get; }

    internal Report Report { get; }

    /// <summary>本轮专属的工作目录。每一轮都换一个，避免上一轮的残留影响下一轮。</summary>
    internal string NewWorkDirectory(string label)
    {
        var directory = Path.Combine(WorkRoot, $"{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    internal static string DefaultWorkRoot() =>
        Path.Combine(Path.GetTempPath(), WorkRootPrefix + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// 本驱动所有工作目录的公共前缀。
    /// </summary>
    /// <remarks>
    /// 应用库护栏用它区分「本驱动自己造的文件」与「用户的真实数据」。
    /// 注意是前缀而不是本次的目录名：上一轮留下的条目也在同一个前缀下，那属于可以接受的历史。
    /// </remarks>
    internal const string WorkRootPrefix = "GlassDesk-桌面驱动-";

    /// <summary>
    /// 本驱动工作目录的<strong>绝对</strong>前缀，供应用库护栏做归属判断。
    /// </summary>
    /// <remarks>
    /// 必须是绝对路径：库里的目标写的是完整路径，拿一个相对前缀去 StartsWith 永远不匹配，
    /// 于是本驱动自己上一轮造的文件会被当成用户的真实数据（实测踩过这个坑）。
    /// </remarks>
    internal static string WorkRootFamily => Path.Combine(Path.GetTempPath(), WorkRootPrefix);
}