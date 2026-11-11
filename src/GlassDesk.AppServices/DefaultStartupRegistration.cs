using GlassDesk.Core.Storage;
using GlassDesk.Platform.Installation;
using GlassDesk.Platform.Startup;

namespace GlassDesk.AppServices;

/// <summary>Enable login startup once for a new installed profile; later launches respect Windows' actual state.</summary>
public static class DefaultStartupRegistration
{
    public static StartupChangeResult? Apply(SettingsStore settings, LoadStatus loadStatus,
        InstallationState installation, StartupRegistration registration, string executablePath)
    {
        if (loadStatus != LoadStatus.FirstRun || installation != InstallationState.VerifiedInstalled) return null;
        var current = registration.Query(executablePath);
        if (current.State is StartupRegistrationState.OtherTarget or StartupRegistrationState.Unavailable)
            return new(false, current, current.Error ?? "现有登录启动项不属于当前程序，未自动修改。");
        try
        {
            // Persist the fresh profile before touching Windows. A subsequent
            // manual disable must never look like another first run.
            settings.Save();
        }
        catch (StorageException ex) { return new(false, current, "无法保存首次运行设置：" + ex.Message); }
        return current.State == StartupRegistrationState.Owned ? new(true, current, null)
            : registration.SetEnabled(executablePath, true);
    }
}
