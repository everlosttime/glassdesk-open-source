using System.Text;
using GlassDesk.App.Startup;
using GlassDesk.Core.Storage;

namespace GlassDesk.AppServices.Tests;

public sealed class SelfTestReportTests
{
    [Fact]
    public void 隔离自检报告不再声称写入数据目录中的日志文件()
    {
        var root = Path.Combine(Path.GetTempPath(), "GlassDesk-SelfTestReport-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        using var output = new MemoryStream();

        try
        {
            SelfTest.Run(paths, output);

            var report = Encoding.UTF8.GetString(output.ToArray());
            Assert.Contains("自检报告：内容写入调用方指定的报告流。", report);
            Assert.DoesNotContain(Path.Combine(root, "logs", "selftest.txt"), report);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
