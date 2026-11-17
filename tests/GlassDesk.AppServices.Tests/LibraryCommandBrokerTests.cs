using GlassDesk.App.Startup;
using GlassDesk.Platform.Storage;
using System.IO.Pipes;
using System.Text.Json;

namespace GlassDesk.AppServices.Tests;

public sealed class LibraryCommandBrokerTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"Arguments\":null}")]
    [InlineData("{\"Arguments\":[null]}")]
    [InlineData("{\"Arguments\":[]}")]
    [InlineData("{broken json}")]
    public async Task InvalidArgumentsAreRejectedAndTheNextValidCommandStillRuns(string invalidJson)
    {
        var parent = Path.Combine(Path.GetTempPath(), "GlassDesk-broker-invalid-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(parent, "Data");
        try
        {
            Assert.True(LibraryWriterLease.TryAcquire(root, out var lease));
            var calls = 0;
            await using var broker = new LibraryCommandBroker(root, lease!, _ =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(new LibraryCommandReply(7, "valid"));
            });
            broker.Start();
            var invalid = await SendJsonAsync(root, invalidJson);
            Assert.Equal(2, invalid.ExitCode);
            Assert.Equal(0, calls);
            var valid = await SendJsonAsync(root, "{\"Arguments\":[\"--list-library\"]}");
            Assert.Equal(7, valid.ExitCode);
            Assert.Equal(1, calls);
            broker.Dispose();
        }
        finally { if (Directory.Exists(parent)) Directory.Delete(parent, true); }
    }

    [Fact]
    public async Task StoppingBrokerRetainsWriterLeaseUntilPendingExecutionActuallyFinishes()
    {
        var parent = Path.Combine(Path.GetTempPath(), "GlassDesk-broker-pending-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(parent, "Data");
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete = new TaskCompletionSource<LibraryCommandReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Assert.True(LibraryWriterLease.TryAcquire(root, out var lease));
            await using var broker = new LibraryCommandBroker(root, lease!, request => { entered.SetResult(request.ExecutionCancellation); return complete.Task; });
            broker.Start();
            var response = Task.Run(() => LibraryCommandBroker.Forward(root, ["--list-library"]));
            var executionCancellation = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            broker.Dispose();
            Assert.True(executionCancellation.IsCancellationRequested);
            var acquiredEarly = LibraryWriterLease.TryAcquire(root, out var prematureWriter);
            prematureWriter?.Dispose();
            complete.TrySetResult(new(0, "complete"));
            Assert.False(acquiredEarly);
            await response;
            await broker.DisposeAsync();
            Assert.True(LibraryWriterLease.TryAcquire(root, out var nextWriter));
            nextWriter!.Dispose();
        }
        finally { complete.TrySetResult(new(1, "stopped")); if (Directory.Exists(parent)) Directory.Delete(parent, true); }
    }

    [Fact]
    public async Task FailedHostCommandReturnsErrorAndDoesNotStopTheListener()
    {
        var parent = Path.Combine(Path.GetTempPath(), "GlassDesk-broker-execution-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(parent, "Data");
        try
        {
            Assert.True(LibraryWriterLease.TryAcquire(root, out var lease));
            await using var broker = new LibraryCommandBroker(root, lease!, request => request.Arguments[0] == "fail"
                ? Task.FromException<LibraryCommandReply>(new InvalidOperationException("host unavailable"))
                : Task.FromResult(new LibraryCommandReply(0, "recovered")));
            broker.Start();
            var failed = await SendJsonAsync(root, "{\"Arguments\":[\"fail\"]}");
            Assert.Equal(1, failed.ExitCode);
            var recovered = await SendJsonAsync(root, "{\"Arguments\":[\"valid\"]}");
            Assert.Equal(0, recovered.ExitCode);
            Assert.Equal("recovered", recovered.Report);
        }
        finally { if (Directory.Exists(parent)) Directory.Delete(parent, true); }
    }

    [Fact]
    public async Task ActiveWriterReceivesArgumentsAndReturnsItsActualResult()
    {
        var parent = Path.Combine(Path.GetTempPath(), "GlassDesk-broker-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(parent, "Data");
        try
        {
            Assert.True(LibraryWriterLease.TryAcquire(root, out var lease));
            await using var broker = new LibraryCommandBroker(root, lease!, request =>
            {
                Assert.Equal(new[] { "--import", "C:\\软件\\应用.lnk" }, request.Arguments);
                return Task.FromResult(new LibraryCommandReply(7, "实际宿主结果"));
            });
            broker.Start();
            var reply = await Task.Run(() => LibraryCommandBroker.Forward(root, ["--import", "C:\\软件\\应用.lnk"]));
            Assert.Equal(7, reply.ExitCode);
            Assert.Equal("实际宿主结果", reply.Report);
            broker.Dispose();
        }
        finally { if (Directory.Exists(parent)) Directory.Delete(parent, true); }
    }

    private static async Task<LibraryCommandReply> SendJsonAsync(string root, string json)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var pipe = new NamedPipeClientStream(".", "GlassDesk.Library." + LibraryWriterLease.Identity(root),
            PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(timeout.Token);
        await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, leaveOpen: true);
        await writer.WriteLineAsync(json);
        var line = await reader.ReadLineAsync(timeout.Token);
        Assert.NotNull(line);
        return JsonSerializer.Deserialize(line, LibraryCommandJsonContext.Default.LibraryCommandReply)!;
    }
}
