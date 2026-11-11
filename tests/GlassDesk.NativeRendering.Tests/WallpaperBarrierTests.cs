using System.Diagnostics;
using GlassDesk.UI.Controls;

namespace GlassDesk.NativeRendering.Tests;

public sealed class WallpaperBarrierTests
{
    [Fact]
    public void FreshReadBarrierDoesNotInvalidateAnUnchangedStaticScene() => NativeEngineTests.OnSta(() =>
    {
        using var capture = new WallpaperSceneCapture(new(0,0,64,32), () => new WallpaperBackdrop(readDesktop: () => new(
            [new("",new(0,0,64,32))], GlassDesk.Platform.DesktopWallpaperPosition.Fill,0x00332211)));
        capture.UpdateExclusions();
        var revision = capture.SceneRevision;
        var barrier = Stopwatch.GetTimestamp();
        var fresh = capture.TryRead(sourceNotBeforeTicks: barrier);
        Assert.NotNull(fresh);
        Assert.True(fresh.SourceQpcTicks >= barrier);
        Assert.Equal(revision, capture.SceneRevision);
    });
}
