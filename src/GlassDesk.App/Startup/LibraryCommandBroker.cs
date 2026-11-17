using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;
using GlassDesk.Platform.Storage;

namespace GlassDesk.App.Startup;

internal sealed record LibraryCommandRequest(string[] Arguments)
{
    [JsonIgnore]
    public CancellationToken ExecutionCancellation { get; init; }
}
internal sealed record LibraryCommandReply(int ExitCode, string Report);
[JsonSerializable(typeof(LibraryCommandRequest))]
[JsonSerializable(typeof(LibraryCommandReply))]
internal partial class LibraryCommandJsonContext : JsonSerializerContext;

internal sealed class LibraryCommandBroker : IDisposable, IAsyncDisposable
{
    private readonly object _lifecycle = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly LibraryWriterLease _lease;
    private readonly string _name;
    private readonly Func<LibraryCommandRequest, Task<LibraryCommandReply>> _execute;
    private Task? _listener;
    private bool _disposed;
    public LibraryCommandBroker(string root, LibraryWriterLease lease, Func<LibraryCommandRequest, Task<LibraryCommandReply>> execute)
    { _name = PipeName(root); _lease = lease; _execute = execute; }
    private static string PipeName(string root) => "GlassDesk.Library." + LibraryWriterLease.Identity(root);
    public void Start()
    {
        lock (_lifecycle)
        {
            if (_disposed) return;
            _listener ??= ListenAsync();
        }
    }
    private async Task ListenAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    await using var pipe = new NamedPipeServerStream(_name, PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(30));
                    using var reader = new StreamReader(pipe, leaveOpen: true);
                    await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                    var line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                    LibraryCommandRequest? request = null;
                    if (line is { Length: <= 65536 })
                    {
                        try { request = JsonSerializer.Deserialize(line, LibraryCommandJsonContext.Default.LibraryCommandRequest); }
                        catch (JsonException) { }
                    }
                    var reply = new LibraryCommandReply(2, "无效命令。");
                    if (request?.Arguments is { Length: > 0 } arguments && arguments.All(argument => argument is not null))
                    {
                        timeout.Token.ThrowIfCancellationRequested();
                        try
                        {
                            // The host checks this token again inside its dispatcher callback.
                            // Keep the writer lease until the callback has actually completed.
                            reply = await _execute(request with { ExecutionCancellation = timeout.Token }).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is not OutOfMemoryException)
                        { reply = new(1, "命令失败：" + ex.Message); }
                    }
                    await writer.WriteLineAsync(JsonSerializer.Serialize(reply, LibraryCommandJsonContext.Default.LibraryCommandReply)
                        .AsMemory(), timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
            }
        }
        finally { _lease.Dispose(); }
    }
    public static LibraryCommandReply Forward(string root, string[] arguments)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName(root), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            pipe.ConnectAsync(timeout.Token).GetAwaiter().GetResult();
            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, leaveOpen: true);
            writer.WriteLine(JsonSerializer.Serialize(new LibraryCommandRequest(arguments), LibraryCommandJsonContext.Default.LibraryCommandRequest));
            var line = reader.ReadLineAsync(timeout.Token).AsTask().GetAwaiter().GetResult();
            return line is null ? new(1, "运行中的实例未返回结果。")
                : JsonSerializer.Deserialize(line, LibraryCommandJsonContext.Default.LibraryCommandReply) ?? new(1, "无效响应。");
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException or UnauthorizedAccessException)
        { return new(1, "数据正由另一实例使用，命令未执行：" + ex.Message); }
    }
    public void Dispose()
    {
        lock (_lifecycle)
        {
            if (_disposed) return;
            _disposed = true;
            _stop.Cancel();
            if (_listener is null) _lease.Dispose();
        }
    }
    public async ValueTask DisposeAsync()
    {
        Dispose();
        // Awaiting is safe; synchronously joining a dispatcher-bound command would deadlock shutdown.
        var listener = _listener;
        if (listener is not null) await listener.ConfigureAwait(false);
        _stop.Dispose();
    }
}
