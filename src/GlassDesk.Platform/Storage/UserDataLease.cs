using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace GlassDesk.Platform.Storage;

/// <summary>以 Windows 文件共享语义协调同一数据目录的运行访问与排他维护。</summary>
/// <remarks>
/// 租约文件位于数据根的同级目录，因此排他租约可以在改名和删除数据根期间继续持有。
/// 共享租约不共享删除权限；多个运行实例可同时打开，维护操作则必须独占打开。
/// </remarks>
public sealed class UserDataLease : IDisposable
{
    private readonly FileStream _stream;
    private int _disposed;

    private UserDataLease(FileStream stream) => _stream = stream;

    /// <summary>尝试取得运行期共享租约；竞争或文件系统拒绝时返回 false。</summary>
    public static bool TryAcquireShared(string dataRoot, out UserDataLease? lease) =>
        TryAcquire(dataRoot, FileShare.ReadWrite, out lease);

    /// <summary>尝试取得数据删除/维护所需的排他租约。</summary>
    public static bool TryAcquireExclusive(string dataRoot, out UserDataLease? lease) =>
        TryAcquire(dataRoot, FileShare.None, out lease);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stream.Dispose();
    }

    private static bool TryAcquire(string dataRoot, FileShare sharing, out UserDataLease? lease)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        lease = null;

        try
        {
            var leasePath = GetLeasePath(dataRoot);
            var parentDirectory = Path.GetDirectoryName(leasePath);
            if (string.IsNullOrWhiteSpace(parentDirectory))
            {
                return false;
            }

            Directory.CreateDirectory(parentDirectory);
            var stream = new FileStream(
                leasePath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                sharing);
            lease = new UserDataLease(stream);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException
            or ArgumentException or NotSupportedException)
        {
            // 维护与运行权限不明时一律不放行；宿主会在加载或删除数据前中止。
            return false;
        }
    }

    private static string GetLeasePath(string dataRoot)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
        var volumeRoot = Path.TrimEndingDirectorySeparator(Path.GetPathRoot(fullRoot) ?? string.Empty);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(fullRoot, volumeRoot, comparison))
        {
            throw new ArgumentException("数据目录不能是卷根。", nameof(dataRoot));
        }

        // Windows 路径不区分大小写；大小写不同的同一路径必须落到相同门禁文件。
        var leaseIdentity = OperatingSystem.IsWindows() ? fullRoot.ToUpperInvariant() : fullRoot;
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(leaseIdentity)));
        var parentDirectory = Directory.GetParent(fullRoot)?.FullName;
        if (parentDirectory is null)
        {
            throw new ArgumentException("无法为数据目录确定同级租约位置。", nameof(dataRoot));
        }

        return Path.Combine(parentDirectory, $".glassdesk-data-{digest}.lease");
    }
}
