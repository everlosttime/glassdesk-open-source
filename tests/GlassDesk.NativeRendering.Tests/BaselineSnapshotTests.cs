using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace GlassDesk.NativeRendering.Tests;

public sealed class BaselineSnapshotTests
{
    [Fact]
    public void SnapshotPreservesProfileAndRefusesOverlappingOrExistingDestinations()
    {
        var repo = Directory.GetParent(Path.GetDirectoryName(Api.LibraryPath)!)!.Parent!.FullName;
        var script = Path.Combine(repo, "build", "Initialize-NativeRenderingBaseline.ps1");
        Assert.True(File.Exists(script), "The reproducible baseline snapshot command is missing.");
        var scratch = Path.Combine(Path.GetTempPath(), "GlassDesk-baseline-test-" + Guid.NewGuid().ToString("N"));
        var installed = Path.Combine(scratch, "installed");
        var data = Path.Combine(installed, "Data");
        Directory.CreateDirectory(Path.Combine(data, "icons"));
        File.WriteAllText(Path.Combine(data, "settings.json"), "{\"pinBarsToDesktop\":false,\"reduceMotion\":true}");
        File.WriteAllText(Path.Combine(data, "icons", "custom-example.png"), "profile fixture; not a decoded image");
        var before = SHA256.HashData(File.ReadAllBytes(Path.Combine(data, "settings.json")));
        try
        {
            var lastOutput = "";
            int Run(string output)
            {
                var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                // A pwsh parent can export its PS7 module path to Windows PS5.
                start.Environment["PSModulePath"] = Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\WindowsPowerShell\v1.0\Modules;%ProgramFiles%\WindowsPowerShell\Modules");
                foreach (var arg in new[] { "-NoProfile", "-File", script, "-InstalledDirectory", installed, "-OutputDirectory", output, "-OriginalCheckout", repo }) start.ArgumentList.Add(arg);
                using var process = Process.Start(start)!;
                var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                Assert.True(process.WaitForExit(20000));
                Task.WaitAll(stdout, stderr);
                lastOutput = stdout.Result + stderr.Result;
                return process.ExitCode;
            }
            var output = Path.Combine(scratch, "snapshot");
            Assert.True(Run(output) == 0, lastOutput);
            Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(Path.Combine(output, "profile", "settings.json"))));
            using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "baseline.json")));
            Assert.Equal("2d4bd40095c04d8a3c5d268a3052018d7de5ae18", report.RootElement.GetProperty("sourceCommit").GetString());
            Assert.True(report.RootElement.GetProperty("sourceProfileUnchanged").GetBoolean());
            Assert.Equal(1, Run(output));
            var overlap = Path.Combine(data, "nested-snapshot");
            Assert.Equal(1, Run(overlap));
            Assert.False(Directory.Exists(overlap));
            Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(Path.Combine(data, "settings.json"))));
        }
        finally { Directory.Delete(scratch, recursive: true); }
    }
}
