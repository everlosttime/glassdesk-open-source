using System.Diagnostics;
using System.Security.Cryptography;
using GlassDesk.Core.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace GlassDesk.Nearby;

public sealed partial class NearbyTransferService
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "The owning service disposes the linked cancellation source after its writer drains. SemaphoreSlim has no WaitHandle consumer and must remain usable by late handlers.")]
    private sealed class ReceiveSession
    {
        internal ReceiveSession(string peer, string address, IReadOnlyList<NearbyFile> files, CancellationToken lifetime)
        {
            Peer = peer; Address = address; Files = files;
            Cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime); Token = Cancellation.Token;
        }
        internal readonly string Id = Guid.NewGuid().ToString("N");
        internal readonly string Peer, Address;
        internal readonly IReadOnlyList<NearbyFile> Files;
        internal readonly CancellationTokenSource Cancellation;
        internal readonly CancellationToken Token;
        internal readonly TaskCompletionSource<bool> Decision = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly SemaphoreSlim Writer = new(1, 1);
        internal readonly Dictionary<string, string> Tokens = new(StringComparer.Ordinal);
        internal readonly HashSet<string> Completed = new(StringComparer.Ordinal);
        internal ReceiveBatch? Batch;
        internal int Finished;
        internal long LastActivity = Stopwatch.GetTimestamp(), Transferred;
        internal readonly Stopwatch Watch = Stopwatch.StartNew();
    }

    public Task DecideAsync(string requestId, bool accept, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) if (_receive?.Id == requestId) _receive.Decision.TrySetResult(accept);
        return Task.CompletedTask;
    }

    private async Task<IResult> PrepareAsync(HttpContext context)
    {
        var body = await context.Request.ReadFromJsonAsync<PrepareRequest>(NearbyJson.Options, context.RequestAborted).ConfigureAwait(false);
        var certificate = await context.Connection.GetClientCertificateAsync(context.RequestAborted).ConfigureAwait(false);
        if (body?.Info is null || body.Files is null || body.Files.Count is 0 or > 10000 || certificate is null) return Results.BadRequest();
        NearbyFile[] files;
        long total;
        try
        {
            files = body.Files.Select(pair => pair.Value is not null && pair.Key == pair.Value.Id ? pair.Value.ToFile() : throw new ArgumentException("文件标识无效。")).ToArray();
            foreach (var file in files) { ReceiveBatch.ValidateName(file.Name); if (file.Size < 0) return Results.BadRequest(); }
            total = files.Aggregate(0L, (sum, f) => checked(sum + f.Size));
        }
        catch (Exception error) when (error is ArgumentException or OverflowException) { return Results.BadRequest(); }
        var peer = CertificateIdentity.Fingerprint(certificate);
        var session = new ReceiveSession(peer, context.Connection.RemoteIpAddress!.ToString(), files, _lifetime!.Token);
        lock (_gate)
        {
            if (_receive is not null) { session.Cancellation.Dispose(); return Results.StatusCode(409); }
            _receive = session;
        }
        Update(s => s with
        {
            Incoming = new(session.Id, body.Info.Alias, files, _receiveDirectory, DateTimeOffset.UtcNow + options.DecisionTimeout),
            Receiving = new(session.Id, true, body.Info.Alias, NearbyTaskState.Waiting, "等待确认", 0, total, 0, null, [], _receiveDirectory),
        });
        try
        {
            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, session.Token);
            var accept = await session.Decision.Task.WaitAsync(options.DecisionTimeout, waiting.Token).ConfigureAwait(false);
            if (!accept) { await FinishReceiveAsync(session, NearbyTaskState.Cancelled, "已拒收。").ConfigureAwait(false); return Results.StatusCode(403); }
            await session.Writer.WaitAsync(waiting.Token).ConfigureAwait(false);
            try
            {
                waiting.Token.ThrowIfCancellationRequested();
                session.Batch = new ReceiveBatch(_receiveDirectory, files);
                foreach (var file in files) session.Tokens.Add(file.Id, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
            }
            finally { session.Writer.Release(); }
            waiting.Token.ThrowIfCancellationRequested();
            session.Watch.Restart();
            Update(s => s with { Incoming = s.Incoming?.Id == session.Id ? null : s.Incoming,
                Receiving = s.Receiving?.Id == session.Id ? s.Receiving with { CurrentFile = "等待文件" } : s.Receiving });
            _ = WatchReceiveAsync(session);
            return Results.Json(new PrepareResponse(session.Id, session.Tokens), NearbyJson.Options);
        }
        catch (Exception error) when (error is OperationCanceledException or TimeoutException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            await FinishReceiveAsync(session, error is IOException or UnauthorizedAccessException or ArgumentException ? NearbyTaskState.Failed : NearbyTaskState.Cancelled,
                error is TimeoutException ? "两分钟未确认，已拒收。" : error is OperationCanceledException ? "接收已取消。" : error.Message).ConfigureAwait(false);
            return Results.StatusCode(error is IOException or UnauthorizedAccessException ? 500 : 403);
        }
    }

    private async Task<IResult> UploadAsync(HttpContext context)
    {
        var session = await AuthorizedSessionAsync(context).ConfigureAwait(false);
        var id = context.Request.Query["fileId"].ToString();
        var token = context.Request.Query["token"].ToString();
        if (session is null || session.Batch is null || !session.Tokens.TryGetValue(id, out var expected)
            || token.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(token), System.Text.Encoding.ASCII.GetBytes(expected)))
            return Results.StatusCode(403);
        if (!await session.Writer.WaitAsync(0, context.RequestAborted).ConfigureAwait(false)) return Results.StatusCode(409);
        var finish = false; string? failure = null; var status = 200;
        try
        {
            if (session.Finished != 0 || session.Completed.Contains(id)) return Results.StatusCode(409);
            var file = session.Files.First(f => f.Id == id);
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            {
                // Kestrel counts chunk framing as well as payload. Keep a bounded allowance
                // for one-byte chunks; ReceiveBatch independently enforces the exact decoded length.
                limit.MaxRequestBodySize = context.Request.ContentLength.HasValue ? file.Size
                    : file.Size <= (long.MaxValue - 65536) / 8 ? file.Size * 8 + 65536 : long.MaxValue;
            }
            if (context.Request.ContentLength is { } length && length != file.Size) { failure = "文件声明大小不符。"; status = 400; }
            else
            {
                using var cancel = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, session.Token);
                var before = session.Transferred;
                await session.Batch.WriteAsync(id, context.Request.Body, amount =>
                {
                    session.LastActivity = Stopwatch.GetTimestamp();
                    session.Transferred = before + amount;
                    Update(s => s with { Receiving = s.Receiving! with { State = NearbyTaskState.Transferring, CurrentFile = file.Name,
                        Transferred = before + amount, BytesPerSecond = (before + amount) / Math.Max(.01, session.Watch.Elapsed.TotalSeconds) } });
                }, cancel.Token).ConfigureAwait(false);
                session.Completed.Add(id);
                session.LastActivity = Stopwatch.GetTimestamp();
                finish = session.Completed.Count == session.Files.Count;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException)
        { failure = error is OperationCanceledException ? "传输已中断或取消。" : error.Message; status = error is ChecksumMismatchException ? 422 : 500; }
        finally { session.Writer.Release(); }
        if (finish || failure is not null) await FinishReceiveAsync(session, finish ? NearbyTaskState.Completed : NearbyTaskState.Failed, failure).ConfigureAwait(false);
        return Results.StatusCode(status);
    }

    private async Task<ReceiveSession?> AuthorizedSessionAsync(HttpContext context)
    {
        ReceiveSession? session; lock (_gate) session = _receive;
        var certificate = await context.Connection.GetClientCertificateAsync(context.RequestAborted).ConfigureAwait(false);
        return session is not null && session.Finished == 0 && certificate is not null
            && session.Id == context.Request.Query["sessionId"] && session.Address == context.Connection.RemoteIpAddress?.ToString()
            && session.Peer.Equals(CertificateIdentity.Fingerprint(certificate), StringComparison.OrdinalIgnoreCase) ? session : null;
    }

    private async Task<IResult> RemoteCancelAsync(HttpContext context)
    {
        var session = await AuthorizedSessionAsync(context).ConfigureAwait(false);
        if (session is null) return Results.StatusCode(403);
        await FinishReceiveAsync(session, NearbyTaskState.Cancelled, "对方已取消。").ConfigureAwait(false);
        return Results.Ok();
    }

    private async Task WatchReceiveAsync(ReceiveSession session)
    {
        try
        {
            var token = session.Token;
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                if (Stopwatch.GetElapsedTime(Volatile.Read(ref session.LastActivity)) >= TimeSpan.FromSeconds(60))
                { await FinishReceiveAsync(session, NearbyTaskState.Failed, "连续 60 秒未收到数据，请重试。").ConfigureAwait(false); return; }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task FinishReceiveAsync(ReceiveSession session, NearbyTaskState state, string? error)
    {
        if (Interlocked.Exchange(ref session.Finished, 1) != 0) return;
        session.Cancellation.Cancel(); session.Decision.TrySetResult(false);
        await session.Writer.WaitAsync().ConfigureAwait(false);
        try { session.Batch?.Dispose(); session.Batch = null; }
        finally { session.Writer.Release(); }
        lock (_gate) if (ReferenceEquals(_receive, session)) _receive = null;
        Update(s => s with { Incoming = s.Incoming?.Id == session.Id ? null : s.Incoming, Receiving = s.Receiving?.Id == session.Id ? s.Receiving with { State = state, Error = error,
            BytesPerSecond = 0, FailedFiles = session.Files.Where(f => !session.Completed.Contains(f.Id)).ToArray() } : s.Receiving });
        session.Cancellation.Dispose();
    }
}
