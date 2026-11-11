using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace GlassDesk.Dock.Tests;

public sealed class DesktopBackgroundReportTests
{
    [Fact]
    public void ReportContainsOnlyBoundedMetadataAndNoScreenshotsOrUserData()
    {
        var root = Path.Combine(Path.GetTempPath(), "GlassDesk-background-report-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "desktop-background.json");
        var start = new ProcessStartInfo("D:\\dotnet10\\dotnet.exe")
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add(CompatibilityProbePath.File("GlassCaptureProbe.dll"));
        start.ArgumentList.Add("--desktop-background-report");
        start.ArgumentList.Add(path);
        using var process = Process.Start(start)!;
        if (!process.WaitForExit(25000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            Assert.Fail("The metadata collector exceeded its time budget.");
        }
        Assert.Equal(0, process.ExitCode);
        using var report = JsonDocument.Parse(File.ReadAllText(path));
        Assert.False(report.RootElement.GetProperty("screenPixelsIncluded").GetBoolean());
        Assert.False(report.RootElement.GetProperty("windowTitlesIncluded").GetBoolean());
        Assert.False(report.RootElement.GetProperty("wallpaperPathsIncluded").GetBoolean());
        Assert.True(report.RootElement.GetProperty("independentProbe").GetBoolean());
        Assert.False(report.RootElement.GetProperty("applicationMaterialSampled").GetBoolean());
        var wallpaper = report.RootElement.GetProperty("wallpaper");
        Assert.False(string.IsNullOrWhiteSpace(wallpaper.GetProperty("sourceKind").GetString()));
        Assert.Contains(wallpaper.GetProperty("windowsCacheFileAvailable").ValueKind,
            new[] { JsonValueKind.True, JsonValueKind.False });
        Assert.Equal(2, report.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.True(report.RootElement.GetProperty("captureRegion").GetProperty("Width").GetInt32() > 0);
        var windows = report.RootElement.GetProperty("windows");
        Assert.True(windows.GetArrayLength() <= 64);
        foreach (var window in windows.EnumerateArray())
        {
            Assert.InRange(window.GetProperty("coverageFraction").GetDouble(), 0, 1);
            Assert.InRange(window.GetProperty("className").GetString()!.Length, 0, 128);
            Assert.True(window.GetProperty("processId").GetInt32() > 0);
            Assert.False(window.TryGetProperty("title", out _));
            Assert.False(window.TryGetProperty("path", out _));
        }
        Assert.True(report.RootElement.GetProperty("frames").GetInt32() > 0);
        Assert.True(report.RootElement.GetProperty("traces").GetArrayLength() <= 256);
        Assert.Equal(new[] { path }, Directory.GetFiles(root));
    }

    [Fact]
    public void IncidentCollectionAcceptsOnlyValidatedBoundedMetadataAndRemovesFreeText()
    {
        var root = CreateDirectory();
        var source = Path.Combine(root, "incidents");
        var destination = Path.Combine(root, "collected");
        Directory.CreateDirectory(source);
        var incident = new
        {
            timestamp = DateTimeOffset.UtcNow, processId = 42, applicationVersion = "1.4.5.0",
            qpcFrequency = 10_000_000, incidentAtTicks = 100, preWindowSeconds = 30, postWindowSeconds = 3,
            screenPixelsIncluded = false, windowTitlesIncluded = false, note = "Private user content must not be copied",
            events = new[] { new { Stage = "CaptureFailure", Reason = "IOException", AtTicks = 100,
                WindowProcess = 42, WindowClass = "WorkerW", Detail = @"C:\Users\private\wallpaper.png" } }
        };
        File.WriteAllText(Path.Combine(source, "dock-backdrop-valid.json"), JsonSerializer.Serialize(incident));
        File.WriteAllText(Path.Combine(source, "dock-backdrop-pixels.json"),
            JsonSerializer.Serialize(incident).Replace("\"screenPixelsIncluded\":false", "\"screenPixelsIncluded\":true"));
        File.WriteAllText(Path.Combine(source, "dock-backdrop-userdata.json"),
            JsonSerializer.Serialize(incident).Replace("\"events\":", "\"userData\":\"secret\",\"events\":"));
        File.WriteAllText(Path.Combine(source, "dock-backdrop-invalid.json"), "not JSON");
        RunCollectorFunction(root, source, destination);
        var collected = Assert.Single(Directory.GetFiles(destination));
        var content = File.ReadAllText(collected);
        Assert.DoesNotContain("private", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Detail", content, StringComparison.Ordinal);
        using var report = JsonDocument.Parse(content);
        Assert.False(report.RootElement.GetProperty("screenPixelsIncluded").GetBoolean());
        Assert.Equal("WorkerW", report.RootElement.GetProperty("events")[0].GetProperty("WindowClass").GetString());
    }

    [Fact]
    public void IncidentCollectionLimitsFileCountAndRejectsOversizedFiles()
    {
        var root = CreateDirectory();
        var source = Path.Combine(root, "incidents");
        var destination = Path.Combine(root, "collected");
        Directory.CreateDirectory(source);
        var incident = "{\"screenPixelsIncluded\":false,\"windowTitlesIncluded\":false,\"events\":[]}";
        for (var index = 0; index < 12; index++)
            File.WriteAllText(Path.Combine(source, $"dock-backdrop-{index:D2}.json"), incident);
        File.WriteAllText(Path.Combine(source, "dock-backdrop-large.json"), new string(' ', 2 * 1024 * 1024 + 1));
        RunCollectorFunction(root, source, destination);
        var files = new DirectoryInfo(destination).GetFiles();
        Assert.Equal(10, files.Length);
        Assert.True(files.Sum(file => file.Length) <= 20 * 1024 * 1024);
        Assert.DoesNotContain(files, file => file.Name.Contains("large", StringComparison.Ordinal));
    }

    private static string CreateDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "GlassDesk-background-report-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void RunCollectorFunction(string root, string source, string destination)
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "build", "dual-windows-candidate", "Collect-Desktop-Background.ps1")))
            repository = repository.Parent;
        Assert.NotNull(repository);
        var collector = Path.Combine(repository.FullName, "build", "dual-windows-candidate", "Collect-Desktop-Background.ps1");
        var script = Path.Combine(root, "test-collector.ps1");
        static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
        File.WriteAllText(script, "$ErrorActionPreference = 'Stop'\n. " + Quote(collector)
            + " -NoDialog\nCopy-BackdropIncidents -SourceDirectory " + Quote(source)
            + " -DestinationDirectory " + Quote(destination) + "\n", new System.Text.UTF8Encoding(true));
        var start = new ProcessStartInfo("powershell.exe")
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardError = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        if (!process.WaitForExit(15000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            Assert.Fail("The incident metadata collector exceeded its time budget.");
        }
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
    }
}
