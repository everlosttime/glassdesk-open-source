using GlassDesk.Platform.Displays;

namespace GlassDesk.AppServices.Tests;

public sealed class PrimaryDisplaySessionTests
{
    [Fact]
    public void SessionCannotStartFromASecondaryDisplay()
    {
        var secondary = Display("DISPLAY2", false,
            new PixelRect(1920, 0, 2560, 1440), new PixelRect(1920, 0, 2560, 1400), 192);

        Assert.Throws<ArgumentException>(() => new PrimaryDisplaySession(secondary));
    }

    [Fact]
    public void CaptureRegionIsClippedAtPrimaryRightEdge()
    {
        var session = new PrimaryDisplaySession(Display("DISPLAY1", true,
            new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), 144));

        var clipped = session.ClipToPrimaryBounds(new PixelRect(1860, 420, 160, 240));

        Assert.Equal(new PixelRect(1860, 420, 60, 240), clipped);
    }

    [Fact]
    public void CaptureRegionWithNegativeVirtualDesktopCoordinatesIsClippedAtPrimaryLeftEdge()
    {
        var session = new PrimaryDisplaySession(Display("DISPLAY1", true,
            new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), 96));

        var clipped = session.ClipToPrimaryBounds(new PixelRect(-120, 300, 220, 80));

        Assert.Equal(new PixelRect(0, 300, 100, 80), clipped);
    }

    [Fact]
    public void SamePrimaryRefreshUpdatesOnlyThatDisplaysCurrentMetrics()
    {
        var session = new PrimaryDisplaySession(Display("DISPLAY1", true,
            new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), 96));

        var refreshed = session.TryRefreshPrimary(Display("DISPLAY1", true,
            new PixelRect(0, 0, 2560, 1440), new PixelRect(0, 0, 2560, 1400), 144));

        Assert.True(refreshed);
        Assert.False(session.RequiresRestart);
        Assert.Equal(new PixelRect(0, 0, 2560, 1440), session.Display.Bounds);
        Assert.Equal((uint)144, session.Display.Dpi);
    }

    [Fact]
    public void PrimaryIdentityChangeRequiresRestartAndCannotRebindToNewDisplay()
    {
        var session = new PrimaryDisplaySession(Display("DISPLAY1", true,
            new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), 96));

        Assert.False(session.TryRefreshPrimary(Display("DISPLAY2", true,
            new PixelRect(0, 0, 2560, 1440), new PixelRect(0, 0, 2560, 1400), 192)));
        Assert.True(session.RequiresRestart);
        Assert.False(session.IsAvailable);
        Assert.False(session.TryRefreshPrimary(Display("DISPLAY1", true,
            new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), 96)));
        Assert.Equal("DISPLAY1", session.StartupDeviceName);
    }

    [Fact]
    public void WindowPlacementKeepsTheWholeWindowInsidePrimaryWorkArea()
    {
        var session = new PrimaryDisplaySession(Display("DISPLAY1", true,
            new PixelRect(0, 0, 1920, 1080), new PixelRect(40, 30, 1840, 1000), 144));

        var clamped = session.ClampWindowToWorkArea(new PixelRect(1780, 980, 400, 180));

        Assert.Equal(new PixelRect(1480, 850, 400, 180), clamped);
        Assert.True(session.ContainsInWorkArea(clamped));
    }

    private static DisplayInfo Display(string name, bool primary, PixelRect bounds, PixelRect workArea, uint dpi) =>
        new(name, primary, bounds, workArea, dpi);
}
