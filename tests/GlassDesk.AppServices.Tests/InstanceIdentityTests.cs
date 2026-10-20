using GlassDesk.Platform;

namespace GlassDesk.AppServices.Tests;

public sealed class InstanceIdentityTests
{
    [Fact]
    public void 同一用户会话得到稳定的Mutex与管道名而不同用户或会话隔离()
    {
        var owner = new InstanceIdentity("S-1-5-21-1001", 42);
        var sameSession = new InstanceIdentity("S-1-5-21-1001", 42);
        var otherSession = new InstanceIdentity("S-1-5-21-1001", 43);
        var otherUser = new InstanceIdentity("S-1-5-21-1002", 42);

        Assert.Equal(owner.GetMutexName("GlassDesk.Host"), sameSession.GetMutexName("GlassDesk.Host"));
        Assert.Equal(owner.GetPipeName("GlassDesk.Host"), sameSession.GetPipeName("GlassDesk.Host"));
        Assert.StartsWith(@"Local\", owner.GetMutexName("GlassDesk.Host"), StringComparison.Ordinal);
        Assert.NotEqual(owner.GetMutexName("GlassDesk.Host"), otherSession.GetMutexName("GlassDesk.Host"));
        Assert.NotEqual(owner.GetPipeName("GlassDesk.Host"), otherSession.GetPipeName("GlassDesk.Host"));
        Assert.NotEqual(owner.GetMutexName("GlassDesk.Host"), otherUser.GetMutexName("GlassDesk.Host"));
        Assert.NotEqual(owner.GetPipeName("GlassDesk.Host"), otherUser.GetPipeName("GlassDesk.Host"));
    }

    [Fact]
    public async Task 当前用户会话的重复实例通过私有管道接收命令()
    {
        var name = "pipe-test-" + Guid.NewGuid().ToString("N");
        using var first = SingleInstanceGuard.Acquire(name);
        using var second = SingleInstanceGuard.Acquire(name);
        Assert.True(first.IsFirstInstance);
        Assert.False(second.IsFirstInstance);

        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        first.CommandReceived += (_, command) => received.TrySetResult(command);
        first.StartListening();

        Assert.True(await SingleInstanceGuard.TrySendCommandAsync(name, "show-launcher", TimeSpan.FromSeconds(3)));
        Assert.Equal("show-launcher", await received.Task.WaitAsync(TimeSpan.FromSeconds(3)));
    }
}
