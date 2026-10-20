using GlassDesk.AppServices;
using GlassDesk.Core.Storage;

namespace GlassDesk.AppServices.Tests;

public sealed class PortableUserDataDeletionTests : IDisposable
{
    private readonly string _installation = Path.Combine(Path.GetTempPath(), "GlassDesk-portable-delete-" + Guid.NewGuid().ToString("N"));

    public PortableUserDataDeletionTests() => Directory.CreateDirectory(_installation);

    [Fact]
    public void Portable_Data_is_deleted_while_program_and_neighbor_bytes_are_preserved()
    {
        var paths = new AppPaths(Path.Combine(_installation, "Data"));
        var store = new GlassDeskDataStore(paths);
        store.LoadAll();
        File.WriteAllText(Path.Combine(paths.RootDirectory, "private.txt"), "old profile");
        var program = Path.Combine(_installation, "GlassDesk.exe");
        var neighbor = Path.Combine(_installation, "Data-other", "keep.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(neighbor)!);
        byte[] bytes = [0, 255, 19, 37];
        File.WriteAllBytes(program, bytes);
        File.WriteAllBytes(neighbor, bytes);

        var result = Service(paths).DeleteAll(confirmed: true);

        Assert.True(result.Status == UserDataDeletionStatus.Deleted, result.Error ?? result.ResidualDirectory);
        Assert.False(Directory.Exists(paths.RootDirectory));
        Assert.Equal(bytes, File.ReadAllBytes(program));
        Assert.Equal(bytes, File.ReadAllBytes(neighbor));
        Assert.Empty(Directory.GetDirectories(_installation, "GlassDesk.deleted-*"));
        new GlassDeskDataStore(paths).LoadAll();
        Assert.True(Directory.Exists(paths.RootDirectory));
        Assert.False(File.Exists(Path.Combine(paths.RootDirectory, "private.txt")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Data-other")]
    [InlineData("Data/nested")]
    public void Portable_exception_never_allows_installation_or_other_children(string relativePath)
    {
        var paths = new AppPaths(Path.Combine(_installation, relativePath));
        Directory.CreateDirectory(paths.RootDirectory);
        var sentinel = Path.Combine(paths.RootDirectory, "keep.bin");
        File.WriteAllBytes(sentinel, [0, 255, 42]);

        var result = Service(paths).DeleteAll(confirmed: true);

        Assert.Equal(UserDataDeletionStatus.UnsafePath, result.Status);
        Assert.Equal(new byte[] { 0, 255, 42 }, File.ReadAllBytes(sentinel));
    }

    [Fact]
    public void Additional_protected_root_inside_Data_is_still_rejected()
    {
        var paths = new AppPaths(Path.Combine(_installation, "Data"));
        var protectedChild = Path.Combine(paths.RootDirectory, "protected");
        Directory.CreateDirectory(protectedChild);
        var sentinel = Path.Combine(protectedChild, "keep.bin");
        File.WriteAllBytes(sentinel, [0, 255, 42]);

        var result = Service(paths, protectedChild).DeleteAll(confirmed: true);

        Assert.Equal(UserDataDeletionStatus.UnsafePath, result.Status);
        Assert.Equal(new byte[] { 0, 255, 42 }, File.ReadAllBytes(sentinel));
    }

    [Fact]
    public void Generic_service_keeps_installation_descendants_protected_without_portable_opt_in()
    {
        var root = Path.Combine(_installation, "Data");
        Directory.CreateDirectory(root);
        var result = new UserDataDeletionService(root, root, [_installation]).DeleteAll(confirmed: true);
        Assert.Equal(UserDataDeletionStatus.UnsafePath, result.Status);
        Assert.True(Directory.Exists(root));
    }

    [Fact]
    public void Portable_Data_link_cannot_redirect_deletion_to_another_directory()
    {
        var outside = Path.Combine(_installation, "outside");
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "keep.bin");
        File.WriteAllBytes(sentinel, [0, 255, 42]);
        var paths = new AppPaths(Path.Combine(_installation, "Data"));
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var argument in new[] { "/d", "/c", "mklink", "/J", paths.RootDirectory, outside })
            start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());

        var result = Service(paths).DeleteAll(confirmed: true);

        Assert.Equal(UserDataDeletionStatus.UnsafePath, result.Status);
        Assert.Equal(new byte[] { 0, 255, 42 }, File.ReadAllBytes(sentinel));
    }

    private UserDataDeletionService Service(AppPaths paths, params string[] additionalProtectedRoots)
        => UserDataDeletionService.CreateForPaths(paths, _installation, [_installation, .. additionalProtectedRoots]);

    public void Dispose()
    {
        if (!Directory.Exists(_installation)) return;
        foreach (var child in Directory.GetDirectories(_installation))
        {
            if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(child, recursive: false);
        }
        Directory.Delete(_installation, recursive: true);
    }
}
