using System.IO;
using System.Text;
using GlassDesk.Platform;

namespace GlassDesk.App.Startup;

/// <summary>
/// 把报告文本送到人或脚本读得到的地方。
/// </summary>
/// <remarks>
/// 宿主按 WinExe 构建，因此自身没有控制台。当进程由终端启动时，
/// 我们附着到该终端的控制台；无论哪种情况，同样的文本都会
/// 写入产品自己的日志目录下的文件，这正是让自检在完全看不到
/// 控制台的构建脚本里也能用的原因。
/// </remarks>
internal static class ReportWriter
{
    public static void Write(string text, string? logFilePath)
    {
        if (ConsoleAttach.TryAttachToParentConsole())
        {
            // 附着借用了父进程的控制台，但 Console.Out 仍指向旧的、
            // 不可用的流，所以必须重新打开。不带 BOM：这是控制台输出而不是
            // 文件，字节序标记会表现为乱入的字符。
            var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true,
            };

            stdout.Write(text);
            stdout.Write(Environment.NewLine);
            stdout.Flush();
        }

        if (string.IsNullOrWhiteSpace(logFilePath))
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(logFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(logFilePath, text, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 报告绝不应成为让自检失败的那一环。
        }
    }
}