using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using GlassDesk.App;
using GlassDesk.App.Startup;
using GlassDesk.UI;
using Microsoft.Win32;

namespace GlassDesk.AppServices.Tests;

public sealed class PerformanceEnvironmentTests
{
    [Fact]
    public void WallpaperNotificationInvalidatesCachedScenesWithoutLeavingGameSleep()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                using var host = new AppHost(application, CommandLineOptions.Parse([]));
                var policy = new PerformanceCoordinator(DateTimeOffset.UtcNow);
                policy.SetManualSleep(true, DateTimeOffset.UtcNow);
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                typeof(AppHost).GetField("_performance", flags)!.SetValue(host, policy);
                var generation = typeof(BackdropRecovery).GetProperty("Generation", BindingFlags.Static | BindingFlags.NonPublic)!;
                var before = (long)generation.GetValue(null)!;
                typeof(AppHost).GetMethod("OnSystemUserPreferenceChanged", flags)!.Invoke(host,
                    [null, new UserPreferenceChangedEventArgs(UserPreferenceCategory.Desktop)]);
                Assert.True((long)generation.GetValue(null)! > before, "Wallpaper changes must invalidate the saved scene even during sleep.");
                Assert.Equal(PerformanceState.Sleeping, policy.State);
                application.Shutdown();
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
