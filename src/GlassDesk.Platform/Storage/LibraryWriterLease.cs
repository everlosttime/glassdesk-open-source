using System.Security.Cryptography;
using System.Text;

namespace GlassDesk.Platform.Storage;

/// <summary>A lifetime single-writer lease, separate from the maintenance lease.</summary>
public sealed class LibraryWriterLease : IDisposable
{
    private readonly FileStream _stream;
    private LibraryWriterLease(FileStream stream) => _stream = stream;
    public static string Identity(string root) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)).ToUpperInvariant())));
    public static bool TryAcquire(string root, out LibraryWriterLease? lease)
    {
        lease = null;
        try
        {
            var parent = Directory.GetParent(Path.GetFullPath(root))?.FullName;
            if (parent is null) return false;
            Directory.CreateDirectory(parent);
            lease = new(new FileStream(Path.Combine(parent, ".glassdesk-writer-" + Identity(root) + ".lease"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
    public void Dispose() => _stream.Dispose();
}
