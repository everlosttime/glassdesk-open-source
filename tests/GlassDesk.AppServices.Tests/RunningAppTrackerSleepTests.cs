using GlassDesk.Core.Storage;

namespace GlassDesk.AppServices.Tests;

public sealed class RunningAppTrackerSleepTests
{
    [Fact]
    public void SuspendedWindowEventsMarkDirtyWithoutEnumeratingAndResumeReconcilesOnce()
    {
        using var temp = new TempWorkspace();
        var store = temp.CreateStore();
        store.LoadAll();
        using var tracker = new RunningAppTracker(store.Library);
        var setter = typeof(RunningAppTracker).GetMethod("SetSuspended");
        Assert.NotNull(setter);
        var count = typeof(RunningAppTracker).GetProperty("ReconcileCount");
        Assert.NotNull(count);
        tracker.Reconcile();
        var before = (long)count.GetValue(tracker)!;
        setter.Invoke(tracker, [true]);
        tracker.Reconcile();
        tracker.Reconcile();
        Assert.Equal(before, (long)count.GetValue(tracker)!);
        setter.Invoke(tracker, [false]);
        Assert.Equal(before + 1, (long)count.GetValue(tracker)!);
        setter.Invoke(tracker, [false]);
        Assert.Equal(before + 1, (long)count.GetValue(tracker)!);
    }
}
