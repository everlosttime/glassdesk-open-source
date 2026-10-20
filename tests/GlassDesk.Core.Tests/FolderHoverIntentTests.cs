using GlassDesk.Core.Models;

namespace GlassDesk.Core.Tests;

public sealed class FolderHoverIntentTests
{
    [Fact]
    public void RequiresContinuousOverlapFor500Milliseconds()
    {
        var intent = new FolderHoverIntent();
        Assert.False(intent.Update("a", true, TimeSpan.Zero));
        Assert.False(intent.Update("a", true, TimeSpan.FromMilliseconds(499)));
        Assert.True(intent.Update("a", true, TimeSpan.FromMilliseconds(500)));
        Assert.False(intent.Update("a", false, TimeSpan.FromMilliseconds(501)));
        Assert.Null(intent.TargetId);
        Assert.False(intent.Update("a", true, TimeSpan.FromMilliseconds(700)));
    }

    [Fact]
    public void SwitchingTargetOrCancellingRestartsDwell()
    {
        var intent = new FolderHoverIntent();
        intent.Update("a", true, TimeSpan.Zero);
        Assert.False(intent.Update("b", true, TimeSpan.FromMilliseconds(500)));
        Assert.True(intent.Update("b", true, TimeSpan.FromMilliseconds(1000)));
        intent.Reset();
        Assert.False(intent.Update("b", true, TimeSpan.FromMilliseconds(1100)));
        Assert.False(intent.IsReady);
    }

    [Fact]
    public void SpringConvergesAndRetargetsWithoutJump()
    {
        var spring = new FolderSpring();
        spring.Target = 1;
        for (var i = 0; i < 12; i++) spring.Advance(1d / 60);
        var current = spring.Value;
        Assert.InRange(current, .1, 1.12);
        spring.Target = 0;
        Assert.Equal(current, spring.Value);
        for (var i = 0; i < 180; i++) spring.Advance(1d / 60);
        Assert.InRange(Math.Abs(spring.Value), 0, .001);
        Assert.False(spring.IsMoving);
    }
}
