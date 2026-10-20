using System.Windows;
using GlassDesk.Platform.Displays;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock.Tests;

public sealed class ContainerMorphMotionTests
{
    private static readonly PixelRect Source = new(520, 920, 64, 64);
    private static readonly PixelRect Destination = new(160, 120, 1100, 720);

    [Fact]
    public void SnapUsesExactBoundsAndClampedRadiusAtEachEndpoint()
    {
        var motion = Create();
        Assert.Equal(new Rect(520, 920, 64, 64), motion.CurrentFrame.Bounds);
        Assert.Equal(24, motion.CurrentFrame.CornerRadius);
        Assert.True(motion.IsSettled);
        motion.Retarget(true);
        motion.SnapToTarget();
        Assert.Equal(new Rect(160, 120, 1100, 720), motion.CurrentFrame.Bounds);
        Assert.Equal(80, motion.CurrentFrame.CornerRadius);
        Assert.Equal(1, motion.CurrentFrame.ContentOpacity);
        Assert.Equal(1, motion.Openness);
        motion.Retarget(false);
        motion.SnapToTarget();
        Assert.Equal(new Rect(520, 920, 64, 64), motion.CurrentFrame.Bounds);
        Assert.Equal(0, motion.CurrentFrame.ContentOpacity);
        Assert.Equal(1, motion.CurrentFrame.GlyphOpacity);
    }

    [Fact]
    public void OpeningStretchesUpBeforeWideningAndHidesContentUntilTheShellIsLargeEnough()
    {
        var motion = Create();
        motion.Retarget(true);
        motion.Advance(.012);
        Assert.Equal(64, motion.CurrentFrame.Bounds.Width, 8);
        Assert.True(motion.CurrentFrame.Bounds.Height > 64);
        Assert.True(motion.CurrentFrame.Bounds.Top < 920);
        for (var i = 0; i < 100 && motion.Openness < .65; i++)
        {
            Assert.Equal(0, motion.CurrentFrame.ContentOpacity);
            motion.Advance(1.0 / 240);
        }
        Assert.InRange(motion.Openness, .65, 1);
        motion.Advance(.1);
        Assert.Equal(1, motion.CurrentFrame.ContentOpacity);
        Assert.Equal(1, motion.CurrentFrame.ContentScale);
    }

    [Fact]
    public void OpeningHasSmallSpringOvershootAndSettlesWithinFourHundredTwentyMilliseconds()
    {
        var motion = Create();
        motion.Retarget(true);
        var widest = 0d;
        for (var i = 0; i < 101; i++)
        {
            motion.Advance(1.0 / 240);
            var frame = motion.CurrentFrame;
            widest = Math.Max(widest, frame.Bounds.Width);
            Assert.InRange(frame.CornerRadius, 0, Math.Min(frame.Bounds.Width, frame.Bounds.Height) / 2);
            Assert.InRange(frame.GlassStrength, 0, 1);
            Assert.InRange(frame.GlyphOpacity, 0, 1);
        }
        Assert.InRange(widest, 1118, 1135);
        Assert.True(motion.IsSettled);
        Assert.Equal(new Rect(160, 120, 1100, 720), motion.CurrentFrame.Bounds);
    }

    [Fact]
    public void ClosingFadesContentBeforeShrinkingAndSettlesWithinThreeHundredTwentyMilliseconds()
    {
        var motion = Create();
        motion.Retarget(true);
        motion.SnapToTarget();
        motion.Retarget(false);
        motion.Advance(.04);
        Assert.InRange(motion.CurrentFrame.ContentOpacity, 0, .99);
        Assert.Equal(new Rect(160, 120, 1100, 720), motion.CurrentFrame.Bounds);
        motion.Advance(.03);
        Assert.Equal(0, motion.CurrentFrame.ContentOpacity);
        Assert.Equal(new Rect(160, 120, 1100, 720), motion.CurrentFrame.Bounds);
        motion.Advance(.01);
        Assert.True(motion.CurrentFrame.Bounds.Width < 1100);
        motion.Advance(.24);
        Assert.True(motion.IsSettled);
        Assert.Equal(new Rect(520, 920, 64, 64), motion.CurrentFrame.Bounds);
    }

    [Fact]
    public void ReversingPreservesTheCurrentGeometryAndInertia()
    {
        var motion = Create();
        motion.Retarget(true);
        motion.Advance(.04);
        var previous = motion.CurrentFrame.Bounds;
        motion.Advance(.0001);
        var before = motion.CurrentFrame;
        var precedingVelocity = (before.Bounds.Width - previous.Width) / .0001;
        motion.Retarget(false);
        Assert.Equal(before, motion.CurrentFrame);
        motion.Advance(.0001);
        var nextVelocity = (motion.CurrentFrame.Bounds.Width - before.Bounds.Width) / .0001;
        Assert.True(precedingVelocity > 0);
        Assert.InRange(nextVelocity / precedingVelocity, .97, 1.03);
        motion.Advance(.035);
        var closing = motion.CurrentFrame;
        motion.Retarget(true);
        Assert.Equal(closing, motion.CurrentFrame);
        motion.Advance(.5);
        Assert.True(motion.IsSettled);
        Assert.Equal(1, motion.CurrentFrame.ContentOpacity);
    }

    [Fact]
    public void AChangedDockAnchorMovesTheClosingTargetWithoutJumpingTheCurrentShell()
    {
        var motion = Create();
        motion.Retarget(true);
        motion.Advance(.12);
        var before = motion.CurrentFrame;
        motion.Retarget(false, new PixelRect(1200, 900, 64, 64));
        Assert.Equal(before, motion.CurrentFrame);
        motion.Advance(.5);
        Assert.Equal(new Rect(1200, 900, 64, 64), motion.CurrentFrame.Bounds);
        Assert.True(motion.IsSettled);
    }

    [Fact]
    public void UpdatingTheDockSizeDuringAnExistingTransitionDoesNotJumpMaterialOrRestartTheFade()
    {
        var motion = Create();
        motion.Retarget(true);
        motion.Advance(.12);
        var before = motion.CurrentFrame;
        motion.Retarget(true, new PixelRect(520, 920, 128, 128));
        Assert.Equal(before, motion.CurrentFrame);
        motion.Advance(.1);
        Assert.Equal(1, motion.CurrentFrame.ContentOpacity);
        motion.Advance(.3);
        motion.Retarget(false);
        motion.Advance(.03);
        motion.Retarget(false, new PixelRect(700, 900, 128, 128));
        motion.Advance(.04);
        Assert.Equal(0, motion.CurrentFrame.ContentOpacity);
        motion.Advance(.4);
        Assert.Equal(new Rect(700, 900, 128, 128), motion.CurrentFrame.Bounds);
        Assert.Equal(48, motion.CurrentFrame.CornerRadius);
    }

    [Fact]
    public void AButtonSharingThePanelLeftAndTopStillSettlesWithinTheOpeningDuration()
    {
        var motion = new ContainerMorphMotion(new PixelRect(160, 120, 64, 64), Destination, 24, 80);
        motion.Retarget(true);
        motion.Advance(.42);
        Assert.True(motion.IsSettled);
        Assert.Equal(new Rect(160, 120, 1100, 720), motion.CurrentFrame.Bounds);
    }

    [Fact]
    public void GeometryDependsOnElapsedTimeRatherThanDisplayRefreshRate()
    {
        var low = SampleAt(.12, 30);
        var medium = SampleAt(.12, 60);
        var high = SampleAt(.12, 144);
        Assert.Equal(low.Bounds.X, medium.Bounds.X, 8);
        Assert.Equal(low.Bounds.Y, high.Bounds.Y, 8);
        Assert.Equal(low.Bounds.Width, high.Bounds.Width, 8);
        Assert.Equal(low.Bounds.Height, medium.Bounds.Height, 8);
        Assert.Equal(low.CornerRadius, high.CornerRadius, 8);
        Assert.Equal(low.ContentOpacity, high.ContentOpacity, 4);
    }

    [Fact]
    public void RepeatedRenderSamplesDoNotAllocateManagedMemory()
    {
        var motion = Create();
        for (var i = 0; i < 50; i++)
        {
            motion.Retarget(i % 2 == 0);
            motion.Advance(.004);
            _ = motion.CurrentFrame;
        }
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            motion.Retarget(i % 2 == 0);
            motion.Advance(.004);
            _ = motion.CurrentFrame;
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static ContainerMorphMotion Create() => new(Source, Destination, 24, 80);

    private static ContainerMorphFrame SampleAt(double elapsed, double refreshRate)
    {
        var motion = Create();
        motion.Retarget(true);
        while (elapsed > 1e-12)
        {
            var delta = Math.Min(elapsed, 1 / refreshRate);
            motion.Advance(delta);
            elapsed -= delta;
        }
        return motion.CurrentFrame;
    }
}
