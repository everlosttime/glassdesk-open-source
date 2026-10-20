using System.IO;
using System.Text;
using GlassDesk.Core.Storage;
using GlassDesk.Platform.Storage;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("GlassDesk.AppServices.Tests")]

namespace GlassDesk.App.Startup;

/// <summary>验收动作的唯一隔离入口：先取得真实数据根保护边界，再创建独占工作区。</summary>
internal static class AcceptanceEntryPoint
{
    public static bool IsValidIsolatedRun(CommandLineOptions options) =>
        (options.SelfTest ^ options.Diagnose) && options.AcceptanceDataRoot is { Length: > 0 }
        && options.AcceptanceReport is { Length: > 0 }
        && options.Verb == AppServiceVerb.None && !options.Exit
        && !options.ShowLauncher && !options.ShowSettings && options.Sample == AppearanceSample.None
        && !options.Help && options.Unknown.Count == 0;

    public static int Run(AppPaths realPaths, string dataRoot, IReadOnlyList<AcceptanceOutput> outputs,
        Func<AcceptanceWorkspace, int> action)
    {
        ArgumentNullException.ThrowIfNull(realPaths);
        ArgumentNullException.ThrowIfNull(outputs);
        ArgumentNullException.ThrowIfNull(action);

        var workspace = Prepare(realPaths, dataRoot, outputs);
        return workspace is null ? 2 : Run(workspace, action);
    }

    /// <summary>异步诊断也必须在排入界面队列之前取得工作区；拒绝时不运行诊断。</summary>
    public static AcceptanceWorkspace? Prepare(AppPaths realPaths, string dataRoot, IReadOnlyList<AcceptanceOutput> outputs)
    {
        try
        {
            return AcceptanceWorkspace.Create(new AcceptanceRequest(realPaths.RootDirectory, dataRoot, outputs));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
        {
            Console.Error.WriteLine($"验收隔离初始化失败（报告路径或数据目录被拒绝）：{ex.Message}");
            return null;
        }
    }

    public static int Run(AcceptanceWorkspace workspace, Func<AcceptanceWorkspace, int> action)
    {
        try
        {
            using (workspace) return action(workspace);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"验收动作或必需输出写入/关闭失败：{ex.Message}");
            return 1;
        }
    }

    public static void WriteText(Stream output, string text)
    {
        using var writer = new StreamWriter(output, new UTF8Encoding(false), 1024, leaveOpen: true);
        writer.Write(text);
        writer.Flush();
    }
}
