using System.Diagnostics;
using System.Reflection;

namespace GlassDesk.Dock.Tests;

public sealed class CaptureRecoveryDeadlineTests
{
    [Fact]
    public void StableFallbackPeriodicallyReprobesAfterRetiringItsResourceOwner()
    {
        long now = Stopwatch.Frequency;
        var fallback = new TestSource(() => now, onlyFirst: true);
        var restored = new TestSource(() => now);
        var attempts = 0;
        using var capture = new RecoveringDesktopSceneCapture(new(0, 0, 2, 2),
            () => { if (++attempts == 1) throw new InvalidOperationException("temporarily unavailable");
                Assert.True(fallback.Disposed); return restored; }, () => fallback, () => now);
        Assert.NotNull(capture.TryRead());
        now += Stopwatch.Frequency * 29;
        Assert.False(capture.UpdateExclusions());
        Assert.Equal(1, attempts);
        now += Stopwatch.Frequency * 2;
        Assert.True(capture.UpdateExclusions());
        Assert.Equal(2, attempts);
        Assert.NotNull(capture.TryRead());
    }

    [Fact]
    public void RepeatedSceneChangesCannotExtendAnActiveMissingFrameDeadline()
    {
        long now = Stopwatch.Frequency;
        var fallback = new TestSource(() => now, onlyFirst: true);
        using var capture = new RecoveringDesktopSceneCapture(new(0, 0, 2, 2),
            () => throw new InvalidOperationException("Native unavailable"), () => fallback, () => now);
        Assert.NotNull(capture.TryRead());
        for (var i = 0; i < 4; i++)
        {
            now += Stopwatch.Frequency;
            fallback.SceneChanged = true;
            Assert.True(capture.UpdateExclusions());
            Assert.Null(capture.TryRead(sourceNotBeforeTicks: now));
        }
        now += Stopwatch.Frequency;
        fallback.SceneChanged = true;
        Assert.True(capture.UpdateExclusions());
        Assert.Throws<TimeoutException>(() => capture.TryRead(sourceNotBeforeTicks: now));
    }

    [Theory]
    [InlineData("update")]
    [InlineData("release")]
    public void NativeLifecycleFaultsRecoverBeforeAnotherRead(string operation)
    {
        long now = Stopwatch.Frequency;
        var primary = new TestSource(() => now) { FailureOperation = operation };
        var fallback = new TestSource(() => now);
        using var capture = new RecoveringDesktopSceneCapture(new(0, 0, 2, 2),
            () => primary, () => { Assert.True(primary.Disposed); return fallback; }, () => now);
        if (operation == "update") capture.UpdateExclusions(); else capture.ReleaseReadbackBuffers();
        Assert.True(primary.Disposed);
        Assert.NotNull(capture.TryRead());
    }

    [Fact]
    public void FailedRetirementNeverStartsAnotherNativeResourceOwner()
    {
        long now = Stopwatch.Frequency;
        var primary = new TestSource(() => now) { FailureOperation = "update", FailDispose = true };
        var fallbackCreations = 0;
        var capture = new RecoveringDesktopSceneCapture(new(0, 0, 2, 2), () => primary,
            () => { fallbackCreations++; return new TestSource(() => now); }, () => now);
        Assert.Throws<InvalidOperationException>(() => capture.UpdateExclusions());
        Assert.Equal(0, fallbackCreations);
        // The error is terminal for this supervisor; repeated disposal is safe.
        capture.Dispose(); capture.Dispose();
    }

    [Fact]
    public void DisposedSupervisorCannotResurrectAHelper()
    {
        var creations = 0;
        var capture = new RecoveringDesktopSceneCapture(new(0, 0, 2, 2),
            () => new TestSource(Stopwatch.GetTimestamp),
            () => { creations++; return new TestSource(Stopwatch.GetTimestamp); }, Stopwatch.GetTimestamp);
        capture.Dispose();
        Assert.Throws<ObjectDisposedException>(() => capture.TryRead());
        Assert.Equal(0, creations);
    }
    [Fact]
    public void HiddenTimeDoesNotTriggerRecoveryBeforeTheResumedCaptureHasTimeToDeliver()
    {
        long now = Stopwatch.Frequency;
        var primary = new TestSource(() => now, onlyFirst: true);
        var fallback = new TestSource(() => now);
        var recoveries = 0;
        using var capture = new RecoveringDesktopSceneCapture(new ScreenRect(0, 0, 2, 2),
            () => primary, () => { recoveries++; return fallback; }, () => now);
        Assert.NotNull(capture.TryRead());
        now += Stopwatch.Frequency * 60;
        var resumedAt = now;
        Assert.Null(capture.TryRead(sourceNotBeforeTicks: resumedAt));
        now += Stopwatch.Frequency * 2;
        Assert.Null(capture.TryRead(sourceNotBeforeTicks: resumedAt));
        Assert.False(primary.Disposed);
        Assert.Equal(0, recoveries);
        now += Stopwatch.Frequency * 2;
        Assert.NotNull(capture.TryRead(sourceNotBeforeTicks: resumedAt));
        Assert.Equal(1, recoveries);
    }

    [Fact]
    public void ADocumentedFallbackThatNeverDeliversTheChangedSceneExitsForFeedRetry()
    {
        long now = Stopwatch.Frequency;
        var fallback = new TestSource(() => now, onlyFirst: true);
        var recoveries = 0;
        using var capture = new RecoveringDesktopSceneCapture(new ScreenRect(0, 0, 2, 2),
            () => throw new InvalidOperationException("Native unavailable"),
            () => { recoveries++; return fallback; }, () => now);
        Assert.NotNull(capture.TryRead());
        fallback.SceneChanged = true;
        Assert.True(capture.UpdateExclusions());
        now += Stopwatch.Frequency * 2;
        Assert.Null(capture.TryRead());
        now += Stopwatch.Frequency * 2;
        Assert.Throws<TimeoutException>(() => capture.TryRead());
        Assert.Equal(1, recoveries);
    }

    [Fact]
    public void FirstFallbackFrameDuringNativeRecoveryCompletesItsInitialDeadline()
    {
        long now = Stopwatch.Frequency;
        var primary = new TestSource(() => now, onlyFirst: true);
        var fallback = new TestSource(() => now, onlyFirst: true);
        using var capture = new RecoveringDesktopSceneCapture(new ScreenRect(0, 0, 2, 2),
            () => primary, () => fallback, () => now);
        Assert.NotNull(capture.TryRead());
        now += Stopwatch.Frequency * 4;
        Assert.NotNull(capture.TryRead());
        Assert.True(primary.Disposed);
        now += Stopwatch.Frequency * 60;
        Assert.Null(capture.TryRead());
    }

    [Fact]
    public void StaticDocumentedSceneDoesNotRequireContinuousDuplicateFrames()
    {
        long now = Stopwatch.Frequency;
        var fallback = new TestSource(() => now, onlyFirst: true);
        using var capture = new RecoveringDesktopSceneCapture(new ScreenRect(0, 0, 2, 2),
            () => throw new InvalidOperationException("Native unavailable"), () => fallback, () => now);
        Assert.NotNull(capture.TryRead());
        now += Stopwatch.Frequency * 60;
        Assert.Null(capture.TryRead());
    }

    [Fact]
    public void DocumentedFallbackGetsFreshGraceAfterAHiddenInterval()
    {
        long now = Stopwatch.Frequency;
        var fallback = new TestSource(() => now, onlyFirst: true);
        using var capture = new RecoveringDesktopSceneCapture(new ScreenRect(0, 0, 2, 2),
            () => throw new InvalidOperationException("Native unavailable"), () => fallback, () => now);
        Assert.NotNull(capture.TryRead());
        now += Stopwatch.Frequency * 60;
        var resumedAt = now;
        Assert.Null(capture.TryRead(sourceNotBeforeTicks: resumedAt));
        now += Stopwatch.Frequency * 2;
        Assert.Null(capture.TryRead(sourceNotBeforeTicks: resumedAt));
        now += Stopwatch.Frequency * 2;
        Assert.Throws<TimeoutException>(() => capture.TryRead(sourceNotBeforeTicks: resumedAt));
    }

    [Fact]
    public void AWorkerThatStopsReturningFramesAfterItsFirstFrameRecoversOnce()
    {
        var ctor = typeof(RecoveringDesktopSceneCapture).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance,
            null, [typeof(ScreenRect), typeof(Func<IDesktopSceneCapture>), typeof(Func<IDesktopSceneCapture>), typeof(Func<long>)], null);
        Assert.NotNull(ctor);
        long now = Stopwatch.Frequency;
        var primary = new TestSource(() => now, onlyFirst: true);
        var fallback = new TestSource(() => now);
        var recoveries = 0;
        using var capture = (IDesktopSceneCapture)ctor.Invoke([new ScreenRect(0, 0, 2, 2),
            (Func<IDesktopSceneCapture>)(() => primary),
            (Func<IDesktopSceneCapture>)(() => { recoveries++; return fallback; }), (Func<long>)(() => now)]);
        Assert.NotNull(capture.TryRead());
        now += Stopwatch.Frequency;
        Assert.Null(capture.TryRead());
        now += Stopwatch.Frequency * 3;
        Assert.NotNull(capture.TryRead());
        Assert.True(primary.Disposed);
        Assert.Equal(1, recoveries);
        now += Stopwatch.Frequency * 10;
        Assert.NotNull(capture.TryRead());
        Assert.Equal(1, recoveries);
    }

    private sealed class TestSource(Func<long> clock, bool onlyFirst = false) : IDesktopSceneCapture
    {
        private bool delivered;
        internal bool Disposed;
        internal bool SceneChanged;
        internal string? FailureOperation;
        internal bool FailDispose;
        public nint CarrierHandle => 1;
        public bool UpdateExclusions()
        {
            if (FailureOperation == "update") throw new InvalidOperationException("scene update failed");
            var changed = SceneChanged; SceneChanged = false; return changed;
        }
        public CapturePixels? TryRead(TimeSpan waitForFrame = default, CancellationToken cancellation = default, long sourceNotBeforeTicks = 0, bool reusePixels = false)
        {
            if (onlyFirst && delivered) return null;
            delivered = true;
            return new(2, 2, new byte[16]) { SourceQpcTicks = clock() };
        }
        public void ReleaseReadbackBuffers()
        { if (FailureOperation == "release") throw new InvalidOperationException("readback release failed"); }
        public void Dispose()
        { Disposed = true; if (FailDispose) throw new InvalidOperationException("retirement not confirmed"); }
    }
}
