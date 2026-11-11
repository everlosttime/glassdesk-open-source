namespace GlassDesk.Nearby.Tests;

public sealed class DiscoveryThrottleTests
{
    [Fact]
    public void RepeatedAnnouncementsDoNotCreateAHandshakeLoop()
    {
        var throttle = new DiscoveryThrottle(); var now = DateTimeOffset.UtcNow;
        Assert.True(throttle.TryBegin("192.168.1.1:53317", "A", now));
        Assert.False(throttle.TryBegin("192.168.1.1:53317", "A", now.AddSeconds(2)));
        Assert.True(throttle.TryBegin("192.168.1.2:53317", "A", now.AddSeconds(2)));
        Assert.True(throttle.TryBegin("192.168.1.1:53317", "A", now.AddSeconds(31)));
    }
}
