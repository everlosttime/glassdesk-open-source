using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using GlassDesk.Platform;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock.Tests;

public sealed class BackgroundOnlyCaptureTests
{
    [Fact]
    public void FullLiveCoverageDoesNotRenderOrRetainAStaticWallpaperFrame() => StaTest.Run(() =>
    {
        using var fixture = new WallpaperFixture();
        var wallpaper = fixture.UseStaticWallpaper();
        Assert.Equal(fixture.Expected, fixture.Read().Pixels);
        Assert.Null(wallpaper.Pixels);
    });

    [Fact]
    public void PartialLiveCoverageReconstructsStaticPixelsOutsideTheWallpaper() => StaTest.Run(() =>
    {
        using var fixture = new WallpaperFixture();
        var wallpaper = fixture.UseStaticWallpaper();
        fixture.Read();
        Assert.Null(wallpaper.Pixels);
        fixture.MoveWallpaper(-9984, -10017);
        var frame = fixture.Read();
        for (var y = 0; y < 48; y++) for (var x = 0; x < 64; x++)
        {
            var expected = x < 16 ? new byte[] { 0, 200, 0, 255 }
                : new byte[] { (byte)(20 + x - 16), (byte)(40 + y + 17), 80, 255 };
            Assert.Equal(expected, frame.Pixels.AsSpan((y * 64 + x) * 4, 4).ToArray());
        }
        Assert.NotNull(wallpaper.Pixels);
    });

    [Fact]
    public void RemovedLiveWallpaperRestoresStaticPixelsInsteadOfTheLastLiveFrame() => StaTest.Run(() =>
    {
        using var fixture = new WallpaperFixture();
        fixture.UseStaticWallpaper();
        fixture.Read();
        Set(fixture.Capture, "_liveWallpaper", (nint)0);
        var frame = fixture.Read();
        for (var i = 0; i < frame.Pixels.Length; i += 4)
            Assert.Equal(new byte[] { 0, 200, 0, 255 }, frame.Pixels.AsSpan(i, 4).ToArray());
    });

    [Fact]
    public void BackgroundOnlySceneDoesNotAllocateCarrierReadbackBeforeFirstRequest() => StaTest.Run(() =>
    {
        using var fixture = new WallpaperFixture();
        Assert.Null(typeof(DocumentedDesktopSceneCapture).GetField("_capture", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Capture));
        Assert.Equal(fixture.Expected, fixture.Read().Pixels);
    });

    [Fact]
    public void CurrentWallpaperWithoutForeignCoverageSkipsCarrierReadAndKeepsImmutableFrames() => StaTest.Run(() =>
    {
        using var fixture = new WallpaperFixture();
        var first = fixture.Read();
        var saved = first.Pixels.ToArray();
        Assert.Equal(fixture.Expected, first.Pixels);
        Assert.DoesNotContain(fixture.Traces, t => t.Stage == "PrintWindow" && t.Reason == "Delivered");
        Assert.Null(typeof(DocumentedDesktopSceneCapture).GetField("_capture", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Capture));
        fixture.Paint(33);
        var barrier = Stopwatch.GetTimestamp();
        var second = fixture.Read(barrier);
        Assert.Equal(fixture.Expected, second.Pixels);
        Assert.Equal(saved, first.Pixels);
        Assert.True(second.SourceQpcTicks >= barrier);
        Assert.NotEqual(saved, second.Pixels);
    });

    [Fact]
    public void CancelledOrFutureBarrierRequestCannotPublishWallpaperAsFresh() => StaTest.Run(() =>
    {
        using var fixture = new WallpaperFixture();
        Assert.Null(fixture.Capture.TryRead(cancellation: new CancellationToken(true)));
        Assert.Null(fixture.Capture.TryRead(sourceNotBeforeTicks: Stopwatch.GetTimestamp() + Stopwatch.Frequency * 60));
    });

    [Fact]
    public void ForegroundCoverageKeepsTheCompositeReadPath() => StaTest.Run(() =>
    {
        using var fixture = new WallpaperFixture();
        fixture.Read();
        fixture.Paint(33);
        Set(fixture.Capture, "_backgroundComposer", new DesktopBackgroundComposer(64, 48, [new(0, 0, 64, 48)]));
        Assert.Equal(fixture.Expected, fixture.Read().Pixels);
        Assert.Contains(fixture.Traces, t => t.Stage == "PrintWindow" && t.Reason == "Delivered");
    });

    [Fact]
    public void DirectBackgroundReleasesCarrierBitmapAndRestoresItForForeignCoverage() => StaTest.Run(() =>
    {
        using var fixture = new WallpaperFixture();
        var first = fixture.Read();
        Assert.Equal(fixture.Expected, first.Pixels);
        Assert.Equal(0, SendMessageW(fixture.Capture.CarrierHandle, 0x0173, 0, 0));
        fixture.Paint(33);
        Set(fixture.Capture, "_backgroundComposer", new DesktopBackgroundComposer(64, 48, [new(0, 0, 64, 48)]));
        Assert.Equal(fixture.Expected, fixture.Read().Pixels);
        Assert.NotEqual(0, SendMessageW(fixture.Capture.CarrierHandle, 0x0173, 0, 0));
        Set(fixture.Capture, "_backgroundComposer", new DesktopBackgroundComposer(64, 48, []));
        Assert.Equal(fixture.Expected, fixture.Read().Pixels);
        Assert.Equal(0, SendMessageW(fixture.Capture.CarrierHandle, 0x0173, 0, 0));
        Assert.Equal((byte)33, first.Pixels[0]); // Original immutable frame remains unchanged.
    });

    [Fact]
    public void StaticBackgroundRestoresReleasedCarrierBeforeCompositeRead() => StaTest.Run(() =>
    {
        using var fixture = new WallpaperFixture();
        fixture.UseStaticWallpaper();
        fixture.Read();
        Set(fixture.Capture, "_liveWallpaper", (nint)0);
        Set(fixture.Capture, "_backgroundComposer", new DesktopBackgroundComposer(64, 48, [new(0, 0, 64, 48)]));
        var frame = fixture.Read();
        for (var i = 0; i < frame.Pixels.Length; i += 4)
            Assert.Equal(new byte[] { 0, 200, 0, 255 }, frame.Pixels.AsSpan(i, 4).ToArray());
        Assert.NotEqual(0, SendMessageW(fixture.Capture.CarrierHandle, 0x0173, 0, 0));
    });

    private static void Set(object owner, string name, object value) => owner.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, value);

    private sealed class WallpaperFixture : IDisposable
    {
        private readonly nint _window;
        private readonly NativeWallpaperSurface _wallpaper;
        internal DocumentedDesktopSceneCapture Capture { get; }
        internal List<CaptureTrace> Traces { get; } = [];
        internal byte[] Expected { get; private set; } = [];
        internal WallpaperFixture()
        {
            SetThreadDpiAwarenessContext(-4);
            _window = CreateWindowExW(0, "STATIC", "GlassDesk background-only fixture", 0x8000000E,
                -10013, -10017, 96, 80, 0, 0, 0, 0);
            Assert.NotEqual(0, _window);
            _wallpaper = new NativeWallpaperSurface(_window, 96, 80);
            Paint(0); ShowWindow(_window, 4);
            Capture = new DocumentedDesktopSceneCapture(new(-10000, -10000, 64, 48), false, Traces.Add,
                null, GetShellWindow, 26100, desktopOnly: true);
            Set(Capture, "_liveWallpaper", _window);
            Set(Capture, "_nextWallpaperScanTicks", long.MaxValue);
            Traces.Clear();
        }
        internal void Paint(int shift)
        {
            var pixels = new byte[96 * 80 * 4];
            for (var y = 0; y < 80; y++) for (var x = 0; x < 96; x++)
            {
                var i = (y * 96 + x) * 4;
                pixels[i] = (byte)(20 + x + shift); pixels[i + 1] = (byte)(40 + y);
                pixels[i + 2] = (byte)(80 + shift); pixels[i + 3] = 255;
            }
            _wallpaper.Update(pixels);
            Expected = new byte[64 * 48 * 4];
            for (var y = 0; y < 48; y++) Array.Copy(pixels, ((y + 17) * 96 + 13) * 4, Expected, y * 64 * 4, 64 * 4);
        }
        internal CapturePixels Read(long barrier = 0) => Assert.IsType<CapturePixels>(Capture.TryRead(sourceNotBeforeTicks: barrier));
        internal WallpaperBackdrop UseStaticWallpaper()
        {
            var wallpaper = new WallpaperBackdrop(readDesktop: () => new DesktopWallpaperSnapshot(
                [new WallpaperMonitor("", new(-10000, -10000, 64, 48))], DesktopWallpaperPosition.Fill, 0x00C800));
            Set(Capture, "_wallpaper", wallpaper);
            return wallpaper;
        }
        internal void MoveWallpaper(int x, int y) => Assert.True(SetWindowPos(_window, 0, x, y, 0, 0, 0x15));
        public void Dispose() { Capture.Dispose(); _wallpaper.Dispose(); DestroyWindow(_window); }
    }
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowExW(uint ex, string cls, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern nint SendMessageW(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
