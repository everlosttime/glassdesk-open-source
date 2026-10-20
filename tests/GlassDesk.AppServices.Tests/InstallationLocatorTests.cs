using Microsoft.Win32;
using GlassDesk.Platform.Installation;

namespace GlassDesk.AppServices.Tests;

public sealed class InstallationLocatorTests : IDisposable
{
    private const string DisplayName = "GlassDesk";
    private readonly string _registrySubkey = $"Software\\GlassDesk.Tests\\{Guid.NewGuid():N}";
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "GlassDesk-install-locator-" + Guid.NewGuid().ToString("N"));
    private readonly string _installDirectory;
    private readonly string _installedExe;
    private readonly string _uninstaller;
    private readonly string _runnerExe;

    public InstallationLocatorTests()
    {
        Directory.CreateDirectory(_tempRoot);
        _installDirectory = Path.Combine(_tempRoot, "install");
        Directory.CreateDirectory(_installDirectory);
        _installedExe = Path.Combine(_installDirectory, "GlassDesk.exe");
        _uninstaller = Path.Combine(_installDirectory, "unins000.exe");
        _runnerExe = Environment.ProcessPath ?? throw new InvalidOperationException("Test runner executable path is unavailable.");
        File.Copy(_runnerExe, _installedExe);
        File.Copy(_runnerExe, _uninstaller);
    }

    [Fact]
    public void Valid_registration_is_verified_and_returns_install_paths()
    {
        WriteRegistration(_installDirectory, $"\"{_uninstaller}\"");

        var result = Locator().Inspect(_installedExe);

        Assert.Equal(InstallationState.VerifiedInstalled, result.State);
        Assert.Equal(Path.GetFullPath(_installDirectory), Path.GetFullPath(result.InstallDirectory!));
        Assert.Equal(Path.GetFullPath(_uninstaller), Path.GetFullPath(result.UninstallerPath!));
        Assert.Null(result.Error);
    }

    [Fact]
    public void Missing_registration_is_portable()
    {
        var result = Locator().Inspect(_installedExe);

        Assert.Equal(InstallationState.Portable, result.State);
    }

    [Fact]
    public void Executable_outside_registered_install_directory_is_a_mismatch()
    {
        WriteRegistration(_installDirectory, $"\"{_uninstaller}\"");
        var otherExe = Path.Combine(_tempRoot, "GlassDesk.exe");
        File.Copy(_runnerExe, otherExe);

        var result = Locator().Inspect(otherExe);

        Assert.Equal(InstallationState.RegistrationMismatch, result.State);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public void Uninstaller_outside_registered_install_directory_is_rejected()
    {
        var outsideUninstaller = Path.Combine(_tempRoot, "unins001.exe");
        File.Copy(_runnerExe, outsideUninstaller);
        WriteRegistration(_installDirectory, $"\"{outsideUninstaller}\"");

        var result = Locator().Inspect(_installedExe);

        Assert.Equal(InstallationState.RegistrationMismatch, result.State);
    }

    [Theory]
    [InlineData("not-a-valid-executable")]
    [InlineData("\"{0}\" & calc.exe")]
    public void Malformed_or_command_injection_uninstall_path_is_rejected(string uninstallTemplate)
    {
        var uninstallString = uninstallTemplate == "not-a-valid-executable"
            ? uninstallTemplate
            : string.Format(uninstallTemplate, _uninstaller);
        WriteRegistration(_installDirectory, uninstallString);

        var result = Locator().Inspect(_installedExe);

        Assert.Equal(InstallationState.RegistrationMismatch, result.State);
    }

    private InstallationLocator Locator() => new(_registrySubkey);

    private void WriteRegistration(string installLocation, string uninstallString)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_registrySubkey, writable: true)
            ?? throw new InvalidOperationException("Could not create isolated test registration key.");
        key.SetValue("DisplayName", DisplayName, RegistryValueKind.String);
        key.SetValue("InstallLocation", installLocation, RegistryValueKind.String);
        key.SetValue("UninstallString", uninstallString, RegistryValueKind.String);
    }

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_registrySubkey, throwOnMissingSubKey: false);
        try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

