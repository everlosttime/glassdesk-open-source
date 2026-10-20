using GlassDesk.Platform.Displays;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock.Tests;

public sealed class ContainerTransformFrameTests
{
    [Fact]
    public void ProgressEndpointsMatchTheDockButtonAndFinalLauncherExactly()
    {
        var source = new PixelRect(180, 240, 48, 64);
        var destination = new PixelRect(300, 120, 1280, 900);

        var opening = ContainerTransformFrame.Create(source, destination, 0, 32, 80);
        var open = ContainerTransformFrame.Create(source, destination, 1, 32, 80);

        Assert.Equal(source, opening.Bounds);
        Assert.Equal(32, opening.CornerRadius);
        Assert.Equal(1, opening.Opacity);
        Assert.Equal(0, opening.GlassStrength);
        Assert.InRange(opening.BlurRadius, 0, 1);
        Assert.Equal(0, opening.ContentOpacity);
        Assert.Equal(destination, open.Bounds);
        Assert.Equal(80, open.CornerRadius);
        Assert.Equal(1, open.Opacity);
        Assert.Equal(1, open.GlassStrength);
        Assert.Equal(0, open.BlurRadius);
        Assert.Equal(1, open.ContentOpacity);
        Assert.Equal(1, open.ContentScale);
    }

    [Fact]
    public void PanelAndContentBeginTogetherWithoutAnEmptyBorderPhase()
    {
        var source = new PixelRect(0, 0, 48, 64);
        var destination = new PixelRect(100, 80, 1000, 800);

        var early = ContainerTransformFrame.Create(source, destination, .25, 32, 80);
        var halfway = ContainerTransformFrame.Create(source, destination, .5, 32, 80);
        var beforeReveal = ContainerTransformFrame.Create(source, destination, .54, 32, 80);
        var revealing = ContainerTransformFrame.Create(source, destination, .82, 32, 80);

        Assert.True(early.Bounds.Width > source.Width + (destination.Width - source.Width) * .5);
        Assert.True(halfway.Bounds.Width > early.Bounds.Width);
        Assert.True(early.ContentOpacity > 0);
        Assert.Equal(1, beforeReveal.ContentOpacity);
        Assert.InRange(revealing.ContentOpacity, 0, 1);
        Assert.InRange(revealing.ContentScale, .92, 1);
        Assert.True(ContainerTransformFrame.Create(source, destination, .6, 32, 80).ContentOpacity > 0);
    }

    [Fact]
    public void RowWavesUseTwentyMillisecondOffsetsAndFinishWithoutBounce()
    {
        var first = ContainerTransformFrame.ContentForRow(.15, 0, 5);
        var second = ContainerTransformFrame.ContentForRow(.15, 1, 5);
        var last = ContainerTransformFrame.ContentForRow(.15, 4, 5);
        Assert.True(first.Opacity > second.Opacity);
        Assert.True(second.Opacity > last.Opacity);
        Assert.Equal(0, last.Opacity);
        Assert.Equal(.92, ContainerTransformFrame.ContentForRow(0, 0, 5).Scale);
        Assert.Equal(first.Opacity, ContainerTransformFrame.ContentForRow(.15 + 20.0 / 420, 1, 5).Opacity, 10);
        for (var row = 0; row < 10; row++)
        {
            Assert.Equal((1d, 1d), ContainerTransformFrame.ContentForRow(1, row, 10));
            Assert.Equal((0d, .92), ContainerTransformFrame.ContentForRow(0, row, 10));
        }
    }

    [Fact]
    public void ProgressOutsideTheAnimationRangeClampsToTheExactEndpoint()
    {
        var source = new PixelRect(4, 8, 48, 64);
        var destination = new PixelRect(80, 100, 800, 600);

        Assert.Equal(source, ContainerTransformFrame.Create(source, destination, -1, 32, 80).Bounds);
        Assert.Equal(destination, ContainerTransformFrame.Create(source, destination, 2, 32, 80).Bounds);
    }
}
