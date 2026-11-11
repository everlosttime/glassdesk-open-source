using GlassDesk.Platform.Battery;
namespace GlassDesk.Platform.Tests;

public sealed class BatteryServiceLifetimeTests
{
    [Fact]
    public async Task StoppedWorkerCannotPublishAProviderResultThatArrivesLate()
    {
        var provider = new DelayedProvider();
        var service = new PeripheralBatteryService([provider]);
        var updates = 0;
        service.Updated += (_, _) => Interlocked.Increment(ref updates);
        service.SetActive(true);
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        service.SetActive(false);
        provider.Result.SetResult([Reading()]);
        await provider.Returned.Task.WaitAsync(TimeSpan.FromSeconds(3));
        // Acquiring the serial gate also waits for the canceled worker to release it.
        var check = await service.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(service.IsActive);
        // The explicit discovery owns the new result; the old worker did not publish it.
        Assert.Single(check);
        Assert.Equal(1, updates);
    }

    [Fact]
    public async Task CancellationReleasesCallerButDoesNotOverlapAnUnfinishedProvider()
    {
        var provider = new DelayedProvider();
        var service = new PeripheralBatteryService([provider]);
        using var cancel = new CancellationTokenSource();
        var pending = service.RefreshAsync(cancel.Token);
        await provider.Started.Task;
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Empty(await service.RefreshAsync());
        Assert.Equal(1, provider.Calls);
        provider.Result.SetResult([Reading()]);
        await provider.Returned.Task;
        Assert.Empty(service.Current);
    }

    private static PeripheralBatterySnapshot Reading() => new("test", "Test", PeripheralBatteryCategory.Mouse,
        true, 50, false, DateTimeOffset.UtcNow, "test", "test", "");

    private sealed class DelayedProvider : IPeripheralBatteryProvider
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Returned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<IReadOnlyList<PeripheralBatterySnapshot>> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public int SuccessfulPublications { get; private set; }
        public async Task<IReadOnlyList<PeripheralBatterySnapshot>> QueryAsync(CancellationToken cancellationToken)
        {
            Calls++;
            Started.TrySetResult();
            var result = await Result.Task;
            if (!cancellationToken.IsCancellationRequested) SuccessfulPublications++;
            Returned.TrySetResult();
            return result;
        }
    }
}
