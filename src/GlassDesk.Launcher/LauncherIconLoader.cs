using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows.Media.Imaging;

namespace GlassDesk.Launcher;

// File decoding and Shell extraction belong to one background STA, never paging's
// Dispatcher. Completed frozen images can be shared by all pages and groups.
internal sealed class LauncherIconLoader : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _cache = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _lru = new();
    private readonly long _budget;
    private long _decodedBytes;
    private readonly BlockingCollection<Work> _queue = new(512);
    private readonly Thread _thread;
    private bool _disposed;
    private bool _suspended;
    private sealed class Entry
    {
        internal readonly TaskCompletionSource<BitmapImage?> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal LinkedListNode<string>? Node;
        internal long Bytes;
    }
    private sealed record Work(string Key, Func<BitmapImage?> Resolve, Entry Entry);

    internal LauncherIconLoader() : this(16L * 1024 * 1024) { }
    internal LauncherIconLoader(long budgetBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(budgetBytes);
        _budget = budgetBytes;
        _thread = new Thread(Run) { IsBackground = true, Name = "GlassDesk launcher icons" };
        _thread.SetApartmentState(ApartmentState.STA); _thread.Start();
    }
    internal Task<BitmapImage?> LoadAsync(string key, Func<BitmapImage?> resolve)
    {
        lock (_gate)
        {
            if (_disposed) return Task.FromResult<BitmapImage?>(null);
            if (_cache.TryGetValue(key, out var cached))
            {
                Touch(cached);
                return cached.Completion.Task;
            }
            var entry = new Entry();
            if (!_queue.TryAdd(new(key, resolve, entry))) return Task.FromResult<BitmapImage?>(null);
            _cache[key] = entry;
            return entry.Completion.Task;
        }
    }
    internal void SetSuspended(bool suspended)
    {
        lock (_gate) { _suspended = suspended; Monitor.PulseAll(_gate); }
    }
    internal (long Bytes, int Cached, int Pending, bool Suspended) InspectCache()
    {
        lock (_gate) return (_decodedBytes, _cache.Count, _queue.Count, _suspended);
    }
    internal void Clear()
    {
        lock (_gate) { _cache.Clear(); _lru.Clear(); _decodedBytes = 0; }
    }
    private void Touch(Entry entry)
    {
        if (entry.Node is not { } node) return;
        _lru.Remove(node); _lru.AddLast(node);
    }
    private void Trim()
    {
        // UI Image.Source references retain visible/dragged pixels independently.
        // Evicting this lookup never removes a displayed icon.
        while ((_decodedBytes > _budget || _lru.Count > 256) && _lru.First is { } oldest)
        {
            _lru.RemoveFirst();
            if (_cache.Remove(oldest.Value, out var entry)) _decodedBytes -= entry.Bytes;
        }
    }
    private void Run()
    {
        foreach (var work in _queue.GetConsumingEnumerable())
        {
            lock (_gate)
            {
                while (_suspended && !_disposed) Monitor.Wait(_gate);
                if (_disposed) { work.Entry.Completion.TrySetResult(null); continue; }
            }
            BitmapImage? image = null;
            try { image = work.Resolve(); }
            catch (Exception error)
            {
                Trace.WriteLine("Launcher icon unavailable: " + error.Message);
            }
            lock (_gate)
            {
                if (_disposed) image = null;
                if (_cache.TryGetValue(work.Key, out var current) && ReferenceEquals(current, work.Entry))
                {
                    current.Bytes = image is null ? 0 : (long)image.PixelWidth * image.PixelHeight * 4;
                    _decodedBytes += current.Bytes;
                    current.Node = _lru.AddLast(work.Key);
                    Trim();
                }
                work.Entry.Completion.TrySetResult(image);
            }
        }
        lock (_gate) _queue.Dispose();
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; _cache.Clear(); _lru.Clear(); _decodedBytes = 0; _queue.CompleteAdding();
            Monitor.PulseAll(_gate);
            // A Shell extension may be blocked. Never join it from Window.Closed.
            while (_queue.TryTake(out var work)) work.Entry.Completion.TrySetResult(null);
        }
    }
}
