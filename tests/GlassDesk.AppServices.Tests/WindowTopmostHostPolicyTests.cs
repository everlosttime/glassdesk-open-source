using System.Windows.Threading;
using GlassDesk.App;
using GlassDesk.Core.Input;
using GlassDesk.Core.Storage;
using GlassDesk.Platform.Displays;

namespace GlassDesk.AppServices.Tests;

public sealed class WindowTopmostHostPolicyTests
{
    [Fact]
    public void ExplicitWindowSelectionWorksWhileAutomaticallyHiddenButStopsForManualSleepAndSessionLock()
    {
        var now = DateTimeOffset.UtcNow;
        var policy = new PerformanceCoordinator(now);
        AssertSelectionAllowed(policy, true);
        policy.SetVisibleSurfaces(false, false, now);
        policy.Advance(now.AddSeconds(10));
        Assert.Equal(PerformanceState.Sleeping, policy.State);
        AssertSelectionAllowed(policy, true);
        policy.SetManualSleep(true, now.AddSeconds(11));
        AssertSelectionAllowed(policy, false);
        policy.SetManualSleep(false, now.AddSeconds(12));
        AssertSelectionAllowed(policy, true);
        policy.SetSessionSuspended(true, now.AddSeconds(13));
        AssertSelectionAllowed(policy, false);
        policy.SetSessionSuspended(false, now.AddSeconds(14));
        AssertSelectionAllowed(policy, true);
    }

    [Fact]
    public void GlobalWindowSelectionActionDoesNotDependOnLauncherVisibility()
    {
        const InputAction action = InputAction.SelectWindowTopmost;
        var data = new GlassDeskDataStore(new AppPaths(Path.Combine(Path.GetTempPath(), "GD-topmost-policy-" + Guid.NewGuid().ToString("N"))));
        var display = new PrimaryDisplaySession(new DisplayInfo("test", true, new(0, 0, 1920, 1080), new(0, 0, 1920, 1040), 96));
        using var controller = new InteractionController(data, display, 0, Dispatcher.CurrentDispatcher, _ => true, _ => { }, _ => { });
        controller.SetLauncherAvailability(false, false);
        Assert.True(controller.CanExecute(action));
    }

    private static void AssertSelectionAllowed(PerformanceCoordinator policy, bool expected)
        => Assert.Equal(expected, policy.AllowsWindowSelection);
}
