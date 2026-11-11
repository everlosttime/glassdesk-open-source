using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using GlassDesk.Platform;
using GlassDesk.UI.CaptureCompatibility;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock.Tests;

public sealed class Windows10DesktopBackgroundTests
{
    [Theory]
    [InlineData("cef-alpha-zero")]
    [InlineData("cef-per-pixel")]
    [InlineData("cef-colorkey")]
    [InlineData("cef-screen-helper")]
    [InlineData("other-alpha-zero")]
    public void FullRegionTransparentHelperCannotReserveOpaqueCoverageOverWallpaper(string mode)
    {
        using var fixture = new ForeignWindows();
        fixture.Send(mode);
        StaTest.Run(() =>
        {
            SetThreadDpiAwarenessContext(-4);
            using var capture = CreateWindows10Scene(fixture, true);
            SetWallpaper(capture, 0x00C800);
            capture.UpdateExclusions();
            using var missingBackground = new NativeWallpaperSurface(capture.CarrierHandle, 360, 240);
            missingBackground.Update(new byte[360 * 240 * 4]);
            ReadPixels(capture, new byte[] { 0, 200, 0, 255 }, new byte[] { 255, 0, 0, 255 });
            var members = (Array)typeof(DocumentedDesktopSceneCapture).GetField("_members", Private)!.GetValue(capture)!;
            Assert.Equal(2, members.Length);
        });
    }

    [Fact]
    public void ZeroOpacityWindowCanBecomeOpaqueAndTransparentWithoutMoving()
    {
        using var fixture = new ForeignWindows();
        fixture.Send("cef-alpha-zero");
        StaTest.Run(() =>
        {
            SetThreadDpiAwarenessContext(-4);
            using var capture = CreateWindows10Scene(fixture, true);
            SetWallpaper(capture, 0x00C800);
            ReadPixels(capture, new byte[] { 0, 200, 0, 255 }, new byte[] { 255, 0, 0, 255 });
            fixture.Send("overlay-opaque");
            ReadPixels(capture, new byte[] { 0, 0, 0, 255 }, new byte[] { 255, 0, 0, 255 });
            fixture.Send("overlay-transparent");
            ReadPixels(capture, new byte[] { 0, 200, 0, 255 }, new byte[] { 255, 0, 0, 255 });
        });
    }

    [Fact]
    public void OpaqueCefSoftwareWithTheSameClassIsStillCaptured()
    {
        using var fixture = new ForeignWindows();
        fixture.Send("cef-opaque");
        StaTest.Run(() =>
        {
            SetThreadDpiAwarenessContext(-4);
            using var capture = CreateWindows10Scene(fixture, true);
            SetWallpaper(capture, 0x00C800);
            ReadPixels(capture, new byte[] { 0, 0, 0, 255 }, new byte[] { 255, 0, 0, 255 });
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeletedOriginalUsesCachedWallpaperInTheFinalSceneWithWorkingSoftwareWindows(bool foreignWindows)
    {
        using var fixture = new ForeignWindows();
        StaTest.Run(() =>
        {
            SetThreadDpiAwarenessContext(-4);
            var root = Path.Combine(Path.GetTempPath(), "GlassDesk-cache-scene-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var original = Path.Combine(root, "deleted.png");
            var cache = Path.Combine(root, "TranscodedWallpaper");
            var pixels = Enumerable.Repeat((byte)255, 16 * 16 * 4).ToArray();
            var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(16, 16, 96, 96,
                System.Windows.Media.PixelFormats.Bgra32, null, pixels, 16 * 4);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using (var output = File.Create(cache)) encoder.Save(output);
            var pathBytes = System.Text.Encoding.Unicode.GetBytes(original + '\0');
            var identity = new byte[24 + pathBytes.Length];
            pathBytes.CopyTo(identity, 24);
            var resolver = new WindowsWallpaperCache(cache, () => (identity, original));
            using var capture = CreateWindows10Scene(fixture, foreignWindows);
            var wallpaper = new WallpaperBackdrop(resolver.Resolve, () => new DesktopWallpaperSnapshot(
                new[] { new WallpaperMonitor(original, new(260, 160, 360, 240)) }, DesktopWallpaperPosition.Fill, 0));
            SetField(capture, "_wallpaper", wallpaper);
            ReadPixels(capture, new byte[] { 255, 255, 255, 255 },
                foreignWindows ? new byte[] { 255, 0, 0, 255 } : new byte[] { 255, 255, 255, 255 });
            using var missingBackground = new NativeWallpaperSurface(capture.CarrierHandle, 360, 240);
            missingBackground.Update(new byte[360 * 240 * 4]);
            ReadPixels(capture, new byte[] { 255, 255, 255, 255 },
                foreignWindows ? new byte[] { 255, 0, 0, 255 } : new byte[] { 255, 255, 255, 255 });
            Assert.Equal("DecodedWindowsCache", wallpaper.SourceKind);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingCarrierBackgroundDoesNotReplaceTheDesktopWithBlack(bool foreignWindows)
    {
        using var fixture = new ForeignWindows();
        if (foreignWindows) fixture.Send("black");
        StaTest.Run(() =>
        {
            SetThreadDpiAwarenessContext(-4);
            using var capture = CreateWindows10Scene(fixture, foreignWindows);
            SetWallpaper(capture, 0x00C800);
            ReadPixels(capture, new byte[] { 0, 200, 0, 255 },
                foreignWindows ? new byte[] { 255, 0, 0, 255 } : new byte[] { 0, 200, 0, 255 });
            using var missingBackground = new NativeWallpaperSurface(capture.CarrierHandle, 360, 240);
            missingBackground.Update(new byte[360 * 240 * 4]);
            var frame = ReadPixels(capture, new byte[] { 0, 200, 0, 255 },
                foreignWindows ? new byte[] { 255, 0, 0, 255 } : new byte[] { 0, 200, 0, 255 });
            if (foreignWindows)
                Assert.Equal(new byte[] { 0, 0, 0, 255 }, frame.Pixels.AsSpan((60 * 360 + 60) * 4, 4).ToArray());
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrintWindowSceneContainsTheWallpaperBehindForeignWindows(bool foreignWindows)
    {
        using var fixture = new ForeignWindows();
        StaTest.Run(() =>
        {
            SetThreadDpiAwarenessContext(-4);
            using var capture = CreateWindows10Scene(fixture, foreignWindows);
            SetWallpaper(capture, 0x00C800);
            ReadPixels(capture, new byte[] { 0, 200, 0, 255 },
                foreignWindows ? new byte[] { 255, 0, 0, 255 } : new byte[] { 0, 200, 0, 255 });
            SetWallpaper(capture, 0xC80000);
            ReadPixels(capture, new byte[] { 200, 0, 0, 255 },
                foreignWindows ? new byte[] { 255, 0, 0, 255 } : new byte[] { 200, 0, 0, 255 });
        });
    }

    [Fact]
    public void LiveWallpaperUpdatesTheNativeSceneWithoutReplacingUncoveredDesktopPixels()
    {
        using var fixture = new ForeignWindows();
        StaTest.Run(() =>
        {
            SetThreadDpiAwarenessContext(-4);
            using var capture = CreateWindows10Scene(fixture, false);
            SetWallpaper(capture, 0x00C800);
            SetField(capture, "_liveWallpaper", fixture.BottomHandle);
            SetField(capture, "_nextWallpaperScanTicks", long.MaxValue);
            ReadPixels(capture, new byte[] { 0, 200, 0, 255 }, new byte[] { 0, 255, 0, 255 });
            fixture.Send("red");
            ReadPixels(capture, new byte[] { 0, 200, 0, 255 }, new byte[] { 0, 0, 255, 255 });
        });
    }

    [Theory]
    [InlineData(19045)]
    [InlineData(26100)]
    public void FailedLiveReadbackDoesNotPublishStaticFallbackAsFreshWallpaper(int windowsBuild)
    {
        using var fixture = new ForeignWindows();
        StaTest.Run(() =>
        {
            SetThreadDpiAwarenessContext(-4);
            using var capture = CreateWindows10Scene(fixture, false, windowsBuild);
            SetWallpaper(capture, 0);
            SetField(capture, "_liveWallpaper", fixture.BottomHandle);
            SetField(capture, "_nextWallpaperScanTicks", long.MaxValue);
            SetField(capture, "_liveWidth", 240);
            SetField(capture, "_liveHeight", 160);
            SetField(capture, "_livePixels", new byte[360 * 240 * 4]);
            var attempts = 0;
            SetField(capture, "_liveSurface", new LiveWallpaperSurface(240, 160, 360, 240,
                (_, _) => { attempts++; return false; }, null));
            var frame = capture.TryRead(TimeSpan.FromMilliseconds(16));
            Assert.Equal(1, attempts);
            Assert.Null(frame);
        });
    }

    [Fact]
    public void CroppedLiveWallpaperPreservesTheSuppliedDesktopOutsideItsWindow()
    {
        using var fixture = new ForeignWindows();
        using var surface = new LiveWallpaperSurface(240, 160, 360, 240);
        var pixels = new byte[360 * 240 * 4];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index + 1] = 200;
            pixels[index + 3] = 255;
        }
        Assert.True(surface.TryDraw(fixture.BottomHandle, 260, 160, 360, 240, 300, 200, 540, 360, pixels));
        Assert.Equal(new byte[] { 0, 200, 0, 255 }, pixels[..4]);
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, pixels.AsSpan((120 * 360 + 180) * 4, 4).ToArray());
    }

    private static DocumentedDesktopSceneCapture CreateWindows10Scene(ForeignWindows fixture, bool foreignWindows, int windowsBuild = 19045)
    {
        var constructor = typeof(DocumentedDesktopSceneCapture).GetConstructor(Private, null,
            new[] { typeof(ScreenRect), typeof(bool), typeof(Action<CaptureTrace>), typeof(Func<int[]>),
                typeof(Func<nint>), typeof(int) }, null);
        Assert.NotNull(constructor);
        Func<int[]> excluded = () => Process.GetProcesses().Select(process =>
        {
            using (process) return process.Id;
        }).Where(id => !foreignWindows || id != fixture.ProcessId).ToArray();
        return (DocumentedDesktopSceneCapture)constructor.Invoke(new object?[]
            { new ScreenRect(260, 160, 360, 240), false, null, excluded, (Func<nint>)GetShellWindow, windowsBuild });
    }

    private static void SetWallpaper(DocumentedDesktopSceneCapture capture, uint color)
    {
        var wallpaper = (WallpaperBackdrop)typeof(DocumentedDesktopSceneCapture).GetField("_wallpaper", Private)!.GetValue(capture)!;
        SetField(wallpaper, "_refreshAt", DateTime.MaxValue);
        SetField(wallpaper, "_desktop", new DesktopWallpaperSnapshot(
            new[] { new WallpaperMonitor("", new(260, 160, 360, 240)) }, DesktopWallpaperPosition.Fill, color));
        SetField(wallpaper, "_last", default(ValueTuple<Rect, int, int, int, double>));
    }

    private static void SetField(object target, string name, object value)
        => target.GetType().GetField(name, Private)!.SetValue(target, value);

    private static CapturePixels ReadPixels(DocumentedDesktopSceneCapture capture, byte[] background, byte[] foreground)
    {
        CapturePixels? frame = null;
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(3))
        {
            frame = capture.TryRead(TimeSpan.FromMilliseconds(16));
            if (frame is not null && frame.Pixels[..4].SequenceEqual(background)
                && frame.Center.SequenceEqual(foreground)) return frame;
            global::Program.Pump(TimeSpan.FromMilliseconds(10));
        }
        Assert.NotNull(frame);
        Assert.Equal(background, frame.Pixels[..4]);
        Assert.Equal(foreground, frame.Center);
        return frame;
    }

    private sealed class ForeignWindows : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "GlassDesk-wallpaper-" + Guid.NewGuid().ToString("N"));
        private readonly Process _process;
        internal int ProcessId => _process.Id;
        internal nint BottomHandle { get; }

        internal ForeignWindows()
        {
            Directory.CreateDirectory(_root);
            var start = new ProcessStartInfo("D:\\dotnet10\\dotnet.exe")
                { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add(CompatibilityProbePath.File("GlassCaptureProbe.dll"));
            start.ArgumentList.Add("fixture");
            start.ArgumentList.Add(_root);
            _process = Process.Start(start)!;
            Assert.True(SpinWait.SpinUntil(() => File.Exists(Path.Combine(_root, "ready.json"))
                || _process.HasExited, TimeSpan.FromSeconds(10)));
            Assert.False(_process.HasExited);
            using var ready = JsonDocument.Parse(File.ReadAllText(Path.Combine(_root, "ready.json")));
            BottomHandle = (nint)ready.RootElement.GetProperty("bottom").GetInt64();
        }

        internal void Send(string command)
        {
            WriteCommand(command);
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(3))
            {
                try { if (File.ReadAllText(Path.Combine(_root, "ack.txt")) == command) return; }
                catch (IOException) { }
                if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
                    global::Program.Pump(TimeSpan.FromMilliseconds(10));
                else Thread.Sleep(10);
            }
            Assert.Fail($"Fixture command {command} did not finish; exited={_process.HasExited}.");
        }

        private void WriteCommand(string command)
        {
            var watch = Stopwatch.StartNew();
            while (true)
            {
                try
                {
                    File.WriteAllText(Path.Combine(_root, "command.txt"), command);
                    return;
                }
                catch (IOException) when (watch.Elapsed < TimeSpan.FromSeconds(3))
                {
                    if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
                        global::Program.Pump(TimeSpan.FromMilliseconds(10));
                    else Thread.Sleep(10);
                }
            }
        }

        public void Dispose()
        {
            if (!_process.HasExited)
            {
                WriteCommand("exit");
                if (!_process.WaitForExit(3000)) { _process.Kill(entireProcessTree: true); _process.WaitForExit(); }
            }
            _process.Dispose();
        }
    }

    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
}
