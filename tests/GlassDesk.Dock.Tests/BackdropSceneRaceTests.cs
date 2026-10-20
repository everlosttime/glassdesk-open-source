using System.Reflection;
using GlassDesk.UI.Controls;
using GlassDesk.Platform.Displays;

namespace GlassDesk.Dock.Tests;

public sealed class BackdropSceneRaceTests
{
    [Fact]
    public void AWindowDisappearingDuringEnumerationDoesNotInvalidateAStableSecondSnapshot()
    {
        var stable = new GlassBackdropScene(new GlassBackdropScene.SceneWindow((nint)123, 42, 10, 20, 80, 90));
        var reads = 0;
        var result = Capture(() => ++reads == 1 ? null : stable);
        Assert.Equal(stable, result);
        Assert.Equal(2, reads);
    }

    [Fact]
    public void PersistentSnapshotFailureStillFailsClosedAfterOneRetry()
    {
        var reads = 0;
        Assert.Null(Capture(() => { reads++; return null; }));
        Assert.Equal(2, reads);
    }

    [Fact]
    public void AValidSnapshotDoesNotRequireASecondEnumeration()
    {
        var reads = 0;
        var stable = new GlassBackdropScene();
        Assert.Same(stable, Capture(() => { reads++; return stable; }));
        Assert.Equal(1, reads);
    }

    private static GlassBackdropScene? Capture(Func<GlassBackdropScene?> read)
    {
        var method = typeof(GlassBackdropScene).GetMethod("CaptureVerifiedSnapshot", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (GlassBackdropScene?)method.Invoke(null, [read]);
    }
}
