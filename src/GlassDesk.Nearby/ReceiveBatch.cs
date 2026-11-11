using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using GlassDesk.Core.Contracts;
using Microsoft.Win32.SafeHandles;

namespace GlassDesk.Nearby;

public sealed class ChecksumMismatchException() : IOException("文件 SHA-256 校验失败。");

/// <summary>Owns directory pins and private temporary files for one approved receive.</summary>
public sealed class ReceiveBatch : IDisposable
{
    private readonly Dictionary<string, (NearbyFile File, string Path)> _files = new(StringComparer.Ordinal);
    private readonly List<SafeFileHandle> _pins = [];
    private readonly HashSet<string> _pinned = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _completed = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _writer = new(1, 1);
    public string DirectoryPath { get; }

    public ReceiveBatch(string directory, IReadOnlyList<NearbyFile> files)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (files.Count == 0 || files.Count > 10000) throw new ArgumentException("文件数量应为 1–10000。");
        // Validate the entire manifest before creating any directories.
        foreach (var file in files)
        {
            ValidateName(file.Name);
            if (file.Size < 0 || string.IsNullOrEmpty(file.Id)) throw new ArgumentException("无效的文件信息。");
            if (file.Sha256 is { } sha && (sha.Length != 64 || !sha.All(Uri.IsHexDigit))) throw new ArgumentException("无效的校验值。");
        }
        if (files.Select(f => f.Id).Distinct(StringComparer.Ordinal).Count() != files.Count)
            throw new ArgumentException("重复的文件标识。");
        var names = files.Select(f => f.Name.Replace('\\', '/')).ToArray();
        var pathNames = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
            for (var slash = name.LastIndexOf('/'); slash >= 0; slash = name.LastIndexOf('/', slash - 1))
                if (pathNames.Contains(name[..slash])) throw new ArgumentException("文件与目录路径冲突。");
        DirectoryPath = Path.GetFullPath(directory);
        try
        {
            EnsurePinnedDirectory(DirectoryPath);
            var total = files.Aggregate(0L, (sum, f) => checked(sum + f.Size));
            var drive = new DriveInfo(Path.GetPathRoot(DirectoryPath)!);
            if (drive.AvailableFreeSpace < total) throw new IOException("接收磁盘空间不足。");
            var probe = Path.Combine(DirectoryPath, ".glassdesk-check-" + Guid.NewGuid().ToString("N"));
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            var roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var planned = new List<(NearbyFile File, string Path)>();
            foreach (var file in files)
            {
                var parts = file.Name.Replace('\\', '/').Split('/');
                if (parts.Length > 1)
                {
                    if (!roots.TryGetValue(parts[0], out var root))
                    {
                        root = UniquePath(Path.Combine(DirectoryPath, parts[0]), reserved, true);
                        roots.Add(parts[0], root);
                        EnsurePinnedDirectory(root);
                    }
                    var target = Path.Combine([root, .. parts.Skip(1)]);
                    var parent = Path.GetDirectoryName(target)!;
                    EnsurePinnedDirectory(parent);
                    planned.Add((file, target));
                }
                else planned.Add((file, Path.Combine(DirectoryPath, parts[0])));
            }
            // All directories now exist and are pinned, so numbering a duplicate leaf
            // cannot accidentally reserve a later directory's name.
            foreach (var item in planned) _files.Add(item.File.Id, (item.File, UniquePath(item.Path, reserved, false)));
        }
        catch { Dispose(); throw; }
    }

    public static void ValidateName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 240 || name.StartsWith('/') || name.StartsWith('\\'))
            throw new ArgumentException("文件路径不安全。");
        foreach (var part in name.Replace('\\', '/').Split('/'))
        {
            var stem = part.Split('.')[0];
            if (part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ')
                || part.Any(c => c < 32 || "<>:\"|?*".Contains(c))
                || stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
                || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                    && "123456789¹²³".Contains(stem[3]))) throw new ArgumentException("文件路径不安全。");
        }
    }

    private static string UniquePath(string path, HashSet<string> reserved, bool folder)
    {
        var original = path;
        for (var number = 2; File.Exists(path) || Directory.Exists(path) || !reserved.Add(path); number++)
            path = Path.Combine(Path.GetDirectoryName(original)!,
                (folder ? Path.GetFileName(original) : Path.GetFileNameWithoutExtension(original)) + $" ({number})" + (folder ? "" : Path.GetExtension(original)));
        return path;
    }

    public async Task WriteAsync(string id, Stream input, Action<long>? progress, CancellationToken cancellationToken)
    {
        if (!await _writer.WaitAsync(0, cancellationToken).ConfigureAwait(false)) throw new IOException("已有文件正在接收。");
        string? temporary = null;
        byte[]? buffer = null;
        try
        {
            if (!_files.TryGetValue(id, out var entry) || _completed.Contains(id)) throw new IOException("文件不存在或已接收。");
            PinAncestors(Path.GetDirectoryName(entry.Path)!);
            temporary = Path.Combine(Path.GetDirectoryName(entry.Path)!, ".glassdesk-" + Guid.NewGuid().ToString("N") + ".partial");
            buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                long length = 0;
                while (true)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    idle.CancelAfter(TimeSpan.FromSeconds(60));
                    int read;
                    try { read = await input.ReadAsync(buffer.AsMemory(0, 128 * 1024), idle.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new IOException("连续 60 秒未收到数据，请重试。"); }
                    if (read == 0) break;
                    length = checked(length + read);
                    if (length > entry.File.Size) throw new IOException("接收数据超过声明大小。");
                    await output.WriteAsync(buffer.AsMemory(0, read), idle.Token).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, read);
                    progress?.Invoke(length);
                }
                if (length != entry.File.Size) throw new IOException("接收数据不完整。");
                if (entry.File.Sha256 is { } expected && !Convert.ToHexString(hash.GetHashAndReset()).Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new ChecksumMismatchException();
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            // Directory handles deny rename/deletion, and Move never replaces existing bytes.
            File.Move(temporary, entry.Path, overwrite: false);
            temporary = null;
            _completed.Add(id);
        }
        finally
        {
            if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer);
            try { if (temporary is not null) File.Delete(temporary); }
            finally { _writer.Release(); }
        }
    }

    private void PinAncestors(string directory)
    {
        for (var current = directory; current is not null; current = Path.GetDirectoryName(current))
        {
            if (_pinned.Contains(current)) continue;
            if (!Directory.Exists(current)) continue;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("接收目录不能包含链接或联接点。");
            if (OperatingSystem.IsWindows())
            {
                var handle = CreateFileW(current, 0x80, 3, 0, 3, 0x02200000, 0);
                if (handle.IsInvalid) { handle.Dispose(); throw new IOException("无法锁定接收目录。", Marshal.GetLastWin32Error()); }
                _pins.Add(handle);
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("接收目录发生了变化。");
            }
            _pinned.Add(current);
        }
    }

    private void EnsurePinnedDirectory(string directory)
    {
        if (_pinned.Contains(directory)) return;
        if (Path.GetDirectoryName(directory) is { } parent) EnsurePinnedDirectory(parent);
        // Create one level only after its parent is pinned. A raced junction cannot
        // redirect recursive directory creation outside the approved destination.
        if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
        PinAncestors(directory);
    }

    public void Dispose()
    {
        foreach (var pin in _pins) pin.Dispose();
        _pins.Clear();
        _pinned.Clear();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
}
