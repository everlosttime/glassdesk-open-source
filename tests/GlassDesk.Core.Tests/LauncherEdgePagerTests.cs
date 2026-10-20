using GlassDesk.Core.Models;

namespace GlassDesk.Core.Tests;

public sealed class LauncherEdgePagerTests
{
    [Fact]
    public void EdgeDwell_ChangesOnePageAfterThresholdAndResetsAfterLeaving()
    {
        var pager = new LauncherEdgePager(TimeSpan.FromMilliseconds(600));
        var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        Assert.False(pager.Tick(1, start));
        Assert.False(pager.Tick(1, start.AddMilliseconds(599)));
        Assert.True(pager.Tick(1, start.AddMilliseconds(600)));
        Assert.False(pager.Tick(1, start.AddMilliseconds(601)));
        pager.Tick(0, start.AddMilliseconds(700));
        Assert.False(pager.Tick(1, start.AddMilliseconds(701)));
        Assert.True(pager.Tick(1, start.AddMilliseconds(1301)));
    }
}
