using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;

namespace GlassDesk.NativeRendering.Tests;

public sealed class ManagedBridgeTests
{
    [Fact]
    public void StaticFrameIsReusedWithoutResubmissionOrReadback() => NativeEngineTests.OnSta(() =>
    {
        var window = new Window();
        using var bridge = new GlassDesk.Rendering.NativeD3DImageBridge(new WindowInteropHelper(window).EnsureHandle(),64,32);
        var pixels = Enumerable.Repeat((byte)255,64*32*4).ToArray();
        bridge.UploadWallpaper(pixels,64,32);
        bridge.TryRenderMaterial(new(64,32,1,.5f,.16f,false,true));
        var watch = Stopwatch.StartNew();
        while (bridge.Image.PixelWidth == 0 && watch.Elapsed.TotalSeconds < 3) { bridge.TryPresent(); Thread.Sleep(1); }
        Assert.Equal(64,bridge.Image.PixelWidth);
        for (var i=0;i<100;i++) bridge.TryPresent();
        Assert.Equal(1ul,bridge.Statistics.SubmittedFrames);
        Assert.Equal(0ul,bridge.Statistics.CpuReadbacks);
        Assert.Equal(1ul,bridge.WallpaperUploads);
        Assert.Equal(1u,bridge.Statistics.LeasedSlots);
        bridge.Dispose(); window.Close();
    });

    [Fact]
    public void TimedD3DImageLockMustBeBalancedEvenWhenItReturnsFalse() => NativeEngineTests.OnSta(() =>
    {
        var image = new D3DImage();
        image.Lock();
        Assert.False(image.TryLock(new Duration(TimeSpan.Zero)));
        image.Unlock();
        image.Unlock();
        Assert.Throws<InvalidOperationException>(() => image.Unlock());
    });

    private static Type Type(string name)
    {
        var type = Assembly.Load("GlassDesk.Rendering").GetType("GlassDesk.Rendering." + name);
        Assert.NotNull(type);
        return type;
    }

    [Fact]
    public void UncleanNativeSessionIsNotSilentlyReenabledOnNextStartup()
    {
        var root = Path.Combine(Path.GetTempPath(), "GlassDesk-native-guard-" + Guid.NewGuid().ToString("N"));
        try
        {
            var begin = Type("NativeSessionRecovery").GetMethod("TryBegin")!;
            using (var first = (IDisposable?)begin.Invoke(null, [root]))
            {
                Assert.NotNull(first);
                Assert.Null(begin.Invoke(null, [root]));
            }
            using (var clean = (IDisposable?)begin.Invoke(null, [root])) Assert.NotNull(clean);
            File.WriteAllText(Path.Combine(root, "native-session-active.json"), "{\"unclean\":true}");
            Assert.Null(begin.Invoke(null, [root]));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void BridgeReleasesDisplayedLeaseBeforeResizeAndOnlyRecreatesDeviceOnce() => NativeEngineTests.OnSta(() =>
    {
        var window = new Window();
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        dynamic bridge = Activator.CreateInstance(Type("NativeD3DImageBridge"), hwnd, 64, 32)!;
        try
        {
            var image = (D3DImage)bridge.Image;
            var watch = Stopwatch.StartNew();
            while (image.PixelWidth == 0 && watch.Elapsed < TimeSpan.FromSeconds(3))
            {
                bridge.TryRender(0d, false);
                Thread.Sleep(1);
            }
            Assert.Equal(64, image.PixelWidth);
            Assert.Equal(1u, (uint)bridge.Statistics.LeasedSlots);
            bridge.Resize(96, 48);
            Assert.Equal(0, image.PixelWidth);
            Assert.Equal(0u, (uint)bridge.Statistics.LeasedSlots);
            Assert.Equal(96u, (uint)bridge.Statistics.Width);
            var generation = (ulong)bridge.Statistics.Generation;
            Assert.True((bool)bridge.TryRecover());
            Assert.True((ulong)bridge.Statistics.Generation > generation);
            Assert.False((bool)bridge.TryRecover());
            Assert.True((bool)bridge.IsFallbackRequired);
            Assert.Equal(0, image.PixelWidth);
        }
        finally { ((IDisposable)bridge).Dispose(); window.Close(); }
    });

    [Fact]
    public void SuspendedBridgeDoesNotSubmitFramesAndDisposeDetachesTheSurface() => NativeEngineTests.OnSta(() =>
    {
        var window = new Window();
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        dynamic bridge = Activator.CreateInstance(Type("NativeD3DImageBridge"), hwnd, 64, 32)!;
        try
        {
            var image = (D3DImage)bridge.Image;
            bridge.Suspend();
            var submitted = (ulong)bridge.Statistics.SubmittedFrames;
            for (var i = 0; i < 10; i++) Assert.False((bool)bridge.TryRender(i / 60d, true));
            Assert.Equal(submitted, (ulong)bridge.Statistics.SubmittedFrames);
            bridge.Resume();
            bridge.TryRender(1d, true);
            Assert.True((ulong)bridge.Statistics.SubmittedFrames > submitted);
            ((IDisposable)bridge).Dispose();
            Assert.Equal(0, image.PixelWidth);
        }
        finally { ((IDisposable)bridge).Dispose(); window.Close(); }
    });
}
