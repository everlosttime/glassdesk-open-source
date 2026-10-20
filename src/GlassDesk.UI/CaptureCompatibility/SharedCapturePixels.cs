using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

// Pixels use an ephemeral mapping; the existing request/reply channel transfers
// ownership before the parent reads and before the helper may write another frame.
internal sealed unsafe class SharedCapturePixels : IDisposable
{
    private readonly MemoryMappedFile _mapping;
    private readonly MemoryMappedViewAccessor _view;
    private byte* _pointer;
    private bool _disposed;
    internal string Name { get; }
    internal int Capacity { get; }

    private SharedCapturePixels(string name, int capacity, bool create)
    {
        if (capacity <= 0 || capacity > 4_000_000) throw new ArgumentOutOfRangeException(nameof(capacity));
        Name = name; Capacity = capacity;
        _mapping = create ? MemoryMappedFile.CreateNew(name, capacity, MemoryMappedFileAccess.ReadWrite)
            : MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.ReadWrite);
        try
        {
            _view = _mapping.CreateViewAccessor(0, capacity, MemoryMappedFileAccess.ReadWrite);
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref _pointer);
            _pointer += _view.PointerOffset;
        }
        catch { _view?.Dispose(); _mapping.Dispose(); throw; }
    }
    internal static SharedCapturePixels Create(int capacity)
        => new("Local\\GlassDeskBackdrop-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N"), capacity, true);
    internal static SharedCapturePixels Open(string name, int capacity) => new(name, capacity, false);
    internal void Write(byte[] pixels)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (pixels.Length <= 0 || pixels.Length > Capacity) throw new ArgumentException("Invalid shared capture size.");
        Marshal.Copy(pixels, 0, (nint)_pointer, pixels.Length);
        Thread.MemoryBarrier();
    }
    internal void Read(byte[] pixels)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (pixels.Length <= 0 || pixels.Length > Capacity) throw new ArgumentException("Invalid shared capture size.");
        Thread.MemoryBarrier();
        Marshal.Copy((nint)_pointer, pixels, 0, pixels.Length);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _pointer = null; _view.Dispose(); _mapping.Dispose();
    }
}
