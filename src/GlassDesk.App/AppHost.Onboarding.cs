using System.Windows.Threading;
using GlassDesk.Settings;

namespace GlassDesk.App;

internal sealed partial class AppHost
{
    private OnboardingWindow? _onboarding;
    private void InitializeOnboarding()
    {
        if (_data!.Settings.Current.OnboardingDismissed != false) return;
        _application.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(ShowOnboarding));
    }
    private void OnOnboardingRequested(object? sender, EventArgs e) => ShowOnboarding();
    private void ShowOnboarding()
    {
        if (_disposed || _shuttingDown || _primaryDisplaySession?.IsAvailable != true || _data is null) return;
        if (_onboarding is not null) { _onboarding.Activate(); return; }
        var window = new OnboardingWindow(_data.Settings) { Icon = CreateWindowIcon() };
        _onboarding = window; window.BindPrimaryDisplay(_primaryDisplaySession);
        // Only the explicit final action opens real settings. Practice stays isolated.
        window.SettingsPageRequested += (_, page) => { ShowSettings(); _settings?.NavigateToPage(page); };
        window.Closed += (_, _) => { if (ReferenceEquals(_onboarding, window)) _onboarding = null; };
        window.PositionOnPrimary(); window.Show(); window.Activate();
    }
    private void DisposeOnboarding()
    {
        if (_settings is not null) _settings.OnboardingRequested -= OnOnboardingRequested;
        _onboarding?.Close(); _onboarding = null;
    }
}
