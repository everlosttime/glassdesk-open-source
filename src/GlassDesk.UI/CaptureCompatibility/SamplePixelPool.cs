// Three exact-sized buffers; a slow consumer causes a dropped frame, never growth.
internal sealed class SamplePixelPool : IDisposable
{
    private readonly object _gate = new();
    private readonly List<SamplePixelOwner> _slots = [];
    private int _size;
    private bool _disposed;
    internal SamplePixelLease? TryRent(int bytes)
    {
        if (bytes <= 0 || bytes > 4_000_000 || bytes % 4 != 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        lock (_gate)
        {
            if (_disposed) return null;
            if (_size != bytes) { _slots.Clear(); _size = bytes; }
            var slot = _slots.FirstOrDefault(value => value.References == 0);
            if (slot is null)
            {
                if (_slots.Count == 3) return null;
                slot = new SamplePixelOwner(new byte[bytes], _gate); _slots.Add(slot);
            }
            slot.References++; return new SamplePixelLease(slot);
        }
    }
    public void Dispose() { lock (_gate) { _disposed = true; _slots.Clear(); _size = 0; } }
}
internal sealed class SamplePixelOwner(byte[] pixels, object gate)
{
    internal readonly byte[] Pixels = pixels;
    internal readonly object Gate = gate;
    internal int References;
}
internal sealed class SamplePixelLease(SamplePixelOwner owner) : IDisposable
{
    private SamplePixelOwner? _owner = owner;
    internal byte[] Pixels => _owner?.Pixels ?? throw new ObjectDisposedException(nameof(SamplePixelLease));
    internal SamplePixelLease Acquire()
    {
        var current = _owner ?? throw new ObjectDisposedException(nameof(SamplePixelLease));
        lock (current.Gate)
        {
            if (!ReferenceEquals(current, _owner)) throw new ObjectDisposedException(nameof(SamplePixelLease));
            current.References++; return new(current);
        }
    }
    public void Dispose()
    {
        var current = _owner;
        if (current is null) return;
        lock (current.Gate)
        {
            if (!ReferenceEquals(current, _owner)) return;
            _owner = null; current.References--;
        }
    }
}
