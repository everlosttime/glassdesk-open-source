using System.Security.Cryptography;
using GlassDesk.AppServices.Updates;
using GlassDesk.Platform.Installation;

namespace GlassDesk.AppServices.Tests;

public sealed class UpdateInstallerTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "GlassDesk-install-update-" + Guid.NewGuid().ToString("N"))).FullName;
    private readonly byte[] _bytes = [1, 2, 3, 4];
    private UpdateRelease Release => new(new Version(0, 2, 10), "GlassDesk-0.2.10-win-x64-Setup.exe",
        new Uri("https://github.com/everlosttime/glassdesk-updates/releases/download/v0.2.10/GlassDesk-0.2.10-win-x64-Setup.exe"),
        Convert.ToHexString(SHA256.HashData(_bytes)), 4, "Notes", new Uri("https://github.com/everlosttime/glassdesk-updates/releases/tag/v0.2.10"));

    [Theory]
    [InlineData(InstallationState.Portable)]
    [InlineData(InstallationState.RegistrationMismatch)]
    public async Task Never_prepares_overwrite_without_verified_installation(InstallationState state)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => UpdateInstaller.PrepareAsync(Release, WriteInstaller(), new InstallationInfo(state), CancellationToken.None));
    }

    [Fact]
    public async Task Uses_exact_verified_directory_and_single_restart_update_mode()
    {
        var install = Path.Combine(_root, "原安装 目录 & special");
        Directory.CreateDirectory(install);
        var file = WriteInstaller();
        var start = await UpdateInstaller.PrepareAsync(Release, file, new InstallationInfo(InstallationState.VerifiedInstalled, install), CancellationToken.None);
        Assert.Equal(file, start.FileName);
        Assert.True(start.UseShellExecute);
        Assert.Contains("/DIR=" + install, start.ArgumentList);
        Assert.Contains("/SILENT", start.ArgumentList);
        Assert.Contains("/NORESTART", start.ArgumentList);
        Assert.Contains("/GLASSDESKUPDATE=1", start.ArgumentList);
        Assert.DoesNotContain(start.ArgumentList, arg => arg.StartsWith("/TASKS="));
    }

    [Fact]
    public async Task Rejects_install_package_changed_after_download()
    {
        var file = WriteInstaller();
        await File.WriteAllBytesAsync(file, [4, 3, 2, 1]);
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateInstaller.PrepareAsync(Release, file,
            new InstallationInfo(InstallationState.VerifiedInstalled, Directory.CreateDirectory(Path.Combine(_root, "install")).FullName), CancellationToken.None));
    }

    [Fact]
    public async Task Rejects_installers_inside_the_directory_being_overwritten()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => UpdateInstaller.PrepareAsync(Release, WriteInstaller(),
            new InstallationInfo(InstallationState.VerifiedInstalled, _root), CancellationToken.None));
    }

    private string WriteInstaller()
    {
        var file = Path.Combine(_root, Release.FileName);
        File.WriteAllBytes(file, _bytes);
        return file;
    }
    public void Dispose() => Directory.Delete(_root, true);
}
