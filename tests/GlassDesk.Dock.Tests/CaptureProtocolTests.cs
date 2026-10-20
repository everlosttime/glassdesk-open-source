using System.Diagnostics;
using System.Reflection;
using System.IO;

namespace GlassDesk.Dock.Tests;

public sealed class CaptureProtocolTests
{
    [Theory]
    [InlineData("initial-hang")]
    [InlineData("read-hang")]
    [InlineData("split-hang")]
    [InlineData("old-sequence")]
    [InlineData("old-generation")]
    [InlineData("old-source")]
    [InlineData("wrong-protocol")]
    [InlineData("wrong-width")]
    [InlineData("wrong-crop")]
    [InlineData("future-source")]
    public void BrokenWorkerIsBoundedAndRejectedWithoutLeakingTheChild(string fault)
    {
        var ctor = typeof(IsolatedDesktopSceneCapture).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
            null, [typeof(ScreenRect), typeof(ProcessStartInfo)], null);
        Assert.NotNull(ctor);
        var executable = CompatibilityProbePath.File("GlassCaptureProbe.exe");
        var start = new ProcessStartInfo(executable);
        start.ArgumentList.Add("--capture-protocol-fixture"); start.ArgumentList.Add(fault);
        var timer = Stopwatch.StartNew();
        var pidPath = Path.Combine(Path.GetTempPath(), "GlassDesk-protocol-" + Guid.NewGuid().ToString("N") + ".pid");
        start.Environment["GLASSDESK_PROTOCOL_PID_FILE"] = pidPath;
        IDesktopSceneCapture? capture = null;
        try
        {
            var error = Record.Exception(() =>
            {
                capture = (IDesktopSceneCapture)ctor.Invoke([new ScreenRect(0, 0, 2, 2), start]);
                capture.TryRead(sourceNotBeforeTicks: Stopwatch.GetTimestamp());
            });
            Assert.NotNull(error);
            if (error is TargetInvocationException invocation) error = invocation.InnerException;
            Assert.True(error is TimeoutException or InvalidDataException, error.ToString());
        }
        finally { capture?.Dispose(); }
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(4.5), timer.Elapsed.ToString());
        Assert.True(File.Exists(pidPath));
        var pid = int.Parse(File.ReadAllText(pidPath));
        Assert.DoesNotContain(pid, CaptureProcessFamily.KnownProcesses);
        try { using var process = Process.GetProcessById(pid); Assert.True(process.HasExited); }
        catch (ArgumentException) { } // Confirmed gone from the process table.
        File.Delete(pidPath);
    }
}
