using System.Reflection;
using GlassDesk.App;

namespace GlassDesk.AppServices.Tests;

public sealed class PerformanceCoordinatorTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 11, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FullscreenDwellAndExitDwellDoNotThrashDuringQuickAltTab()
    {
        var runtime = new Runtime();
        runtime.Call("ObserveForeground", true, false, Start);
        runtime.Call("Advance", Start.AddMilliseconds(499));
        Assert.Equal("Active", runtime.State);
        runtime.Call("Advance", Start.AddMilliseconds(500));
        Assert.Equal("Sleeping", runtime.State);
        var generation = runtime.Generation;
        runtime.Call("ObserveForeground", false, false, Start.AddMilliseconds(700));
        runtime.Call("Advance", Start.AddMilliseconds(999));
        Assert.Equal("Sleeping", runtime.State);
        runtime.Call("ObserveForeground", true, false, Start.AddMilliseconds(999));
        runtime.Call("Advance", Start.AddMilliseconds(1500));
        Assert.Equal(generation, runtime.Generation);
        runtime.Call("ObserveForeground", false, false, Start.AddMilliseconds(1600));
        runtime.Call("Advance", Start.AddMilliseconds(1900));
        Assert.Equal("Waking", runtime.State);
        runtime.Call("CompleteWake", Start.AddMilliseconds(1900));
        Assert.Equal("Active", runtime.State);
    }

    [Fact]
    public void OwnTrayActivationRetainsForeignFullscreenAndExplicitWakeIsTemporary()
    {
        var runtime = new Runtime();
        runtime.Call("ObserveForeground", true, false, Start);
        runtime.Call("Advance", Start.AddMilliseconds(500));
        runtime.Call("ObserveForeground", false, true, Start.AddSeconds(1));
        runtime.Call("Advance", Start.AddSeconds(2));
        Assert.Equal("Sleeping", runtime.State);
        runtime.Call("RequestTemporaryActivation", Start.AddSeconds(2));
        Assert.Equal("Waking", runtime.State);
        runtime.Call("CompleteWake", Start.AddSeconds(2));
        Assert.Equal("TemporaryActive", runtime.State);
        runtime.Call("SetVisibleSurfaces", false, true, Start.AddSeconds(2));
        runtime.Call("SetVisibleSurfaces", false, false, Start.AddSeconds(3));
        Assert.Equal("Sleeping", runtime.State);
    }

    [Fact]
    public void NormalHiddenPreservesResourcesForFiveSecondsAndWakesOnVisibility()
    {
        var runtime = new Runtime();
        runtime.Call("SetVisibleSurfaces", false, false, Start);
        Assert.Equal("WarmHidden", runtime.State);
        runtime.Call("Advance", Start.AddMilliseconds(4999));
        Assert.Equal("WarmHidden", runtime.State);
        runtime.Call("Advance", Start.AddSeconds(5));
        Assert.Equal("Sleeping", runtime.State);
        runtime.Call("SetVisibleSurfaces", true, false, Start.AddSeconds(6));
        Assert.Equal("Waking", runtime.State);
        runtime.Call("CompleteWake", Start.AddSeconds(6));
        Assert.Equal("Active", runtime.State);
    }

    [Fact]
    public void ManualSleepAndSessionSleepWorkWhenAutomaticFullscreenSleepIsDisabled()
    {
        var runtime = new Runtime();
        runtime.Call("SetAutoSuspend", false, Start);
        runtime.Call("ObserveForeground", true, false, Start);
        runtime.Call("Advance", Start.AddSeconds(1));
        Assert.Equal("Active", runtime.State);
        runtime.Call("SetManualSleep", true, Start.AddSeconds(1));
        Assert.Equal("Sleeping", runtime.State);
        runtime.Call("SetManualSleep", false, Start.AddSeconds(2));
        Assert.Equal("Waking", runtime.State);
        runtime.Call("CompleteWake", Start.AddSeconds(2));
        runtime.Call("SetSessionSuspended", true, Start.AddSeconds(3));
        Assert.Equal("Sleeping", runtime.State);
        runtime.Call("RequestTemporaryActivation", Start.AddSeconds(4));
        Assert.Equal("Sleeping", runtime.State);
        runtime.Call("SetSessionSuspended", false, Start.AddSeconds(5));
        Assert.Equal("Waking", runtime.State);
    }

    private sealed class Runtime
    {
        private readonly object _instance;
        private readonly Type _type;
        public Runtime()
        {
            _type = typeof(AppHost).Assembly.GetType("GlassDesk.App.PerformanceCoordinator")!;
            Assert.NotNull(_type);
            _instance = Activator.CreateInstance(_type, [Start])!;
        }
        public string State => _type.GetProperty("State")!.GetValue(_instance)!.ToString()!;
        public long Generation => (long)_type.GetProperty("Generation")!.GetValue(_instance)!;
        public void Call(string name, params object[] values) => _type.GetMethod(name)!.Invoke(_instance, values);
    }
}
