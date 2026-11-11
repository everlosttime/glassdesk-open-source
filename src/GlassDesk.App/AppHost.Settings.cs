using GlassDesk.Platform.Installation;
using GlassDesk.Platform.Startup;
using GlassDesk.Settings;

namespace GlassDesk.App;

internal sealed partial class AppHost
{
    // Keep the opened instance for selection, scroll and unfinished edits. Only
    // constructing an unused Settings tree at startup is avoided here.
    private SettingsWindow EnsureSettingsWindow()
    {
        _application.Dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed || _shuttingDown,this);
        if (_settings is not null) return _settings;
        var primary=_primaryDisplaySession??throw new InvalidOperationException("Settings requires a primary display session.");
        var data=_data??throw new InvalidOperationException("Settings requires the loaded data store.");
        var window=new SettingsWindow();
        try
        {
            window.BindPrimaryDisplay(primary);
            window.AttachDataStore(data,_loadReport);
            window.AttachStartupRegistration(new StartupRegistration(),Environment.ProcessPath??string.Empty);
            window.AttachInstallation(new InstallationLocator().Inspect(Environment.ProcessPath??string.Empty));
            window.LauncherSettingsChanged+=(_,settings)=>_launcher?.ApplySettings(settings);
            window.PerformanceSettingsChanged+=OnPerformanceSettingsChanged;
            window.InteractionSettingsApply=ApplyInteractionSettings;
            window.DesktopIconsRead=()=>_interaction?.ReadDesktopIcons();
            window.DesktopIconsSet=visible=>_interaction is { } controller?controller.SetDesktopIcons(visible):"运行服务未连接，未保存更改。";
            window.PrepareNaiwaAsync=PrepareNaiwaAsync;
            window.RunNaiwaAction=RunNaiwaAction;
            window.NaiwaSettingsChanged+=(_,_)=>ReconcileNaiwa();
            window.ShowNaiwaStatus(_naiwaStatus);
            window.DataRestored+=OnSettingsDataRestored;
            window.UserDataDeletionRequested+=OnSettingsUserDataDeletionRequested;
            window.UninstallRequested+=OnSettingsUninstallRequested;
            if (_interactionStatus is { } status) window.ShowInteractionStatus(status);
            window.NearbySettingsApply=ApplyNearbySettingsAsync;
            window.OpenNearbyPanel=()=>ShowNearbyPanel(null);
            window.BeforeDataRestore=StopNearbyForRestoreAsync;
            window.AfterDataRestoreFailure=ResumeNearbyAfterFailedRestoreAsync;
            if (_services is not null) window.UpdateNearbyStatus(NearbyStatusText());
            window.OnboardingRequested+=OnOnboardingRequested;
            BindSettingsUpdates(window);
            window.Icon=CreateWindowIcon();
            window.Closing+=OnWindowClosing;
            _settings=window;
            return window;
        }
        catch { window.Close(); throw; }
    }
}
