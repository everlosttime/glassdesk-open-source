using GlassDesk.UI.Controls;
using System.Windows;

namespace GlassDesk.Dock.Tests;

public sealed class GlassFrameWorkerTests
{
    [Fact]
    public async Task HideAfterComputationBeforeUiPublicationInvalidatesCompletedFrame()
    {
        var worker = new GlassFrameWorker();
        var result = (await worker.RenderAsync(() => new byte[] { 10 }))!;
        Assert.True(worker.IsCurrent(result));
        worker.Invalidate();
        Assert.False(worker.IsCurrent(result));
    }

    [Fact]
    public async Task MovingCompositionOffUiThreadPreservesEveryOutputByte()
    {
        var pixels = Enumerable.Range(0, 64 * 48 * 4).Select(i => (byte)(i * 17)).ToArray();
        byte[] Compose() => LiquidGlassFrameComposer.Compose(new LiquidGlassOptics(), pixels,
            64, 48, new Size(64, 48), 12, true, .8, .4, LiquidGlassDebugView.None,
            LiquidGlassSamplingParameters.Default, LiquidGlassWeightParameters.Default,
            new Rect(4, 4, 56, 40), LiquidGlassLodParameters.Default,
            LiquidGlassFinishingParameters.Default, LiquidGlassBrdfParameters.Default,
            LiquidGlassDispersionParameters.Default);
        var expected = Compose();
        var result = await new GlassFrameWorker().RenderAsync(Compose);
        Assert.Equal(expected, result!.Pixels);
    }

    [Fact]
    public async Task PendingFrameDoesNotQueueMoreWorkAndInvalidatedResultCannotReturn()
    {
        var worker = new GlassFrameWorker();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var first = worker.RenderAsync(() =>
        {
            entered.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            return new byte[] { 10 };
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(worker.IsBusy);
            Assert.Null(await worker.RenderAsync(() => throw new InvalidOperationException("must not queue")));
            worker.Invalidate();
        }
        finally { release.Set(); }
        Assert.Null(await first);
        Assert.False(worker.IsBusy);
        Assert.Equal(new byte[] { 20 }, (await worker.RenderAsync(() => new byte[] { 20 }))!.Pixels);
    }

    [Fact]
    public void OpticalComputationDoesNotExecuteOnTheCallingUiThread()
    {
        StaTest.Run(() =>
        {
            var uiThread = Environment.CurrentManagedThreadId;
            var worker = new GlassFrameWorker();
            var result = worker.RenderAsync(() =>
            {
                Assert.NotEqual(uiThread, Environment.CurrentManagedThreadId);
                return new byte[] { 10, 20, 30, 255 };
            }).GetAwaiter().GetResult();
            Assert.Equal(new byte[] { 10, 20, 30, 255 }, result!.Pixels);
        });
    }

    [Fact]
    public async Task RenderFailureReleasesWorkerAndPropagatesForCrashDiagnostics()
    {
        var worker = new GlassFrameWorker();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            worker.RenderAsync(() => throw new InvalidOperationException("render failure")));
        Assert.False(worker.IsBusy);
        Assert.NotNull(await worker.RenderAsync(() => new byte[] { 42 }));
    }
}
