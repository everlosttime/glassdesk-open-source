using GlassDesk.App.Startup;

namespace GlassDesk.AppServices.Tests;

public sealed class CrashDiagnosticsTests
{
    [Fact]
    public void FatalReportIsAppendedToTheRequestedDataDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "GlassDesk-CrashTest-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "logs", "crash.log");
        try
        {
            CrashDiagnostics.TryWrite(path, "Dispatcher", new InvalidOperationException("first failure"));
            CrashDiagnostics.TryWrite(path, "AppDomain", new InvalidOperationException("second failure"));
            var report = File.ReadAllText(path);
            Assert.Contains("first failure", report);
            Assert.Contains("second failure", report);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void FatalReportContainsInnerExceptionAndExecutableLocation()
    {
        var report = CrashDiagnostics.FormatReport("Dispatcher", new InvalidOperationException(
            "hide failure", new ArgumentException("panel bounds")));

        Assert.Contains("Dispatcher", report);
        Assert.Contains("hide failure", report);
        Assert.Contains("panel bounds", report);
        Assert.Contains(AppContext.BaseDirectory, report);
    }

    [Fact]
    public void UnwritableDiagnosticPathCannotReplaceTheOriginalCrash()
    {
        var path = Path.GetTempFileName();
        try
        {
            var failure = Record.Exception(() => CrashDiagnostics.TryWrite(
                Path.Combine(path, "crash.log"), "Dispatcher", new InvalidOperationException("original")));
            Assert.Null(failure);
        }
        finally { File.Delete(path); }
    }
}
