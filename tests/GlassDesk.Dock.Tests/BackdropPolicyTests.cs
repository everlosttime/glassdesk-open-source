using System.Reflection;
using System.Diagnostics;

namespace GlassDesk.Dock.Tests;

public sealed class BackdropPolicyTests
{
    [Fact]
    public void RepeatedFailuresBackOffAndAHealthyFrameResetsTheCooldown()
    {
        var type = typeof(SharedBackdropFeed).Assembly.GetType("BackdropProviderPolicy");
        Assert.NotNull(type);
        var policy = Activator.CreateInstance(type, nonPublic: true)!;
        var fail = type.GetMethod("RecordFailure", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var allowed = type.GetMethod("RetryAllowed", BindingFlags.Instance | BindingFlags.NonPublic)!;
        long now = Stopwatch.Frequency;
        foreach (var seconds in new[] { 1, 2, 5, 10, 30, 30 })
        {
            fail.Invoke(policy, [now]);
            Assert.False((bool)allowed.Invoke(policy, [now + (seconds * Stopwatch.Frequency) - 1])!);
            now += seconds * Stopwatch.Frequency;
            Assert.True((bool)allowed.Invoke(policy, [now])!);
        }
        type.GetMethod("RecordSuccess", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(policy, null);
        fail.Invoke(policy, [now]);
        Assert.True((bool)allowed.Invoke(policy, [now + Stopwatch.Frequency])!);
    }
}
