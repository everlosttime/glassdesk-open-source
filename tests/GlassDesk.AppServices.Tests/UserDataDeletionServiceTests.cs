using System.Reflection;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using GlassDesk.AppServices;
using GlassDesk.Core.Storage;
using GlassDesk.Platform.Storage;

namespace GlassDesk.AppServices.Tests;

public sealed class UserDataDeletionServiceTests : IDisposable
{
    private readonly string _parent = Path.Combine(Path.GetTempPath(), "GlassDesk-delete-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _dataRoot;

    public UserDataDeletionServiceTests()
    {
        Directory.CreateDirectory(_parent);
        _dataRoot = Path.Combine(_parent, "GlassDeskData");
    }

    [Fact]
    public void Unconfirmed_delete_leaves_data_bytes_unchanged()
    {
        Directory.CreateDirectory(_dataRoot);
        var bytes = new byte[] { 0, 1, 2, 255, 42 };
        var file = Path.Combine(_dataRoot, "apps.json");
        File.WriteAllBytes(file, bytes);

        var result = CreateService().DeleteAll(confirmed: false);

        Assert.Equal(UserDataDeletionStatus.Cancelled, result.Status);
        Assert.Equal(bytes, File.ReadAllBytes(file));
    }

    [Fact]
    public void Active_shared_runtime_lease_returns_busy_and_preserves_data()
    {
        Directory.CreateDirectory(_dataRoot);
        var file = Path.Combine(_dataRoot, "settings.json");
        var bytes = new byte[] { 1, 3, 3, 7, 0, 255 };
        File.WriteAllBytes(file, bytes);
        Assert.True(UserDataLease.TryAcquireShared(_dataRoot, out var runtimeLease));

        using (runtimeLease)
        {
            var result = CreateService().DeleteAll(confirmed: true);

            Assert.Equal(UserDataDeletionStatus.Busy, result.Status);
            Assert.Equal(bytes, File.ReadAllBytes(file));
            Assert.True(Directory.Exists(_dataRoot));
        }
    }

    [Fact]
    public void Open_directory_handle_blocking_sibling_rename_returns_failed_and_preserves_root()
    {
        Directory.CreateDirectory(_dataRoot);
        var file = Path.Combine(_dataRoot, "settings.json");
        var bytes = new byte[] { 9, 8, 7, 0, 255 };
        File.WriteAllBytes(file, bytes);
        using var handle = OpenDirectoryWithoutDeleteSharing(_dataRoot);

        var result = CreateService().DeleteAll(confirmed: true);

        Assert.Equal(UserDataDeletionStatus.Failed, result.Status);
        Assert.True(File.Exists(file));
        Assert.Equal(bytes, File.ReadAllBytes(file));
    }

    [Fact]
    public void Successful_delete_removes_only_data_root_and_preserves_sibling_user_files()
    {
        Directory.CreateDirectory(_dataRoot);
        File.WriteAllText(Path.Combine(_dataRoot, "apps.json"), "private data");
        var nestedDirectory = Path.Combine(_dataRoot, "icon-cache", "nested");
        Directory.CreateDirectory(nestedDirectory);
        File.WriteAllBytes(Path.Combine(nestedDirectory, "icon.bin"), [5, 0, 6, 255]);
        var externalBackup = Path.Combine(_parent, "GlassDesk-backup.zip");
        var import = Path.Combine(_parent, "import.json");
        var neighbor = Path.Combine(_parent, "neighbor-sentinel.txt");
        var backupBytes = new byte[] { 0, 1, 2, 3, 0xFE, 0xFF };
        var importBytes = new byte[] { 10, 0, 13, 255, 42 };
        var neighborBytes = new byte[] { 99, 0, 88, 77 };
        File.WriteAllBytes(externalBackup, backupBytes);
        File.WriteAllBytes(import, importBytes);
        File.WriteAllBytes(neighbor, neighborBytes);
        var protectedRoots = new[] { externalBackup, import };

        var result = CreateService(protectedRoots: protectedRoots).DeleteAll(confirmed: true);

        Assert.True(result.Status == UserDataDeletionStatus.Deleted, result.Error ?? result.ResidualDirectory);
        Assert.False(Directory.Exists(_dataRoot));
        Assert.Equal(backupBytes, File.ReadAllBytes(externalBackup));
        Assert.Equal(importBytes, File.ReadAllBytes(import));
        Assert.Equal(neighborBytes, File.ReadAllBytes(neighbor));
    }

    [Fact]
    public void Successful_delete_removes_read_only_files_inside_the_data_root()
    {
        Directory.CreateDirectory(_dataRoot);
        var file = Path.Combine(_dataRoot, "read-only.bin");
        File.WriteAllBytes(file, [4, 2, 0, 255]);
        File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);

        var result = CreateService().DeleteAll(confirmed: true);

        Assert.Equal(UserDataDeletionStatus.Deleted, result.Status);
        Assert.False(Directory.Exists(_dataRoot));
    }

    [Fact]
    public void Read_only_hard_link_to_external_file_is_preserved_without_changing_external_attributes()
    {
        Directory.CreateDirectory(_dataRoot);
        var external = Path.Combine(_parent, "external-read-only-sentinel.bin");
        var externalBytes = new byte[] { 0x45, 0, 0x58, 0x54, 0xFF };
        File.WriteAllBytes(external, externalBytes);
        File.SetAttributes(external, File.GetAttributes(external) | FileAttributes.ReadOnly);
        var originalAttributes = File.GetAttributes(external);
        var internalLink = Path.Combine(_dataRoot, "linked-sentinel.bin");
        Assert.True(CreateHardLinkW(internalLink, external, IntPtr.Zero), new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message);

        try
        {
            var result = CreateService().DeleteAll(confirmed: true);

            Assert.Equal(UserDataDeletionStatus.Residual, result.Status);
            Assert.Equal(originalAttributes, File.GetAttributes(external));
            Assert.Equal(externalBytes, File.ReadAllBytes(external));
        }
        finally
        {
            File.SetAttributes(external, File.GetAttributes(external) & ~FileAttributes.ReadOnly);
        }
    }

    [Fact]
    public void Concurrent_hard_link_creation_cannot_mutate_external_read_only_file()
    {
        Directory.CreateDirectory(_dataRoot);
        var dataFile = Path.Combine(_dataRoot, "read-only.bin");
        var bytes = new byte[] { 0x47, 0, 0x44, 0xFF };
        File.WriteAllBytes(dataFile, bytes);
        File.SetAttributes(dataFile, File.GetAttributes(dataFile) | FileAttributes.ReadOnly);
        var originalAttributes = File.GetAttributes(dataFile);
        var externalLink = Path.Combine(_parent, "concurrent-hard-link.bin");
        var fileSystem = DispatchProxy.Create<IUserDataDeletionFileSystem, HardLinkDuringDeleteProxy>();
        var proxy = (HardLinkDuringDeleteProxy)(object)fileSystem;
        proxy.NewLinkPath = externalLink;

        try
        {
            var result = CreateService(fileSystem: fileSystem).DeleteAll(confirmed: true);

            if (proxy.HardLinkCreated)
            {
                Assert.Equal(UserDataDeletionStatus.Residual, result.Status);
                Assert.True(Directory.Exists(result.ResidualDirectory));
                Assert.Equal(originalAttributes, File.GetAttributes(externalLink));
                Assert.Equal(bytes, File.ReadAllBytes(externalLink));
            }
            else
            {
                Assert.Equal(UserDataDeletionStatus.Deleted, result.Status);
                Assert.Equal(32, proxy.HardLinkErrorCode);
                Assert.False(File.Exists(externalLink));
                Assert.False(Directory.Exists(_dataRoot));
            }
        }
        finally
        {
            if (File.Exists(externalLink))
            {
                File.SetAttributes(externalLink, File.GetAttributes(externalLink) & ~FileAttributes.ReadOnly);
            }
            else if (File.Exists(dataFile))
            {
                File.SetAttributes(dataFile, File.GetAttributes(dataFile) & ~FileAttributes.ReadOnly);
            }
        }
    }

    [Fact]
    public void Delete_failure_after_rename_reports_residual_directory_and_preserves_its_contents()
    {
        Directory.CreateDirectory(_dataRoot);
        var marker = Path.Combine(_dataRoot, "marker.bin");
        var bytes = new byte[] { 0x13, 0x37, 0, 0xFF };
        File.WriteAllBytes(marker, bytes);
        var fileSystem = DispatchProxy.Create<IUserDataDeletionFileSystem, FailOnDeleteProxy>();
        ((FailOnDeleteProxy)(object)fileSystem).FailDelete = true;

        var result = CreateService(fileSystem: fileSystem).DeleteAll(confirmed: true);

        Assert.Equal(UserDataDeletionStatus.Residual, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.ResidualDirectory));
        Assert.True(Directory.Exists(result.ResidualDirectory));
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(result.ResidualDirectory!, "marker.bin")));
        Assert.True(UserDataLease.TryAcquireShared(_dataRoot, out var recoveredLease));
        using (recoveredLease) Assert.NotNull(recoveredLease);
    }

    [Fact]
    public void Data_root_containing_reparse_point_is_rejected_without_touching_target()
    {
        var outside = Path.Combine(_parent, "outside-target");
        Directory.CreateDirectory(_dataRoot);
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "sentinel.txt");
        File.WriteAllText(sentinel, "untouched");
        var link = Path.Combine(_dataRoot, "linked-directory");
        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return; // 本机未启用创建目录链接所需的系统能力。
        }

        var result = CreateService().DeleteAll(confirmed: true);

        Assert.Equal(UserDataDeletionStatus.UnsafePath, result.Status);
        Assert.Equal("untouched", File.ReadAllText(sentinel));
        Assert.True(Directory.Exists(outside));
    }

    [Fact]
    public void Concurrent_directory_replacement_cannot_redirect_deletion_outside_quarantine()
    {
        var nested = Path.Combine(_dataRoot, "nested");
        var outside = Path.Combine(_parent, "outside-race-target");
        Directory.CreateDirectory(nested);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(nested, "inside.txt"), "delete this");
        var sentinel = Path.Combine(outside, "sentinel.bin");
        var sentinelBytes = new byte[] { 0xAC, 0, 0xED, 0xFF, 7 };
        File.WriteAllBytes(sentinel, sentinelBytes);

        var fileSystem = DispatchProxy.Create<IUserDataDeletionFileSystem, ReplaceNestedDirectoryProxy>();
        var proxy = (ReplaceNestedDirectoryProxy)(object)fileSystem;
        proxy.OutsideDirectory = outside;

        var result = CreateService(fileSystem: fileSystem).DeleteAll(confirmed: true);

        Assert.Equal(sentinelBytes, File.ReadAllBytes(sentinel));
        Assert.Equal(UserDataDeletionStatus.Deleted, result.Status);
        Assert.True(proxy.ReplacementWasBlocked, "A held directory identity must prevent replacement between checking and enumeration.");
    }

    [Fact]
    public void Data_root_that_is_itself_a_directory_link_is_rejected_without_touching_target()
    {
        var outside = Path.Combine(_parent, "outside-root-target");
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "sentinel.txt");
        File.WriteAllText(sentinel, "untouched root target");
        try
        {
            Directory.CreateSymbolicLink(_dataRoot, outside);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return; // 本机未启用创建目录链接所需的系统能力。
        }

        var result = CreateService().DeleteAll(confirmed: true);

        Assert.Equal(UserDataDeletionStatus.UnsafePath, result.Status);
        Assert.Equal("untouched root target", File.ReadAllText(sentinel));
        Assert.True(Directory.Exists(outside));
    }

    [Fact]
    public void Mismatched_configured_root_is_rejected_without_mutating_data()
    {
        Directory.CreateDirectory(_dataRoot);
        var file = Path.Combine(_dataRoot, "settings.json");
        var bytes = new byte[] { 2, 7, 1, 8, 2, 8 };
        File.WriteAllBytes(file, bytes);

        var result = new UserDataDeletionService(_dataRoot, _parent, Array.Empty<string>()).DeleteAll(confirmed: true);

        Assert.Equal(UserDataDeletionStatus.UnsafePath, result.Status);
        Assert.Equal(bytes, File.ReadAllBytes(file));
    }

    [Fact]
    public void Fresh_data_store_load_after_delete_creates_default_data()
    {
        Directory.CreateDirectory(_dataRoot);
        File.WriteAllText(Path.Combine(_dataRoot, "unexpected.txt"), "remove me");

        var result = CreateService().DeleteAll(confirmed: true);
        Assert.True(result.Status == UserDataDeletionStatus.Deleted, result.Error ?? result.ResidualDirectory);

        var store = new GlassDeskDataStore(new AppPaths(_dataRoot));
        var report = store.LoadAll();

        Assert.True(Directory.Exists(_dataRoot));
        Assert.False(report.HasNotices);
        Assert.Equal(GlassDesk.Core.Models.ThemeMode.Dark, store.Settings.Current.Appearance.Theme);
        Assert.True(Directory.Exists(new AppPaths(_dataRoot).IconCacheDirectory));
    }

    private UserDataDeletionService CreateService(
        IReadOnlyList<string>? protectedRoots = null,
        IUserDataDeletionFileSystem? fileSystem = null,
        string? expectedDataRoot = null)
        => new(_dataRoot, expectedDataRoot ?? _dataRoot, protectedRoots ?? Array.Empty<string>(), fileSystem);

    private static FileStream OpenDirectoryWithoutDeleteSharing(string path)
    {
        // Windows directory handle: share read/write, deliberately omit FILE_SHARE_DELETE.
        const uint GenericRead = 0x80000000;
        const uint ShareRead = 0x1;
        const uint ShareWrite = 0x2;
        const uint OpenExisting = 3;
        const uint BackupSemantics = 0x02000000;
        var handle = CreateFile(path, GenericRead, ShareRead | ShareWrite, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException("Could not open the temporary directory without delete sharing.");
        return new FileStream(handle, FileAccess.Read);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string newFileName, string existingFileName, IntPtr securityAttributes);

    public void Dispose()
    {
        try { if (Directory.Exists(_parent)) Directory.Delete(_parent, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public class FailOnDeleteProxy : DispatchProxy
    {
        public bool FailDelete { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null || args is null) throw new InvalidOperationException("Missing filesystem invocation details.");
            var name = targetMethod.Name;
            if (name == "DeleteDirectoryByHandle")
            {
                if (FailDelete) throw new IOException("Injected delete failure after the root was renamed.");
                new UserDataDeletionFileSystem().DeleteDirectoryByHandle((SafeFileHandle)args[0]!);
                return null;
            }

            var path = args.OfType<string>().FirstOrDefault();
            if (path is null) throw new InvalidOperationException($"Unexpected filesystem operation: {name}");
            if (FailDelete && name.Contains("Delete", StringComparison.OrdinalIgnoreCase))
                throw new IOException("Injected delete failure after the root was renamed.");

            if (name == "DeleteFileByHandle")
            {
                new UserDataDeletionFileSystem().DeleteFileByHandle((SafeFileHandle)args[0]!, path);
                return null;
            }

            object? value = name switch
            {
                "Exists" => Directory.Exists(path) || File.Exists(path),
                "GetAttributes" => File.GetAttributes(path),
                "EnumerateFileSystemEntries" or "EnumerateEntries" => Directory.EnumerateFileSystemEntries(path).ToArray(),
                "MoveDirectory" or "Move" => Move(path, args),
                _ => throw new InvalidOperationException($"Unexpected filesystem operation: {name}"),
            };
            if (targetMethod.ReturnType == typeof(void)) return null;
            return value;
        }

        private static object? Move(string source, object?[] args)
        {
            var destination = args.OfType<string>().Skip(1).First();
            Directory.Move(source, destination);
            return null;
        }
    }

    public class ReplaceNestedDirectoryProxy : DispatchProxy
    {
        public string OutsideDirectory { get; set; } = string.Empty;
        public bool ReplacementWasBlocked { get; private set; }
        private int _quarantineNestedEnumerations;
        private readonly UserDataDeletionFileSystem _inner = new();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null || args is null) throw new InvalidOperationException("Missing filesystem invocation details.");
            var name = targetMethod.Name;
            if (name == "DeleteDirectoryByHandle")
            {
                _inner.DeleteDirectoryByHandle((SafeFileHandle)args[0]!);
                return null;
            }

            if (name == "DeleteFileByHandle")
            {
                _inner.DeleteFileByHandle((SafeFileHandle)args[0]!, (string)args[1]!);
                return null;
            }

            var path = args.OfType<string>().FirstOrDefault();
            if (path is null) throw new InvalidOperationException($"Unexpected filesystem operation: {name}");

            if (name is "EnumerateFileSystemEntries" or "EnumerateEntries"
                && Path.GetFileName(path) == "nested"
                && Path.GetFileName(Path.GetDirectoryName(path) ?? string.Empty).StartsWith("GlassDesk.deleted-", StringComparison.Ordinal)
                && Interlocked.Increment(ref _quarantineNestedEnumerations) == 2)
            {
                var displaced = path + ".original";
                try
                {
                    Directory.Move(path, displaced);
                }
                catch (IOException)
                {
                    ReplacementWasBlocked = true;
                    return Directory.EnumerateFileSystemEntries(path).ToArray();
                }

                CreateDirectoryJunction(path, OutsideDirectory);
            }

            object? value = name switch
            {
                "Exists" => Directory.Exists(path) || File.Exists(path),
                "GetAttributes" => File.GetAttributes(path),
                "EnumerateFileSystemEntries" or "EnumerateEntries" => Directory.EnumerateFileSystemEntries(path).ToArray(),
                "MoveDirectory" or "Move" => Move(path, args),
                _ => throw new InvalidOperationException($"Unexpected filesystem operation: {name}"),
            };
            if (targetMethod.ReturnType == typeof(void)) return null;
            return value;
        }

        private static object? Move(string source, object?[] args)
        {
            var destination = args.OfType<string>().Skip(1).First();
            Directory.Move(source, destination);
            return null;
        }

        private static void CreateDirectoryJunction(string linkPath, string targetPath)
        {
            var startInfo = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add("mklink");
            startInfo.ArgumentList.Add("/J");
            startInfo.ArgumentList.Add(linkPath);
            startInfo.ArgumentList.Add(targetPath);
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start the temporary junction test helper.");
            process.WaitForExit();
            if (process.ExitCode != 0) throw new IOException("Could not create the test junction.");
        }
    }

    public class HardLinkDuringDeleteProxy : DispatchProxy
    {
        public string NewLinkPath { get; set; } = string.Empty;
        public bool HardLinkCreated { get; private set; }
        public int HardLinkErrorCode { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null || args is null) throw new InvalidOperationException("Missing filesystem invocation details.");
            if (targetMethod.Name == "DeleteFileByHandle")
            {
                var handle = (SafeFileHandle)args[0]!;
                var filePath = (string)args[1]!;
                HardLinkCreated = CreateHardLinkW(NewLinkPath, filePath, IntPtr.Zero);
                HardLinkErrorCode = HardLinkCreated ? 0 : Marshal.GetLastWin32Error();
                new UserDataDeletionFileSystem().DeleteFileByHandle(handle, filePath);
                return null;
            }

            if (targetMethod.Name == "DeleteDirectoryByHandle")
            {
                new UserDataDeletionFileSystem().DeleteDirectoryByHandle((SafeFileHandle)args[0]!);
                return null;
            }

            var path = args.OfType<string>().FirstOrDefault();
            if (path is null) throw new InvalidOperationException($"Unexpected filesystem operation: {targetMethod.Name}");
            object? value = targetMethod.Name switch
            {
                "Exists" => Directory.Exists(path) || File.Exists(path),
                "GetAttributes" => File.GetAttributes(path),
                "EnumerateFileSystemEntries" or "EnumerateEntries" => Directory.EnumerateFileSystemEntries(path).ToArray(),
                "MoveDirectory" or "Move" => Move(path, args),
                _ => throw new InvalidOperationException($"Unexpected filesystem operation: {targetMethod.Name}"),
            };
            return targetMethod.ReturnType == typeof(void) ? null : value;
        }

        private static object? Move(string source, object?[] args)
        {
            var destination = args.OfType<string>().Skip(1).First();
            Directory.Move(source, destination);
            return null;
        }
    }
}

