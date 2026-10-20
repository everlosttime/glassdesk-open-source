using System.Diagnostics;
using GlassDesk.AppServices;
using GlassDesk.Platform.Storage;

namespace GlassDesk.AppServices.Tests;

public sealed class UserDataLeaseProcessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GlassDesk-UserDataLease-" + Guid.NewGuid().ToString("N"));
    private readonly string _dataRoot;

    public UserDataLeaseProcessTests()
    {
        Directory.CreateDirectory(_root);
        _dataRoot = Path.Combine(_root, "isolated-data-root");
    }

    [Fact]
    public async Task 两个进程可持有共享租约且排他申请被拒()
    {
        using var shared = AcquireChild("shared");
        await WaitForFileAsync(shared.ReadyPath, shared.Process);

        Assert.True(UserDataLease.TryAcquireShared(_dataRoot, out var secondShared));
        using (secondShared)
        {
            Assert.NotNull(secondShared);
            Assert.False(UserDataLease.TryAcquireExclusive(_dataRoot, out var exclusive));
            Assert.Null(exclusive);
        }

        shared.StopAndWait();
    }

    [Fact]
    public async Task 排他租约持有期间跨进程的共享和排他申请均被拒()
    {
        using var exclusive = AcquireChild("exclusive");
        await WaitForFileAsync(exclusive.ReadyPath, exclusive.Process);

        Assert.False(UserDataLease.TryAcquireShared(_dataRoot, out var shared));
        Assert.Null(shared);
        Assert.False(UserDataLease.TryAcquireExclusive(_dataRoot, out var secondExclusive));
        Assert.Null(secondExclusive);

        exclusive.StopAndWait();
    }

    [Fact]
    public async Task 持有排他租约的进程异常终止后门禁恢复()
    {
        using var exclusive = AcquireChild("exclusive");
        await WaitForFileAsync(exclusive.ReadyPath, exclusive.Process);

        exclusive.TerminateOwnedProcess();

        Assert.True(UserDataLease.TryAcquireExclusive(_dataRoot, out var recovered));
        using (recovered) Assert.NotNull(recovered);
    }

    [Fact]
    public async Task 另一进程持有共享租约时删除服务拒绝且保留数据()
    {
        Directory.CreateDirectory(_dataRoot);
        var marker = Path.Combine(_dataRoot, "marker.bin");
        var bytes = new byte[] { 0, 1, 2, 0xFF, 42 };
        File.WriteAllBytes(marker, bytes);
        using var shared = AcquireChild("shared");
        await WaitForFileAsync(shared.ReadyPath, shared.Process);

        var result = new UserDataDeletionService(_dataRoot, _dataRoot, Array.Empty<string>()).DeleteAll(confirmed: true);

        Assert.Equal(UserDataDeletionStatus.Busy, result.Status);
        Assert.Equal(bytes, File.ReadAllBytes(marker));
        Assert.True(Directory.Exists(_dataRoot));
        shared.StopAndWait();
    }

    [Fact]
    public async Task 删除独占租约期间新进程无法取得运行租约()
    {
        using var exclusive = AcquireChild("exclusive");
        await WaitForFileAsync(exclusive.ReadyPath, exclusive.Process);
        using var startup = AcquireChild("shared");

        var result = await WaitForReadyContentAsync(startup.ReadyPath, startup.Process);

        Assert.Equal("denied", result);
        Assert.True(startup.Process.WaitForExit(5000));
        Assert.Equal(3, startup.Process.ExitCode);
        exclusive.StopAndWait();
    }

    private ChildLease AcquireChild(string mode)
    {
        var readyPath = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".ready");
        var stopPath = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".stop");
        var start = new ProcessStartInfo(GetProbeExecutable())
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "userdata-lease", mode, _dataRoot, readyPath, stopPath })
            start.ArgumentList.Add(argument);

        var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the controlled lease child process.");
        return new ChildLease(process, readyPath, stopPath);
    }

    private static string GetProbeExecutable()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "GlassDesk.slnx"))) repository = repository.Parent;
        Assert.NotNull(repository);

        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name;
        Assert.False(string.IsNullOrWhiteSpace(configuration));
        var framework = new DirectoryInfo(AppContext.BaseDirectory).Name;
        return Path.Combine(repository!.FullName, "tests", "GlassDesk.InstanceProbe", "bin", configuration!, framework, "GlassDesk.InstanceProbe.exe");
    }

    private static async Task WaitForFileAsync(string path, Process owner)
    {
        Assert.Equal("acquired", await WaitForReadyContentAsync(path, owner));
    }

    private static async Task<string> WaitForReadyContentAsync(string path, Process owner)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path))
            {
                return await File.ReadAllTextAsync(path);
            }
            if (owner.HasExited)
                throw new InvalidOperationException($"Lease child exited ({owner.ExitCode}): {await owner.StandardError.ReadToEndAsync()}");
            await Task.Delay(25);
        }

        throw new TimeoutException($"Timed out waiting for lease child readiness at '{path}'.");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class ChildLease(Process process, string readyPath, string stopPath) : IDisposable
    {
        public Process Process { get; } = process;
        public string ReadyPath { get; } = readyPath;

        public void StopAndWait()
        {
            File.WriteAllText(stopPath, "stop");
            if (!Process.WaitForExit(5000)) TerminateOwnedProcess();
        }

        public void TerminateOwnedProcess()
        {
            if (!Process.HasExited) Process.Kill(entireProcessTree: true);
            Process.WaitForExit();
        }

        public void Dispose()
        {
            if (!Process.HasExited) TerminateOwnedProcess();
            Process.Dispose();
        }
    }
}
