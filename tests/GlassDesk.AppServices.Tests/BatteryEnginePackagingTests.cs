using System.Diagnostics;
using System.Text.Json.Nodes;

namespace GlassDesk.AppServices.Tests;

public sealed class BatteryEnginePackagingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GlassDesk-BatteryPackaging-" + Guid.NewGuid().ToString("N"));

    public BatteryEnginePackagingTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData("vendorFiles", "vendor")]
    [InlineData("componentFiles", "source")]
    [InlineData("payloadFiles", "payload")]
    public async Task CopyRejectsModifiedInputsBeforeExecutingTheRuntime(string records, string expectedContext)
    {
        var repo = FindRepositoryRoot();
        var document = JsonNode.Parse(File.ReadAllText(Path.Combine(repo, "build", "battery-engine.lock.json")))!;
        document[records]!.AsArray()[0]!["sha256"] = new string('0', 64);
        var lockPath = Path.Combine(_root, "invalid.lock.json");
        File.WriteAllText(lockPath, document.ToJsonString());
        var output = Path.Combine(_root, "output");
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // dotnet may inherit PowerShell 7's module path; let Windows PowerShell
        // initialize its own built-in modules (including Get-FileHash).
        start.Environment.Remove("PSModulePath");
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File",
                     Path.Combine(repo, "build", "Copy-BatteryEngine.ps1"), "-OutputDirectory", output, "-LockFile", lockPath })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
        var text = await stdout + await stderr;
        Assert.NotEqual(0, process.ExitCode);
        Assert.True(text.Contains($"{expectedContext} checksum mismatch", StringComparison.OrdinalIgnoreCase), text);
        Assert.DoesNotContain("isolated offline", text, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(output, "components", "battery-engine")));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GlassDesk.slnx"))) directory = directory.Parent;
        Assert.NotNull(directory);
        return directory.FullName;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
