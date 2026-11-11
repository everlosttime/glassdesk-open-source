using System.Reflection;
using GlassDesk.Core.Storage;
using GlassDesk.Platform.Installation;
using GlassDesk.Platform.Startup;
using Microsoft.Win32;

namespace GlassDesk.AppServices.Tests;

public sealed class DefaultStartupRegistrationTests : IDisposable
{
    private readonly string _id = "GlassDesk.DefaultStartup.Tests-" + Guid.NewGuid().ToString("N");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GlassDesk-default-startup-" + Guid.NewGuid().ToString("N"));
    private readonly RegistryKey _key;
    private readonly string _exe;
    private readonly SettingsStore _settings;
    public DefaultStartupRegistrationTests()
    {
        Directory.CreateDirectory(_root);
        _exe = Path.Combine(_root, "GlassDesk.exe"); File.WriteAllText(_exe, "test fixture, never executed");
        _key = Registry.CurrentUser.CreateSubKey(@"Software\GlassDesk.Tests\" + _id)!;
        _settings = new SettingsStore(Path.Combine(_root, "settings.json")); _settings.Load();
    }

    [Fact]
    public void FreshInstalledProfileDefaultsToEnabledAndManualDisableSurvivesRestart()
    {
        var data = new GlassDeskDataStore(new AppPaths(_root));
        var first = data.LoadAll();
        Assert.Equal(LoadStatus.FirstRun, first.SettingsStatus);
        var registration = new StartupRegistration(_key, _id);
        Assert.True(Apply(first.SettingsStatus, InstallationState.VerifiedInstalled, registration)!.Succeeded);
        Assert.Equal(StartupRegistrationState.Owned, registration.Query(_exe).State);
        Assert.False(_settings.Current.OnboardingDismissed);
        Assert.True(registration.SetEnabled(_exe, false).Succeeded);
        var reloaded = _settings.Load();
        Assert.Equal(LoadStatus.Loaded, reloaded.Status);
        Assert.Equal(reloaded.Status, data.LoadAll().SettingsStatus);
        Assert.Null(Apply(data.LoadAll().SettingsStatus, InstallationState.VerifiedInstalled, registration));
        Assert.Equal(StartupRegistrationState.Missing, registration.Query(_exe).State);
    }

    [Theory]
    [InlineData(LoadStatus.Loaded, InstallationState.VerifiedInstalled)]
    [InlineData(LoadStatus.RecoveredFromBackup, InstallationState.VerifiedInstalled)]
    [InlineData(LoadStatus.Unrecoverable, InstallationState.VerifiedInstalled)]
    [InlineData(LoadStatus.FirstRun, InstallationState.Portable)]
    [InlineData(LoadStatus.FirstRun, InstallationState.RegistrationMismatch)]
    public void ExistingChoicesAndUninstalledCopiesRemainUntouched(LoadStatus load, InstallationState installation)
    {
        Assert.Null(Apply(load, installation, new StartupRegistration(_key, _id)));
        Assert.Null(_key.GetValue(_id)); Assert.False(File.Exists(_settings.FilePath));
    }

    [Fact]
    public void ConflictingStartupEntryIsKept()
    {
        const string other = "\"C:\\Other\\GlassDesk.exe\" --autostart";
        _key.SetValue(_id, other);
        Assert.False(Apply(LoadStatus.FirstRun, InstallationState.VerifiedInstalled, new StartupRegistration(_key, _id))!.Succeeded);
        Assert.Equal(other, _key.GetValue(_id));
    }

    [Fact]
    public void FailureToPersistFirstRunDoesNotRegisterStartup()
    {
        using (File.Open(_settings.FilePath + AtomicFile.TempSuffix, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            Assert.False(Apply(LoadStatus.FirstRun, InstallationState.VerifiedInstalled, new StartupRegistration(_key, _id))!.Succeeded);
        Assert.Null(_key.GetValue(_id)); Assert.False(File.Exists(_settings.FilePath));
    }

    private StartupChangeResult? Apply(LoadStatus load, InstallationState installation, StartupRegistration registration)
    {
        var type = typeof(AppServiceHost).Assembly.GetType("GlassDesk.AppServices.DefaultStartupRegistration"); Assert.NotNull(type);
        return (StartupChangeResult?)type.GetMethod("Apply", BindingFlags.Public | BindingFlags.Static)!.Invoke(null,
            [_settings, load, installation, registration, _exe]);
    }

    public void Dispose()
    {
        _key.Dispose(); Registry.CurrentUser.DeleteSubKeyTree(@"Software\GlassDesk.Tests\" + _id, false);
        Directory.Delete(_root, true);
    }
}
