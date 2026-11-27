using System.Windows;
using GlassDesk.App;

namespace GlassDesk.AppServices.Tests;

public sealed class StageMotionTests
{
    private static readonly Rect Rail = new(16, 320, 126, 70);
    private static readonly Rect Stage = new(264, 110, 1090, 770);

    [Fact]
    public void AStalledFrameCannotJumpStraightToTheEndpoint()
    {
        var motion = new StageMotion(Rail, onStage: false);
        motion.Retarget(Stage, opening: true);

        motion.Advance(.30);

        Assert.NotEqual(Stage, motion.Pose.Bounds);
        Assert.False(motion.IsSettled);
        Assert.InRange(motion.Pose.Bounds.X, Rail.X, Stage.X);
    }

    [Fact]
    public void ChangingDestinationPreservesTheDisplayedPoseAndMomentum()
    {
        var motion = new StageMotion(Rail, onStage: false);
        motion.Retarget(Stage, opening: true);
        for (var frame = 0; frame < 5; frame++) motion.Advance(1.0 / 120);
        var before = motion.Pose;

        motion.Retarget(Rail, opening: false);
        Assert.Equal(before, motion.Pose);
        motion.Advance(1.0 / 240);

        Assert.True(motion.Pose.Bounds.X > before.Bounds.X);
    }

    [Fact]
    public void InterruptingTheHandoffKeepsTheCurrentOpacity()
    {
        var motion = new StageMotion(Stage, onStage: true);
        motion.BeginHandoff();
        motion.Advance(1.0 / 120);
        var before = motion.Pose.HandoffOpacity;

        motion.Retarget(Rail, opening: false);

        Assert.Equal(before, motion.Pose.HandoffOpacity);
        for (var frame = 0; frame < 30; frame++) motion.Advance(1.0 / 120);
        Assert.InRange(motion.Pose.HandoffOpacity, .99, 1);
    }

    [Theory]
    [InlineData(.85, 1)]
    [InlineData(.85, .66)]
    [InlineData(.71, .35)]
    [InlineData(.57, 0)]
    public void ClosingHandoffKeepsTheCompositedPreviewOpacity(double target, double fraction)
    {
        var movingOpacity = target * fraction;
        var cardOpacity = target * StageMotion.RailPreviewWeight(target, movingOpacity);

        Assert.Equal(target, movingOpacity + (1 - movingOpacity) * cardOpacity, precision: 12);
    }

    [Fact]
    public void HandoffBeginsNearTheDestinationAndFadesOnlyAfterNativeReadiness()
    {
        var motion = new StageMotion(Rail, onStage: false);
        motion.Retarget(Stage, opening: true);
        Assert.False(motion.CanRestoreNativeWindow);

        for (var frame = 0; frame < 60 && !motion.CanRestoreNativeWindow; frame++) motion.Advance(1.0 / 120);
        Assert.True(motion.CanRestoreNativeWindow);
        Assert.False(motion.IsSettled);
        Assert.Equal(1, motion.Pose.HandoffOpacity);

        motion.BeginHandoff();
        Assert.Equal(1, motion.Pose.HandoffOpacity);
        motion.Advance(1.0 / 120);
        Assert.InRange(motion.Pose.HandoffOpacity, .3, .999);
        for (var frame = 0; frame < 20; frame++) motion.Advance(1.0 / 120);
        Assert.True(motion.HandoffComplete);
    }
}
