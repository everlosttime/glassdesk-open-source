using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace GlassDesk.NativeRendering.Tests;

public sealed class ProbeProcessTests
{
    [Fact]
    public void MissingNativeLibraryProducesFailureEvidenceInsteadOfAnUnhandledStartupExit()
    {
        var root = Directory.GetParent(Path.GetDirectoryName(Api.LibraryPath)!)!.Parent!.FullName;
        var probe = Path.Combine(root, "artifacts", "native-rendering", "probe", "GlassDesk.NativeRenderingProbe.exe");
        var output = Path.Combine(root, "artifacts", "native-rendering", "startup-failure-" + Guid.NewGuid().ToString("N"));
        var start = new ProcessStartInfo(probe) { UseShellExecute = false, CreateNoWindow = true };
        start.Environment["GLASSDESK_NATIVE_RENDER_DLL"] = Path.Combine(output, "does-not-exist.dll");
        foreach (var arg in new[] { "--self-test", "--output", output, "--state-directory", Path.Combine(output, "state") }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        try { Assert.True(process.WaitForExit(10000), "Initialization failure must not hang the desktop."); }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(); } }
        var reportPath = Path.Combine(output, "probe.json");
        Assert.True(File.Exists(reportPath), "A failed native initialization must still produce a diagnostic report.");
        using var report = JsonDocument.Parse(File.ReadAllText(reportPath));
        Assert.False(report.RootElement.GetProperty("passed").GetBoolean());
        Assert.Equal(1, process.ExitCode);
        Assert.False(File.Exists(Path.Combine(output, "state", "native-session-active.json")));
    }

    [Fact]
    public void TransparentProductWindowTransportsAlphaResizesAndRecoversWithoutChangingOwner()
    {
        var root = Directory.GetParent(Path.GetDirectoryName(Api.LibraryPath)!)!.Parent!.FullName;
        var probe = Path.Combine(root, "artifacts", "native-rendering", "probe", "GlassDesk.NativeRenderingProbe.exe");
        Assert.True(File.Exists(probe), "Build the transparent native rendering probe first.");
        var output = Path.Combine(root, "artifacts", "native-rendering", "probe-test-" + Guid.NewGuid().ToString("N"));
        var start = new ProcessStartInfo(probe) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--self-test"); start.ArgumentList.Add("--output"); start.ArgumentList.Add(output);
        start.ArgumentList.Add("--state-directory"); start.ArgumentList.Add(Path.Combine(output, "state"));
        using var process = Process.Start(start)!;
        try { Assert.True(process.WaitForExit(30000), "Native window probe exceeded its deadline."); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        var report = Path.Combine(output, "probe.json");
        Assert.True(File.Exists(report), "Probe must persist evidence even when a check fails.");
        using var json = JsonDocument.Parse(File.ReadAllText(report));
        var result = json.RootElement;
        Assert.True(result.GetProperty("passed").GetBoolean(), result.GetRawText());
        Assert.Equal(0, process.ExitCode);
        Assert.True(result.GetProperty("checks").GetProperty("alphaTransport").GetBoolean());
        Assert.True(result.GetProperty("checks").GetProperty("ownerPreserved").GetBoolean());
        Assert.False(result.GetProperty("keyboardWinDVerified").GetBoolean());
        Assert.False(result.GetProperty("presentedFpsMeasured").GetBoolean());
    }

    [Fact]
    public void KilledNativeProbeCannotReenableItselfWithADifferentReportDirectory()
    {
        var root = Directory.GetParent(Path.GetDirectoryName(Api.LibraryPath)!)!.Parent!.FullName;
        var probe = Path.Combine(root, "artifacts", "native-rendering", "probe", "GlassDesk.NativeRenderingProbe.exe");
        var output = Path.Combine(root, "artifacts", "native-rendering", "crash-test-" + Guid.NewGuid().ToString("N"));
        var state = Path.Combine(output, "state");
        ProcessStartInfo Start(string folder)
        {
            var info = new ProcessStartInfo(probe) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "--output", Path.Combine(output, folder), "--state-directory", state }) info.ArgumentList.Add(arg);
            return info;
        }
        using (var first = Process.Start(Start("first"))!)
        {
            try
            {
                var watch = Stopwatch.StartNew();
                while (!File.Exists(Path.Combine(output, "first", "live-observation.json")) && !first.HasExited && watch.Elapsed < TimeSpan.FromSeconds(10)) Thread.Sleep(20);
                Assert.False(first.HasExited, "First native probe must actually start before the simulated crash.");
                Assert.True(File.Exists(Path.Combine(output, "first", "live-observation.json")));
            }
            finally { if (!first.HasExited) { first.Kill(entireProcessTree: true); first.WaitForExit(); } }
        }
        using var second = Process.Start(Start("second"))!;
        try { Assert.True(second.WaitForExit(5000), "A crash marker must stop a new native session before creating a window."); }
        finally { if (!second.HasExited) { second.Kill(entireProcessTree: true); second.WaitForExit(); } }
        Assert.Equal(2, second.ExitCode);
        using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "second", "probe.json")));
        Assert.True(report.RootElement.GetProperty("nativeDisabledAfterUncleanSession").GetBoolean());
    }
}
