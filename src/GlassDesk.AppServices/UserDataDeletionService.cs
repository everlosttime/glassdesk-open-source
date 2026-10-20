using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32.SafeHandles;
using GlassDesk.Core.Storage;
using GlassDesk.Platform.Storage;

namespace GlassDesk.AppServices;

public enum UserDataDeletionStatus
{
    Cancelled,
    NotFound,
    Busy,
    UnsafePath,
    Failed,
    Residual,
    Deleted,
}

public sealed record UserDataDeletionResult(
    UserDataDeletionStatus Status,
    int DeletedEntries = 0,
    string? ResidualDirectory = null,
    string? Error = null);

/// <summary>通过跨进程独占租约与同级隔离目录删除 GlassDesk 当前用户数据。</summary>
public sealed class UserDataDeletionService
{
    private readonly string _dataRoot;
    private readonly string _expectedDataRoot;
    private readonly IReadOnlyList<string> _protectedRoots;
    private readonly IUserDataDeletionFileSystem _fileSystem;
    private readonly string? _portableInstallationDirectory;

    internal UserDataDeletionService(
        string dataRoot,
        string expectedDataRoot,
        IReadOnlyList<string> protectedRoots,
        IUserDataDeletionFileSystem? fileSystem = null,
        string? portableInstallationDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedDataRoot);
        ArgumentNullException.ThrowIfNull(protectedRoots);

        _dataRoot = Path.GetFullPath(dataRoot);
        _expectedDataRoot = Path.GetFullPath(expectedDataRoot);
        _protectedRoots = protectedRoots.Select(Path.GetFullPath).ToArray();
        _fileSystem = fileSystem ?? new UserDataDeletionFileSystem();
        if (!string.IsNullOrWhiteSpace(portableInstallationDirectory)
            && PathsEqual(_dataRoot, Path.Combine(portableInstallationDirectory, "Data"))
            && PathsEqual(_dataRoot, _expectedDataRoot))
        {
            _portableInstallationDirectory = Path.GetFullPath(portableInstallationDirectory);
        }
    }

    /// <summary>创建固定绑定到当前运行版本数据目录的生产删除服务。</summary>
    public static UserDataDeletionService ForCurrentUser(string? installationDirectory = null)
    {
        var paths = AppPaths.ForCurrentUser();
        var protectedRoots = new List<string>();
        if (!string.IsNullOrWhiteSpace(installationDirectory))
        {
            protectedRoots.Add(installationDirectory);
        }

        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            var processDirectory = Path.GetDirectoryName(processPath);
            if (!string.IsNullOrWhiteSpace(processDirectory))
            {
                protectedRoots.Add(processDirectory);
            }
        }

        return CreateForPaths(paths, AppContext.BaseDirectory, protectedRoots);
    }

    internal static UserDataDeletionService CreateForPaths(
        AppPaths paths, string applicationDirectory, IReadOnlyList<string> protectedRoots)
        => new(paths.RootDirectory, paths.RootDirectory, [.. protectedRoots, applicationDirectory],
            portableInstallationDirectory: applicationDirectory);

    /// <summary>确认前不读取或修改数据；确认后执行隔离改名与逐项删除。</summary>
    public UserDataDeletionResult DeleteAll(bool confirmed)
    {
        if (!confirmed)
        {
            return new UserDataDeletionResult(UserDataDeletionStatus.Cancelled);
        }

        if (!IsConfiguredPathSafe(out var pathError))
        {
            return new UserDataDeletionResult(UserDataDeletionStatus.UnsafePath, Error: pathError);
        }

        if (!UserDataLease.TryAcquireExclusive(_dataRoot, out var lease) || lease is null)
        {
            return new UserDataDeletionResult(
                UserDataDeletionStatus.Busy,
                Error: "GlassDesk 数据仍被运行中的实例使用，或当前文件夹无法取得独占租约。请先退出其它 GlassDesk 实例后重试。");
        }

        using (lease)
        {
            var moved = false;
            string? quarantine = null;
            var deletedEntries = 0;
            try
            {
                if (!_fileSystem.Exists(_dataRoot))
                {
                    return new UserDataDeletionResult(UserDataDeletionStatus.NotFound);
                }

                ValidateDirectoryTree(_dataRoot);
                ValidatePhysicalIdentity(_dataRoot, _expectedDataRoot);
                ValidateProtectedRoots(_dataRoot);

                // 保持源目录句柄跨过改名；FILE_SHARE_DELETE 使本操作可改名，身份复核可发现路径被替换。
                using var sourceIdentity = OpenDirectoryIdentity(_dataRoot);
                var sourceId = ReadDirectoryIdentity(sourceIdentity);
                var sourceFinalPath = GetFinalPath(sourceIdentity);
                var expectedFinalPath = ResolveFinalPath(_expectedDataRoot);
                if (!PathsEqual(sourceFinalPath, expectedFinalPath))
                {
                    throw new UnsafeDataPathException("数据目录的物理路径与当前用户数据目录不一致。");
                }

                quarantine = CreateQuarantinePath();
                _fileSystem.MoveDirectory(_dataRoot, quarantine);
                moved = true;

                using var quarantineIdentity = OpenDirectoryIdentity(quarantine);
                if (ReadDirectoryIdentity(quarantineIdentity) != sourceId
                    || !PathsEqual(GetFinalPath(quarantineIdentity), quarantine))
                {
                    throw new UnsafeDataPathException("改名后的目录身份与已核验的数据目录不一致。");
                }

                ValidateDirectoryTree(quarantine);
                ValidateProtectedRoots(quarantine, quarantine);
                DeleteDirectoryContents(quarantine, sourceId, ref deletedEntries);
                return new UserDataDeletionResult(UserDataDeletionStatus.Deleted, deletedEntries);
            }
            catch (UnsafeDataPathException ex)
            {
                return moved
                    ? new UserDataDeletionResult(UserDataDeletionStatus.Residual, deletedEntries, quarantine, ex.Message)
                    : new UserDataDeletionResult(UserDataDeletionStatus.UnsafePath, Error: ex.Message);
            }
            catch (Exception ex) when (IsFileSystemFailure(ex))
            {
                return moved
                    ? new UserDataDeletionResult(UserDataDeletionStatus.Residual, deletedEntries, quarantine, ex.Message)
                    : new UserDataDeletionResult(UserDataDeletionStatus.Failed, Error: ex.Message);
            }
        }
    }

    private bool IsConfiguredPathSafe(out string? error)
    {
        error = null;
        if (!PathsEqual(_dataRoot, _expectedDataRoot))
        {
            error = "拒绝删除：目标目录不是当前用户配置的数据目录。";
            return false;
        }

        var volumeRoot = Path.GetPathRoot(_dataRoot);
        if (string.IsNullOrWhiteSpace(volumeRoot)
            || PathsEqual(_dataRoot, volumeRoot)
            || IsSameOrAncestor(_dataRoot, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)))
        {
            error = "拒绝删除：目标目录不能是磁盘根目录或用户配置文件根目录。";
            return false;
        }

        foreach (var protectedRoot in _protectedRoots)
        {
            if (OverlapsProtectedRoot(_dataRoot, protectedRoot))
            {
                error = "拒绝删除：目标目录与程序安装目录或受保护路径重叠。";
                return false;
            }
        }

        return true;
    }

    private void ValidateProtectedRoots(string targetRoot, string? quarantinePath = null)
    {
        foreach (var protectedRoot in _protectedRoots)
        {
            if (OverlapsProtectedRoot(targetRoot, protectedRoot, quarantinePath))
            {
                throw new UnsafeDataPathException("数据目录与程序安装目录或受保护路径重叠。");
            }

            if (!_fileSystem.Exists(protectedRoot))
            {
                continue;
            }

            var physical = ResolveFinalPath(protectedRoot);
            if (OverlapsProtectedRoot(ResolveFinalPath(targetRoot), physical, quarantinePath))
            {
                throw new UnsafeDataPathException("数据目录与受保护路径的物理位置重叠。");
            }
        }
    }

    private bool OverlapsProtectedRoot(string targetRoot, string protectedRoot, string? quarantinePath = null)
    {
        if (!PathsOverlap(targetRoot, protectedRoot)) return false;

        // The capture edition owns precisely <application>/Data. Its parent stays protected;
        // only that child and this operation's allocated sibling quarantine may be removed.
        // Physical identity, reparse-point and hard-link checks still apply before deletion.
        return !(_portableInstallationDirectory is not null
            && PathsEqual(protectedRoot, _portableInstallationDirectory)
            && PathsEqual(Path.GetDirectoryName(targetRoot) ?? targetRoot, _portableInstallationDirectory)
            && (PathsEqual(targetRoot, _dataRoot)
                || (quarantinePath is not null && PathsEqual(targetRoot, quarantinePath))));
    }

    private static void ValidatePhysicalIdentity(string actualPath, string expectedPath)
    {
        RejectReparseAncestors(actualPath);
        RejectReparseAncestors(expectedPath);
        if (!PathsEqual(ResolveFinalPath(actualPath), ResolveFinalPath(expectedPath)))
        {
            throw new UnsafeDataPathException("数据目录的最终物理路径与配置路径不一致。");
        }
    }

    private void ValidateDirectoryTree(string root)
    {
        var pending = new Stack<(string Path, bool IsRoot)>();
        pending.Push((root, true));
        while (pending.Count > 0)
        {
            var (path, isRoot) = pending.Pop();
            var attributes = _fileSystem.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnsafeDataPathException($"检测到目录联接或符号链接，已拒绝删除：{path}");
            }

            if (isRoot && (attributes & FileAttributes.Directory) == 0)
            {
                throw new IOException("数据路径不是目录。");
            }

            if (isRoot) RejectReparseAncestors(path);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                using var directory = OpenDirectoryForDeletion(path);
                foreach (var entry in directory.Entries)
                {
                    pending.Push((entry, false));
                }
            }
        }
    }

    private void DeleteDirectoryContents(string directory, DirectoryIdentity expectedRootIdentity, ref int deletedEntries)
    {
        var pending = new Stack<DirectoryTraversal>();
        pending.Push(OpenDirectoryForDeletion(directory, expectedRootIdentity));
        try
        {
            while (pending.Count > 0)
            {
                var current = pending.Peek();
                if (current.NextEntry == current.Entries.Length)
                {
                    _fileSystem.DeleteDirectoryByHandle(current.Handle);
                    pending.Pop().Dispose();
                    deletedEntries++;
                    continue;
                }

                var path = current.Entries[current.NextEntry++];
                var attributes = _fileSystem.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new UnsafeDataPathException($"隔离目录中新增了联接或符号链接，已停止删除：{path}");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(OpenDirectoryForDeletion(path));
                    continue;
                }

                using var file = OpenFileForDeletion(path);
                _fileSystem.DeleteFileByHandle(file, path);
                deletedEntries++;
            }
        }
        finally
        {
            while (pending.Count > 0)
            {
                pending.Pop().Dispose();
            }
        }
    }

    private DirectoryTraversal OpenDirectoryForDeletion(string path, DirectoryIdentity? expectedIdentity = null)
    {
        const uint Delete = 0x00010000;
        const uint FileReadAttributes = 0x80;
        const uint ShareReadWrite = 0x1 | 0x2;
        const uint OpenExisting = 3;
        const uint BackupSemantics = 0x02000000;
        const uint OpenReparsePoint = 0x00200000;

        var handle = CreateFileW(
            path,
            Delete | FileReadAttributes,
            ShareReadWrite,
            IntPtr.Zero,
            OpenExisting,
            BackupSemantics | OpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, $"无法锁定并核验待删除目录：{path}");
        }

        try
        {
            var info = ReadFileInformation(handle);
            var attributes = (FileAttributes)info.FileAttributes;
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnsafeDataPathException($"检测到待删除目录被替换为联接或符号链接：{path}");
            }

            if ((attributes & FileAttributes.Directory) == 0)
            {
                throw new IOException($"待删除路径不再是目录：{path}");
            }

            if (!PathsEqual(GetFinalPath(handle), path))
            {
                throw new UnsafeDataPathException($"待删除目录的物理路径与隔离路径不一致：{path}");
            }

            var identity = ToDirectoryIdentity(info);
            if (expectedIdentity is DirectoryIdentity expected && identity != expected)
            {
                throw new UnsafeDataPathException($"待删除目录已被替换，身份与改名时核验的目录不一致：{path}");
            }

            // 不共享 FILE_SHARE_DELETE：目录句柄持有期间，路径不能被改名或替换。
            // 同一句柄随后用于标记删除，避免释放句柄与按路径删除之间出现竞态。
            var entries = _fileSystem.EnumerateFileSystemEntries(path).ToArray();
            return new DirectoryTraversal(handle, entries, identity);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static SafeFileHandle OpenFileForDeletion(string path)
    {
        const uint Delete = 0x00010000;
        const uint FileReadAttributes = 0x80;
        const uint FileWriteAttributes = 0x100;
        const uint ShareNone = 0;
        const uint OpenExisting = 3;
        const uint OpenReparsePoint = 0x00200000;

        var handle = CreateFileW(
            path,
            Delete | FileReadAttributes | FileWriteAttributes,
            ShareNone,
            IntPtr.Zero,
            OpenExisting,
            OpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, $"无法锁定并核验待删除文件：{path}");
        }

        try
        {
            var info = ReadFileInformation(handle);
            var attributes = (FileAttributes)info.FileAttributes;
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnsafeDataPathException($"隔离目录中新增了联接或符号链接，已停止删除：{path}");
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                throw new UnsafeDataPathException($"待删除文件路径已被替换为目录：{path}");
            }

            if (info.NumberOfLinks != 1)
            {
                throw new UnsafeDataPathException($"检测到硬链接文件，为避免影响数据目录外的同一文件，已停止删除：{path}");
            }

            if (!PathsEqual(GetFinalPath(handle), path))
            {
                throw new UnsafeDataPathException($"待删除文件的物理路径与隔离路径不一致：{path}");
            }

            // 独占句柄：持有期间无法重命名、替换、打开或新增指向该文件的链接。
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private string CreateQuarantinePath()
    {
        var parent = Path.GetDirectoryName(_dataRoot)
            ?? throw new UnsafeDataPathException("无法确定数据目录的同级隔离位置。");
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var candidate = Path.Combine(parent, "GlassDesk.deleted-" + Guid.NewGuid().ToString("N"));
            if (!_fileSystem.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException("无法为本次数据删除分配唯一隔离目录。");
    }

    private static string ResolveFinalPath(string path)
    {
        using var handle = OpenDirectoryIdentity(path);
        return GetFinalPath(handle);
    }

    private static void RejectReparseAncestors(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrWhiteSpace(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new UnsafeDataPathException($"路径链中包含联接或符号链接：{current}");
                }
            }
            catch (FileNotFoundException) when (!PathsEqual(current, path))
            {
                // Path chains are checked up to their existing root; a missing expected path is handled by Exists.
            }

            var parent = Directory.GetParent(current)?.FullName;
            if (parent is null || PathsEqual(parent, current))
            {
                break;
            }

            current = parent;
        }
    }

    private static SafeFileHandle OpenDirectoryIdentity(string path)
    {
        const uint FileReadAttributes = 0x80;
        const uint ShareAll = 0x1 | 0x2 | 0x4;
        const uint OpenExisting = 3;
        const uint BackupSemantics = 0x02000000;
        var handle = CreateFileW(path, FileReadAttributes, ShareAll, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, $"无法打开并核验目录：{path}");
        }

        return handle;
    }

    private static DirectoryIdentity ReadDirectoryIdentity(SafeFileHandle handle)
        => ToDirectoryIdentity(ReadFileInformation(handle));

    private static DirectoryIdentity ToDirectoryIdentity(ByHandleFileInformation info)
        => new(info.VolumeSerialNumber, info.FileIndexHigh, info.FileIndexLow);

    private static ByHandleFileInformation ReadFileInformation(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取文件或目录身份。");
        }

        return info;
    }

    internal static void MarkDirectoryForDeletion(SafeFileHandle handle)
    {
        var information = new FileDispositionInformation { DeleteFile = 1 };
        if (!SetFileInformationByHandle(handle, 4, ref information, (uint)Marshal.SizeOf<FileDispositionInformation>()))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法通过已核验的目录句柄删除目录。");
        }
    }

    internal static void DeleteFileByHandle(SafeFileHandle handle, string path)
    {
        var info = ReadFileInformation(handle);
        if (info.NumberOfLinks != 1)
        {
            throw new UnsafeDataPathException($"待删除文件在锁定后出现了硬链接，已停止删除：{path}");
        }

        if (((FileAttributes)info.FileAttributes & FileAttributes.ReadOnly) != 0)
        {
            var attributes = info.FileAttributes & ~(uint)FileAttributes.ReadOnly;
            if (attributes == 0) attributes = (uint)FileAttributes.Normal;
            var basicInformation = new FileBasicInformation
            {
                CreationTime = -1,
                LastAccessTime = -1,
                LastWriteTime = -1,
                ChangeTime = -1,
                FileAttributes = attributes,
            };
            if (!SetFileInformationByHandle(
                handle,
                0,
                ref basicInformation,
                (uint)Marshal.SizeOf<FileBasicInformation>()))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"无法清除待删除文件的只读属性：{path}");
            }
        }

        var disposition = new FileDispositionInformation { DeleteFile = 1 };
        if (!SetFileInformationByHandle(
            handle,
            4,
            ref disposition,
            (uint)Marshal.SizeOf<FileDispositionInformation>()))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"无法通过已核验的文件句柄删除文件：{path}");
        }
    }

    private static string GetFinalPath(SafeFileHandle handle)
    {
        var buffer = new char[32768];
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
        if (length == 0 || length >= buffer.Length)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法核验目录的最终物理路径。");
        }

        var path = new string(buffer, 0, checked((int)length));
        if (path.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase))
        {
            path = "\\\\" + path[8..];
        }
        else if (path.StartsWith("\\\\?\\", StringComparison.OrdinalIgnoreCase))
        {
            path = path[4..];
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static bool IsFileSystemFailure(Exception ex) => ex is IOException or UnauthorizedAccessException
        or SecurityException or ArgumentException or NotSupportedException or Win32Exception;

    private static bool PathsEqual(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool PathsOverlap(string left, string right) => IsSameOrAncestor(left, right) || IsSameOrAncestor(right, left);

    private static bool IsSameOrAncestor(string possibleAncestor, string child)
    {
        if (string.IsNullOrWhiteSpace(child)) return false;
        var ancestor = Path.TrimEndingDirectorySeparator(Path.GetFullPath(possibleAncestor));
        var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(child));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(ancestor, candidate, comparison)) return true;
        return candidate.StartsWith(ancestor + Path.DirectorySeparatorChar, comparison);
    }

    private readonly record struct DirectoryIdentity(uint VolumeSerial, uint FileIndexHigh, uint FileIndexLow);

    private sealed class DirectoryTraversal(SafeFileHandle handle, string[] entries, DirectoryIdentity identity) : IDisposable
    {
        public SafeFileHandle Handle { get; } = handle;
        public string[] Entries { get; } = entries;
        public DirectoryIdentity Identity { get; } = identity;
        public int NextEntry { get; set; }

        public void Dispose() => Handle.Dispose();
    }

    private sealed class UnsafeDataPathException(string message) : IOException(message);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInformation
    {
        public byte DeleteFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInformation
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
        public uint Reserved;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation fileInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        int fileInformationClass,
        ref FileDispositionInformation fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        int fileInformationClass,
        ref FileBasicInformation fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, [Out] char[] filePath, uint filePathSize, uint flags);
}

internal interface IUserDataDeletionFileSystem
{
    bool Exists(string path);
    FileAttributes GetAttributes(string path);
    IEnumerable<string> EnumerateFileSystemEntries(string path);
    void MoveDirectory(string source, string destination);
    void DeleteFileByHandle(SafeFileHandle handle, string path);
    void DeleteDirectoryByHandle(SafeFileHandle handle);
}

internal sealed class UserDataDeletionFileSystem : IUserDataDeletionFileSystem
{
    public bool Exists(string path) => Directory.Exists(path) || File.Exists(path);
    public FileAttributes GetAttributes(string path) => File.GetAttributes(path);
    public IEnumerable<string> EnumerateFileSystemEntries(string path) => Directory.EnumerateFileSystemEntries(path);
    public void MoveDirectory(string source, string destination) => Directory.Move(source, destination);
    public void DeleteFileByHandle(SafeFileHandle handle, string path)
        => UserDataDeletionService.DeleteFileByHandle(handle, path);

    public void DeleteDirectoryByHandle(SafeFileHandle handle)
        => UserDataDeletionService.MarkDirectoryForDeletion(handle);
}
