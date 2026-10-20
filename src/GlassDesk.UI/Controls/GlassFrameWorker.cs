using System.Diagnostics;

namespace GlassDesk.UI.Controls;

internal sealed class GlassFrameResult(byte[] pixels, double milliseconds, long generation, Action? release = null) : IDisposable
{
    private Action? _release = release;
    internal byte[] Pixels { get; } = pixels;
    internal double Milliseconds { get; } = milliseconds;
    internal long Generation { get; } = generation;
    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}

/// <summary>One CPU frame at a time; hiding invalidates results without queueing obsolete work.</summary>
internal sealed class GlassFrameWorker
{
    private int _busy;
    private long _generation;
    private byte[]? _pixels;
    private byte[]? _scratch;

    internal bool IsBusy => Volatile.Read(ref _busy) != 0;
    internal void Invalidate() => Interlocked.Increment(ref _generation);
    internal bool IsCurrent(GlassFrameResult result) => result.Generation == Interlocked.Read(ref _generation);
    internal void ClearCachedBuffers()
    {
        Interlocked.Exchange(ref _pixels, null);
        Interlocked.Exchange(ref _scratch, null);
    }

    // The consumer releases the lease after WPF has copied the pixels. Until then,
    // no new frame can overwrite them, even while its dispatcher continuation waits.
    internal async Task<GlassFrameResult?> RenderBufferedAsync(int length, Action<byte[], byte[]> render)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return null;
        var generation = Interlocked.Read(ref _generation);
        var leased = false;
        try
        {
            var pixels = _pixels;
            if (pixels?.Length != length) _pixels = pixels = new byte[length];
            var scratch = _scratch;
            if (scratch?.Length != length) _scratch = scratch = new byte[length];
            var result = await Task.Run(() =>
            {
                var started = Stopwatch.GetTimestamp();
                render(pixels, scratch);
                return new GlassFrameResult(pixels, Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    generation, () => Volatile.Write(ref _busy, 0));
            }).ConfigureAwait(false);
            if (generation != Interlocked.Read(ref _generation)) return null;
            leased = true;
            return result;
        }
        finally { if (!leased) Volatile.Write(ref _busy, 0); }
    }

    internal async Task<GlassFrameResult?> RenderAsync(Func<byte[]> render)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return null;
        var generation = Interlocked.Read(ref _generation);
        try
        {
            var result = await Task.Run(() =>
            {
                var started = Stopwatch.GetTimestamp();
                return new GlassFrameResult(render(), Stopwatch.GetElapsedTime(started).TotalMilliseconds, generation);
            }).ConfigureAwait(false);
            return generation == Interlocked.Read(ref _generation) ? result : null;
        }
        finally { Volatile.Write(ref _busy, 0); }
    }
}
