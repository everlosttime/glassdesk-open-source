using System.Diagnostics;

namespace GlassDesk.AppServices.Tests;

public sealed class SingleInstanceProcessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GlassDesk-InstanceProcess-" + Guid.NewGuid().ToString("N"));

    public SingleInstanceProcessTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task 独立进程只允许首实例并通过私有管道传递命令()
    {
        var host = GetHostExecutable();
        Assert.True(File.Exists(host), $"Instance process host was not built: {host}");

        var name = "process-test-" + Guid.NewGuid().ToString("N");
        var serverReady = Path.Combine(_root, "server.ready");
        var messageFile = Path.Combine(_root, "received.txt");
        var stopServer = Path.Combine(_root, "server.stop");
        var server = StartHost(host, "server", name, serverReady, messageFile, stopServer);

        try
        {
            await WaitForFileAsync(serverReady, server, TimeSpan.FromSeconds(10));
            Assert.Equal("first", await File.ReadAllTextAsync(serverReady));

            var duplicateReady = Path.Combine(_root, "duplicate.ready");
            var duplicate = StartHost(host, "server", name, duplicateReady, Path.Combine(_root, "unused.txt"), Path.Combine(_root, "unused.stop"));
            using (duplicate)
            {
                await WaitForExitAsync(duplicate, TimeSpan.FromSeconds(5));
                Assert.Equal(2, duplicate.ExitCode);
                Assert.Equal("duplicate", await File.ReadAllTextAsync(duplicateReady));
            }

            using var client = StartHost(host, "send", name, "show-launcher");
            await WaitForExitAsync(client, TimeSpan.FromSeconds(5));
            Assert.Equal(0, client.ExitCode);

            await WaitForFileAsync(messageFile, server, TimeSpan.FromSeconds(5));
            Assert.Equal("show-launcher", await File.ReadAllTextAsync(messageFile));
        }
        finally
        {
            File.WriteAllText(stopServer, "stop");
            if (!server.WaitForExit(5000)) server.Kill(entireProcessTree: true);
            server.Dispose();
        }
    }

    private static Process StartHost(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start)!;
    }

    private static async Task WaitForFileAsync(string path, Process owner, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path)) return;
            if (owner.HasExited)
                throw new InvalidOperationException($"Instance process exited ({owner.ExitCode}): {await owner.StandardError.ReadToEndAsync()}");
            await Task.Delay(25);
        }

        throw new TimeoutException($"Timed out waiting for '{path}'.");
    }

    private static async Task WaitForExitAsync(Process process, TimeSpan timeout)
    {
        if (process.WaitForExit((int)timeout.TotalMilliseconds)) return;

        process.Kill(entireProcessTree: true);
        process.WaitForExit();
        throw new TimeoutException($"Instance helper process timed out: {await process.StandardError.ReadToEndAsync()}");
    }

    private static string GetHostExecutable()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "GlassDesk.slnx"))) repository = repository.Parent;
        Assert.NotNull(repository);

        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name;
        Assert.False(string.IsNullOrWhiteSpace(configuration));
        var framework = new DirectoryInfo(AppContext.BaseDirectory).Name;
        return Path.Combine(repository!.FullName, "tests", "GlassDesk.InstanceProbe", "bin", configuration!, framework, "GlassDesk.InstanceProbe.exe");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
