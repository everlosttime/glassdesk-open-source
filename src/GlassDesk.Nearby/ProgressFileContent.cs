using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using GlassDesk.Core.Contracts;

namespace GlassDesk.Nearby;

internal sealed class ProgressFileContent : HttpContent
{
    private readonly FileStream _file;
    private readonly long _length;
    private readonly Action<long> _progress;
    private readonly Action _payloadComplete;
    internal ProgressFileContent(NearbyFile file, Action<long> progress, Action payloadComplete)
    {
        if (file.LocalPath is null) throw new IOException("找不到发送文件。");
        _file = new FileStream(file.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        _length = _file.Length;
        if (_length != file.Size) { _file.Dispose(); throw new IOException("文件大小已改变，请重新添加。"); }
        _progress = progress;
        _payloadComplete = payloadComplete;
        Headers.ContentType = MediaTypeHeaderValue.TryParse(file.ContentType, out var type) ? type : new("application/octet-stream");
        Headers.ContentLength = _length;
    }
    protected override bool TryComputeLength(out long length) { length = _length; return true; }
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);
    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            long length = 0;
            while (true)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                idle.CancelAfter(TimeSpan.FromSeconds(60));
                var read = await _file.ReadAsync(buffer.AsMemory(0, 128 * 1024), idle.Token).ConfigureAwait(false);
                if (read == 0) break;
                await stream.WriteAsync(buffer.AsMemory(0, read), idle.Token).ConfigureAwait(false);
                length += read; _progress(length);
            }
            _payloadComplete();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
    protected override void Dispose(bool disposing) { if (disposing) _file.Dispose(); base.Dispose(disposing); }
}
