using GlassDesk.App.Startup;
using GlassDesk.AppServices;
using GlassDesk.Platform.Installation;
using GlassDesk.Platform.Startup;

namespace GlassDesk.App;

internal sealed partial class AppHost
{
    private string? _startupRegistrationFailure;
    private void InitializeDefaultAutostart()
    {
        if (_options.Sample != AppearanceSample.None || _data is null || _loadReport is null) return;
        var executable = Environment.ProcessPath ?? string.Empty;
        var result = DefaultStartupRegistration.Apply(_data.Settings, _loadReport.SettingsStatus,
            new InstallationLocator().Inspect(executable).State, new StartupRegistration(), executable);
        if (result is null) return;
        _settings?.RefreshStartupRegistration();
        if (!result.Succeeded)
        {
            _startupRegistrationFailure=result.Error??"未知错误。";
            _settings?.ShowStartupRegistrationFailure(_startupRegistrationFailure);
        }
    }
}
