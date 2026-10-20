using System.Reflection;
using System.IO;
using System.Windows;

namespace GlassDesk.Dock.Tests;

public sealed class CaptureWorkerBootstrapTests
{
    [Fact]
    public void InvalidWorkerParentExitsWithoutConstructingTheDesktopApplication()
    {
        StaTest.Run(() =>
        {
            var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
            var assembly = Assembly.LoadFrom(Path.Combine(repo,
                "src/GlassDesk.App/bin/Release/net10.0-windows10.0.26100.0/GlassDesk.dll"));
            var entry = assembly.EntryPoint!;
            Assert.Equal(typeof(string[]), Assert.Single(entry.GetParameters()).ParameterType);
            Assert.Null(Application.Current);
            Assert.Equal(2, entry.Invoke(null, [new[] { "--glass-backdrop-worker", "0" }]));
            Assert.Null(Application.Current);
        });
    }
}
