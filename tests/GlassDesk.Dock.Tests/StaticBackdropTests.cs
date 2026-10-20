using System.Reflection;

namespace GlassDesk.Dock.Tests;

public sealed class StaticBackdropTests
{
    [Fact]
    public void ValidatedStaticPixelsKeepTheirSourceTimeAndExpireAtSceneBoundaries()
    {
        using var state = new LatestBackdropState();
        var request = state.Request(new(0, 0, 2, 2), 2, 2);
        var frame = new BackdropFrame(request, 1, 100, 110, new byte[16], 100);
        var property = typeof(BackdropFrame).GetProperty("IsStatic", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(property); property.SetValue(frame, true);
        Assert.True(state.Publish(frame));
        Assert.True(state.TryRead(10000, 120, out var reused));
        Assert.Same(frame, reused); Assert.Equal(100, reused!.SourceTicks);
        state.Request(new(2, 0, 4, 2), 2, 2);
        Assert.False(state.TryRead(10001, 120, out _));
        Assert.False(state.Publish(frame));
    }

    [Fact]
    public void StaticCacheDoesNotCrossASuspendOrAcceptAFutureTimestamp()
    {
        using var state = new LatestBackdropState();
        var request = state.Request(new(0, 0, 2, 2), 2, 2);
        var frame = new BackdropFrame(request, 1, 100, 110, new byte[16], 100);
        var property = typeof(BackdropFrame).GetProperty("IsStatic", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(property); property.SetValue(frame, true);
        Assert.True(state.Publish(frame)); Assert.False(state.TryRead(99, 120, out _));
        state.Suspend(); state.Request(new(0, 0, 2, 2), 2, 2);
        Assert.False(state.Publish(frame)); Assert.False(state.TryRead(10000, 120, out _));
    }
}
