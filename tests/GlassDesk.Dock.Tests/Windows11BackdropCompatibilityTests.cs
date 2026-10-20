using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using Xunit.Abstractions;

namespace GlassDesk.Dock.Tests;

public sealed class Windows11BackdropCompatibilityTests(ITestOutputHelper output)
{
    [Fact]
    public void SharedCarrierIsExcludedFromTaskSwitchingWhileLiveCaptureKeepsUpdating()
    {
        if (GlassDesk.Platform.WindowsCapabilities.BuildNumber != 26100) return;
        using var fixture = new Fixture();
        StaTest.Run(() =>
        {
            SetThreadDpiAwarenessContext(-4);
            using var capture = CreateCarrierCapture();
            var handle = capture.CarrierHandle;
            // NOACTIVATE excludes an auxiliary popup from Alt+Tab by default.
            // TOOLWINDOW would make this surface ineligible for WGC capture.
            var style = GetWindowLongPtrW(handle, -20).ToInt64();
            Assert.True((style & 0x08000000L) != 0,
                $"Capture carrier participates in activation/task switching: ExStyle=0x{style:X}.");
            Assert.Equal(0L, style & (0x00040000L | 0x00000080L));
            Assert.NotEqual(nint.Zero, GetWindow(handle, 4));
            Assert.True(IsWindowVisible(handle));
            Read(typeof(SharedDesktopSceneCapture), capture, frame => Is(frame, 20, 20, 0, 255, 0));
            fixture.Send("red");
            Read(typeof(SharedDesktopSceneCapture), capture, frame => Is(frame, 20, 20, 0, 0, 255));
        });
    }

    private SharedDesktopSceneCapture CreateCarrierCapture()
    {
        try { return new SharedDesktopSceneCapture(new(300, 200, 240, 160)); }
        catch (Exception error) { output.WriteLine(error.ToString()); throw; }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NonContributingWindowMotionDoesNotRebuildTheLiveCapture(bool hidden)
    {
        if (GlassDesk.Platform.WindowsCapabilities.BuildNumber != 26100) return;
        using var fixture = new Fixture();
        StaTest.Run(() =>
        {
            SetThreadDpiAwarenessContext(-4);
            var handle = fixture.TopHandle;
            if (hidden) fixture.Send("hide");
            else SetWindowPos(handle, 0, -10000, -10000, 120, 80, 0x14);
            using var capture = new SharedDesktopSceneCapture(new(300, 200, 240, 160));
            var revision = capture.SceneRevision;
            for (var i = 0; i < 12; i++)
            {
                SetWindowPos(handle, 0, hidden ? 360 + i : -10000 + i, hidden ? 240 : -10000, 120, 80, 0x14);
                capture.UpdateExclusions();
            }
            Assert.Equal(revision, capture.SceneRevision);
            var clock = Stopwatch.StartNew();
            CapturePixels? frame = null;
            while (frame is null && clock.Elapsed < TimeSpan.FromSeconds(3))
                frame = capture.TryRead(TimeSpan.FromMilliseconds(16));
            Assert.NotNull(frame);
            Assert.True(Is(frame, 100, 80, 0, 255, 0), "Invisible/offscreen motion must leave the actual green capture intact.");
        });
    }

    [Fact]
    public void ChangedShellIdentityRetiresTheObsoletePrivateDesktopPlane()
    {
        if (GlassDesk.Platform.WindowsCapabilities.BuildNumber != 26100) return;
        StaTest.Run(() =>
        {
            using var capture = new SharedDesktopSceneCapture(new(300, 200, 240, 160));
            var shell = typeof(SharedDesktopSceneCapture).GetField("_shellWindow", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(shell);
            shell.SetValue(capture, (nint)(-1));
            Assert.Throws<InvalidOperationException>(() => capture.UpdateExclusions());
        });
    }

    [Fact]
    public void WindowBoundsChangeCapturesFreshPixelsWithoutRetiringTheLiveScene()
    {
        if (GlassDesk.Platform.WindowsCapabilities.BuildNumber != 26100) return;
        using var fixture = new Fixture();
        StaTest.Run(() =>
        {
            SetThreadDpiAwarenessContext(-4);
            using var capture = CreateCarrierCapture();
            Read(typeof(SharedDesktopSceneCapture), capture, frame => Is(frame, 100, 80, 255, 0, 0));
            var visualField = typeof(SharedDesktopSceneCapture).GetField("visual", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var visual = visualField.GetValue(capture);
            var revision = capture.SceneRevision;
            var movedAt=Stopwatch.GetTimestamp();
            fixture.Send("move");
            Assert.False(capture.UpdateExclusions());
            Assert.Equal(revision,capture.SceneRevision);
            Assert.Equal(visual, visualField.GetValue(capture));
            var moved=Read(typeof(SharedDesktopSceneCapture), capture,
                frame => Is(frame, 100, 80, 0, 255, 0) && Is(frame, 170, 100, 255, 0, 0));
            Assert.True(moved.SourceQpcTicks>=movedAt);
            revision = capture.SceneRevision;
            var resizedAt=Stopwatch.GetTimestamp();
            SetWindowPos(fixture.TopHandle, 0, 450, 280, 48, 48, 0x14);
            Assert.False(capture.UpdateExclusions());
            Assert.Equal(revision,capture.SceneRevision);
            Assert.Equal(visual, visualField.GetValue(capture));
            var resized=Read(typeof(SharedDesktopSceneCapture), capture,
                frame => Is(frame, 170, 100, 255, 0, 0) && Is(frame, 220, 130, 0, 255, 0));
            Assert.True(resized.SourceQpcTicks>=resizedAt);
            fixture.Send("hide");
            Assert.False(capture.UpdateExclusions());
            Assert.Equal(visual, visualField.GetValue(capture));
            Read(typeof(SharedDesktopSceneCapture), capture, frame => Is(frame, 170, 100, 0, 255, 0));
            fixture.Send("add");
            Assert.True(capture.UpdateExclusions());
            Assert.NotEqual(visual, visualField.GetValue(capture));
            Read(typeof(SharedDesktopSceneCapture), capture, frame => Is(frame, 120, 96, 0, 255, 255));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MaximizedWindowCaptureUsesTheExpandedBoundsAndRestoresWithoutStaleContent(bool initiallyMaximized)
    {
        if (GlassDesk.Platform.WindowsCapabilities.BuildNumber != 26100) return;
        using var fixture = new Fixture();
        StaTest.Run(() =>
        {
            SetThreadDpiAwarenessContext(-4);
            if (initiallyMaximized) fixture.Send("maximize");
            using var capture = CreateCarrierCapture();
            if (initiallyMaximized)
            {
                Read(typeof(SharedDesktopSceneCapture), capture, frame => Is(frame, 20, 20, 255, 0, 0));
                fixture.Send("restore");
            }
            Read(typeof(SharedDesktopSceneCapture), capture, frame => Is(frame, 20, 20, 0, 255, 0));
            for (var i = 0; i < 4; i++)
            {
                fixture.Send("maximize");
                Read(typeof(SharedDesktopSceneCapture), capture, frame => Is(frame, 20, 20, 255, 0, 0));
                fixture.Send("restore");
                Read(typeof(SharedDesktopSceneCapture), capture, frame => Is(frame, 20, 20, 0, 255, 0));
            }
        });
    }

    [Fact]
    public void MaximizeAndRestoreRebindTheForeignCompositionWhileOrdinaryMovesStayLive()
    {
        if (GlassDesk.Platform.WindowsCapabilities.BuildNumber != 26100) return;
        using var fixture = new Fixture();
        StaTest.Run(() =>
        {
            SetThreadDpiAwarenessContext(-4);
            using var capture = CreateCarrierCapture();
            Read(typeof(SharedDesktopSceneCapture), capture, frame => Is(frame, 20, 20, 0, 255, 0));
            var visualField = typeof(SharedDesktopSceneCapture).GetField("visual", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var normalVisual = visualField.GetValue(capture);
            fixture.Send("maximize");
            Assert.True(capture.UpdateExclusions());
            Assert.NotEqual(normalVisual, visualField.GetValue(capture));
            Read(typeof(SharedDesktopSceneCapture), capture, frame => Is(frame, 20, 20, 255, 0, 0));
            var maximizedVisual = visualField.GetValue(capture);
            fixture.Send("restore");
            Assert.True(capture.UpdateExclusions());
            Assert.NotEqual(maximizedVisual, visualField.GetValue(capture));
            Read(typeof(SharedDesktopSceneCapture), capture, frame => Is(frame, 20, 20, 0, 255, 0));
        });
    }

    [Fact]
    public void NativeLiveVisualStopsPaintingAClosedForeignWindowWithoutRecommittingItsExclusions()
    {
        if (GlassDesk.Platform.WindowsCapabilities.BuildNumber != 26100) return;
        using var fixture = new Fixture();
        StaTest.Run(() =>
        {
            SetThreadDpiAwarenessContext(-4);
            using var capture = CreateCarrierCapture();
            Read(typeof(SharedDesktopSceneCapture), capture, frame => Is(frame, 100, 80, 255, 0, 0));
            // Diagnostic only: establish whether DWM itself tracks foreign HWND
            // changes before changing the production scene-rebuild policy.
            capture.DiagnosticFreezeExclusions = true;
            fixture.Send("close");
            Read(typeof(SharedDesktopSceneCapture), capture, frame => Is(frame, 120, 96, 0, 255, 0));
        });
    }

    [Fact]
    public void CrashedNativeWorkerFallsBackWithoutTerminatingTheCaller()
    {
        var type = typeof(SharedBackdropFeed).Assembly.GetType("RecoveringDesktopSceneCapture");
        Assert.NotNull(type);
        using var fixture = new Fixture();
        StaTest.Run(() =>
        {
            SetThreadDpiAwarenessContext(-4);
            var executable = CompatibilityProbePath.File("GlassCaptureProbe.exe");
            using var capture = (IDisposable)Activator.CreateInstance(type, BindingFlags.NonPublic | BindingFlags.Instance,
                null, [new ScreenRect(300, 200, 240, 160), null, executable], null)!;
            Read(type, capture, frame => Is(frame, 20, 20, 0, 255, 0));
            var carrier = (nint)type.GetProperty("CarrierHandle", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(capture)!;
            GetWindowThreadProcessId(carrier, out var processId);
            Assert.NotEqual((uint)Environment.ProcessId, processId);
            using var worker = Process.GetProcessById((int)processId);
            worker.Kill(); worker.WaitForExit(3000);
            Read(type, capture, frame => Is(frame, 20, 20, 0, 255, 0) && Is(frame, 100, 80, 255, 0, 0));
            var fallbackCarrier = (nint)type.GetProperty("CarrierHandle", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(capture)!;
            GetWindowThreadProcessId(fallbackCarrier, out var fallbackProcess);
            Assert.NotEqual((uint)Environment.ProcessId, fallbackProcess);
            Assert.True(worker.HasExited);
        });
    }
    [Theory]
    [InlineData(22000, "IsolatedSharedVisual")]
    [InlineData(22621, "IsolatedSharedVisual")]
    [InlineData(22631, "IsolatedSharedVisual")]
    [InlineData(26100, "IsolatedSharedVisual")]
    [InlineData(26200, "IsolatedSharedVisual")]
    [InlineData(26300, "IsolatedSharedVisual")]
    [InlineData(28000, "IsolatedSharedVisual")]
    [InlineData(40000, "IsolatedSharedVisual")]
    public void UnknownWindows11BuildsKeepPrivateAbiOutsideTheMainProcess(int build, string expected)
    {
        var factory = typeof(SharedBackdropFeed).Assembly.GetType("DesktopSceneCaptureFactory");
        Assert.NotNull(factory);
        var select = factory.GetMethod("SelectProvider", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.Equal(expected, select.Invoke(null, [build])!.ToString());
    }

    [Fact]
    public void IsolatedDesktopPlaneMatchesVerifiedSceneOnReferenceBuild()
    {
        if (GlassDesk.Platform.WindowsCapabilities.BuildNumber != 26100) return;
        StaTest.Run(() =>
        {
            SetThreadDpiAwarenessContext(-4);
            var bounds = GlassDesk.Platform.Displays.DisplayService.Primary()!.Bounds;
            var region = new ScreenRect(bounds.X + bounds.Width / 2 - 80, bounds.Bottom - 200, 160, 48);
            // Keep a contemporaneous reference. A snapshot taken before the helper's
            // cold startup can belong to a different desktop/animation state.
            // The helper excludes every running product process. Match that
            // contract in the reference too: an installed Dock may cover this
            // region, so capturing it on only one side compares different scenes.
            using var referenceSource = new SharedDesktopSceneCapture(region,
                excludedProcesses: CaptureProcessFamily.Snapshot);
            var worker = typeof(SharedBackdropFeed).Assembly.GetType("IsolatedDesktopSceneCapture");
            Assert.NotNull(worker);
            var executable = CompatibilityProbePath.File("GlassCaptureProbe.exe");
            using var capture = (IDisposable)Activator.CreateInstance(worker, BindingFlags.Instance | BindingFlags.NonPublic,
                null, [region, null, executable], null)!;
            var frame = Read(worker, capture, pixels =>
            {
                var reference = referenceSource.TryRead(TimeSpan.FromMilliseconds(16));
                return reference is not null
                    && Math.Abs(pixels.SourceQpcTicks - reference.SourceQpcTicks) <= Stopwatch.Frequency / 10
                    && Math.Abs(pixels.Center[0] - reference.Center[0]) <= 8
                    && Math.Abs(pixels.Center[1] - reference.Center[1]) <= 8
                    && Math.Abs(pixels.Center[2] - reference.Center[2]) <= 8;
            });
            output.WriteLine("Isolated live desktop plane BGRA: " + string.Join(",", frame.Center));
        });
    }

    [Theory]
    [InlineData("documented")]
    [InlineData("private-helper")]
    [InlineData("documented-helper")]
    public void CompatibleScenesCaptureLiveWindowsInOrderWithoutCapturingOwnWindows(string provider)
    {
        var isolated = provider != "documented";
        var providerType = typeof(SharedBackdropFeed).Assembly.GetType(isolated ? "IsolatedDesktopSceneCapture" : "DocumentedDesktopSceneCapture");
        Assert.NotNull(providerType);
        using var fixture = new Fixture();
        StaTest.Run(() =>
        {
            SetThreadDpiAwarenessContext(-4);
            // This red own-process window covers the external green/blue scene. It must
            // remain visible to screenshots, while never appearing in background pixels.
            var own = new Window { Left = 300, Top = 200, Width = 240, Height = 160,
                WindowStyle = WindowStyle.None, Background = Brushes.Red, Topmost = true, ShowInTaskbar = false };
            own.Show();
            SetWindowPos(new System.Windows.Interop.WindowInteropHelper(own).Handle, -1, 300, 200, 240, 160, 0x10);
            try
            {
                var executable = CompatibilityProbePath.File("GlassCaptureProbe.exe");
                object?[] arguments = provider == "documented-helper" ? [new ScreenRect(300, 200, 240, 160), true, null, executable]
                    : isolated ? [new ScreenRect(300, 200, 240, 160), null, executable]
                    : [new ScreenRect(300, 200, 240, 160), false, null];
                using var capture = (IDisposable)Activator.CreateInstance(providerType,
                    BindingFlags.Instance | BindingFlags.NonPublic, null,
                    arguments, null)!;
                var initial = Read(providerType, capture, frame => Is(frame, 20, 20, 0, 255, 0)
                    && Is(frame, 100, 80, 255, 0, 0));
                output.WriteLine($"Compatible scene (isolated={isolated}): {initial.Width}x{initial.Height}, green and blue, own red excluded.");
                GetWindowDisplayAffinity(new System.Windows.Interop.WindowInteropHelper(own).Handle, out var affinity);
                Assert.Equal(0u, affinity);
                fixture.Send("move");
                Read(providerType, capture, frame => Is(frame, 100, 80, 0, 255, 0)
                    && Is(frame, 200, 120, 255, 0, 0));
                fixture.Send("hide");
                Read(providerType, capture, frame => Is(frame, 100, 80, 0, 255, 0));
                fixture.Send("red");
                Read(providerType, capture, frame => Is(frame, 20, 20, 0, 0, 255));
            }
            finally { own.Close(); }
        });
    }

    [Fact]
    public void RevealSnapshotUsesTheDesktopAfterAForeignWindowMoves()
    {
        using var fixture = new Fixture();
        StaTest.Run(() =>
        {
            SetThreadDpiAwarenessContext(-4);
            using var sampler = new GlassDesk.Platform.DesktopRegionSampler();
            for (var index = 0; index < 20; index++)
            {
                Assert.True(SetWindowPos(fixture.TopHandle, -1, 360, 240, 120, 80, 0x10));
                Thread.Sleep(40);
                Assert.True(SetWindowPos(fixture.TopHandle, -1, 480, 280, 120, 80, 0x10));
                Assert.True(sampler.TryCaptureBeforeReveal(new(400, 280, 16, 16), 16, 16, out var pixels));
                Assert.True(pixels![0] <= 3 && pixels[1] >= 252 && pixels[2] <= 3,
                    $"Snapshot retained the moved blue window: {pixels[0]},{pixels[1]},{pixels[2]} at reveal {index}.");
            }
        });
    }

    private static bool Is(CapturePixels frame, int x, int y, byte b, byte g, byte r)
    {
        var i = (y * frame.Width + x) * 4;
        return Math.Abs(frame.Pixels[i] - b) <= 3 && Math.Abs(frame.Pixels[i + 1] - g) <= 3
            && Math.Abs(frame.Pixels[i + 2] - r) <= 3 && frame.Pixels[i + 3] >= 250;
    }

    private static CapturePixels Read(Type type, object capture, Func<CapturePixels, bool> matches)
    {
        var method = type.GetMethod("TryRead", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var watch = Stopwatch.StartNew();
        CapturePixels? last = null;
        while (watch.Elapsed < TimeSpan.FromSeconds(6))
        {
            last = (CapturePixels?)method.Invoke(capture, [TimeSpan.FromMilliseconds(16), CancellationToken.None, 0L, false]);
            if (last is not null && matches(last)) return last;
            global::Program.Pump(TimeSpan.FromMilliseconds(10));
        }
        throw new TimeoutException("Documented scene did not deliver expected pixels. Center=" +
            (last is null ? "no frame" : string.Join(",", last.Center)));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "GlassDesk-compatibility-" + Guid.NewGuid().ToString("N"));
        private readonly Process _process;
        public nint TopHandle
        {
            get
            {
                using var ready = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(_root, "ready.json")));
                return (nint)ready.RootElement.GetProperty("top").GetInt64();
            }
        }
        public Fixture()
        {
            Directory.CreateDirectory(_root);
            var probe = CompatibilityProbePath.File("GlassCaptureProbe.dll");
            var start = new ProcessStartInfo("D:\\dotnet10\\dotnet.exe") { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add(probe); start.ArgumentList.Add("fixture"); start.ArgumentList.Add(_root);
            _process = Process.Start(start)!;
            Wait(() => File.Exists(Path.Combine(_root, "ready.json")));
        }
        public void Send(string command)
        {
            WriteCommand(command);
            Wait(() => File.Exists(Path.Combine(_root, "ack.txt")) && File.ReadAllText(Path.Combine(_root, "ack.txt")) == command);
        }
        private void WriteCommand(string command)
        {
            var watch = Stopwatch.StartNew();
            while (true)
            {
                try { File.WriteAllText(Path.Combine(_root, "command.txt"), command); return; }
                catch (IOException) when (watch.Elapsed < TimeSpan.FromSeconds(2)) { Thread.Sleep(10); }
            }
        }
        private void Wait(Func<bool> ready)
        {
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(10))
            {
                if (_process.HasExited) throw new Exception("Fixture exited: " + _process.ExitCode);
                try { if (ready()) return; } catch (IOException) { }
                Thread.Sleep(20);
            }
            throw new TimeoutException("Compatibility fixture timed out.");
        }
        public void Dispose()
        {
            if (!_process.HasExited)
            {
                WriteCommand("exit");
                if (!_process.WaitForExit(3000)) _process.Kill();
            }
            _process.Dispose();
        }
    }

    [DllImport("user32.dll")] private static extern bool GetWindowDisplayAffinity(nint hwnd, out uint affinity);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
}
