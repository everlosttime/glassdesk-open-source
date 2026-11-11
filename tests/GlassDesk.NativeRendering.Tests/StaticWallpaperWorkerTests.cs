using System.Windows;
using GlassDesk.UI.Controls;

namespace GlassDesk.NativeRendering.Tests;

public sealed class StaticWallpaperWorkerTests
{
    [Fact]
    public void SamplerReusesUnchangedWallpaperAndDoesNotServeAnotherGeometry()
    {
        using var worker = new StaticWallpaperWorker(new WallpaperBackdrop(readDesktop: () => new(
            [new("", new(0, 0, 1000, 1000))], GlassDesk.Platform.DesktopWallpaperPosition.Fill, 0x00332211)));
        try
        {
            var region = new Rect(0, 0, 128, 64);
            worker.Request(region, 64, 32);
            var firstDeadline = System.Diagnostics.Stopwatch.StartNew();
            while (worker.Read(region,64,32) is null && firstDeadline.Elapsed.TotalSeconds < 3) Thread.Sleep(10);
            object? first = worker.Read(region, 64, 32);
            Assert.NotNull(first);
            worker.Request(region, 64, 32);
            Assert.Same(first, (object?)worker.Read(region, 64, 32));
            Assert.Null((object?)worker.Read(new Rect(1, 0, 128, 64), 64, 32));
            worker.Request(region, 32, 16);
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (worker.Read(region,32,16) is null && deadline.Elapsed.TotalSeconds < 3) Thread.Sleep(10);
            var resized = worker.Read(region,32,16);
            Assert.NotNull(resized);
            Assert.Equal(32*16*4, resized.Pixels!.Length);
            Assert.Equal(new byte[] { 0x33,0x22,0x11,255 }, resized.Pixels.Take(4));
        }
        finally { ((IDisposable)worker).Dispose(); }
    }
}
