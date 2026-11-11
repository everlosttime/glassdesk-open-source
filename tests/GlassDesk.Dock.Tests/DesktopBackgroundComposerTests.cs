namespace GlassDesk.Dock.Tests;

public sealed class DesktopBackgroundComposerTests
{
    [Fact]
    public void OverlappingClippedWindowCoveragePreservesRealBlackSoftwarePixels()
    {
        var foreground = new[] { new ScreenRect(4, 1, 9, 3), new ScreenRect(-2, -1, 5, 4), new ScreenRect(2, 2, 4, 1) };
        var composer = new DesktopBackgroundComposer(8, 5, foreground);
        var frame = new byte[8 * 5 * 4];
        var background = Enumerable.Repeat((byte)200, frame.Length).ToArray();
        composer.Apply(frame, background);
        for (var row = 0; row < 5; row++)
        for (var column = 0; column < 8; column++)
        {
            var covered = foreground.Any(bounds => column >= bounds.X && column < bounds.X + bounds.Width
                && row >= bounds.Y && row < bounds.Y + bounds.Height);
            Assert.Equal(covered ? (byte)0 : (byte)200, frame[(row * 8 + column) * 4]);
        }
        Assert.All(background, channel => Assert.Equal((byte)200, channel));
    }

    [Fact]
    public void NoForeignWindowsCopiesTheEntireChangingDesktop()
    {
        var composer = new DesktopBackgroundComposer(5, 3, Array.Empty<ScreenRect>());
        var frame = new byte[5 * 3 * 4];
        var background = Enumerable.Repeat((byte)120, frame.Length).ToArray();
        composer.Apply(frame, background);
        Assert.Equal(background, frame);
        Array.Fill(background, (byte)210);
        composer.Apply(frame, background);
        Assert.Equal(background, frame);
    }

    [Fact]
    public void FullScreenForeignWindowIsNotReplacedByWallpaper()
    {
        var composer = new DesktopBackgroundComposer(5, 3, new[] { new ScreenRect(0, 0, 5, 3) });
        var frame = new byte[5 * 3 * 4];
        composer.Apply(frame, Enumerable.Repeat((byte)255, frame.Length).ToArray());
        Assert.All(frame, channel => Assert.Equal((byte)0, channel));
    }
}
