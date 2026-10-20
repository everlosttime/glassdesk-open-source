using GlassDesk.Platform.Displays;

namespace GlassDesk.AppServices.Tests;

public sealed class PrimaryDisplayRecoveryTests
{
    [Fact]
    public void ExplicitRecoveryRebindsTheExistingSessionAndRefreshesTheNewPrimary()
    {
        var session = new PrimaryDisplaySession(new("DISPLAY1", true, new(0,0,1920,1080), new(0,0,1920,1040), 96));
        var changed = new DisplayInfo("DISPLAY2", true, new(-2560,0,2560,1440), new(-2560,0,2560,1400), 192);
        Assert.False(session.TryRefreshPrimary(changed));
        var recovery = typeof(PrimaryDisplaySession).GetMethod("TryRebindPrimary");
        Assert.NotNull(recovery);
        Assert.True((bool)recovery.Invoke(session, [changed])!);
        Assert.True(session.IsAvailable);
        Assert.Equal(changed, session.Display);
        Assert.True(session.TryRefreshPrimary(changed with { Dpi=144 }));
        Assert.Equal((uint)144, session.Display.Dpi);
        Assert.Equal("DISPLAY1", session.StartupDeviceName);
    }

    [Fact]
    public void MissingDisplayStaysUnavailableUntilAnActualPrimaryReturns()
    {
        var session = new PrimaryDisplaySession(new("DISPLAY1", true, new(0,0,1920,1080), new(0,0,1920,1040), 96));
        var recovery = typeof(PrimaryDisplaySession).GetMethod("TryRebindPrimary");
        Assert.NotNull(recovery);
        Assert.False((bool)recovery.Invoke(session, [null])!);
        Assert.False(session.IsAvailable);
        Assert.False((bool)recovery.Invoke(session, [session.Display with { IsPrimary=false }])!);
        Assert.True((bool)recovery.Invoke(session, [session.Display])!);
        Assert.True(session.IsAvailable);
    }
}
