using System.Reflection;
using System.Diagnostics;
using System.IO;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock.Tests;

public sealed class WallpaperCaptureTests
{
    [Fact]
    public void WallpaperHelperProvidesExplicitStaticPixelsWithoutWgc()
    {
        var ctor = typeof(IsolatedDesktopSceneCapture).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance,
            null, [typeof(ScreenRect), typeof(GlassBackdropSource), typeof(Action<CaptureTrace>), typeof(string)], null);
        Assert.NotNull(ctor);
        var exe = CompatibilityProbePath.File("GlassCaptureProbe.exe");
        using var capture = (IDesktopSceneCapture)ctor.Invoke([new ScreenRect(0, 0, 32, 16), GlassBackdropSource.Wallpaper, null, exe]);
        var frame = capture.TryRead(sourceNotBeforeTicks: Stopwatch.GetTimestamp());
        Assert.NotNull(frame); Assert.Equal(32 * 16 * 4, frame.Pixels.Length);
        Assert.True(frame.IsStatic); Assert.Equal(GlassBackdropSource.Wallpaper, frame.Source);
        var firstTime = frame.SourceQpcTicks;
        Assert.False(capture.UpdateExclusions());
        Assert.Null(capture.TryRead());
        // A resume is an actual fresh file/COM resample, never a retimestamped old frame.
        var resumed = capture.TryRead(sourceNotBeforeTicks: Stopwatch.GetTimestamp());
        Assert.NotNull(resumed); Assert.True(resumed.SourceQpcTicks > firstTime);
    }
}
