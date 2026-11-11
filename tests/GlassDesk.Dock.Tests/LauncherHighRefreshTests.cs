using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using GlassDesk.Launcher;
using GlassDesk.Platform.Displays;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock.Tests;

public sealed class LauncherHighRefreshTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LauncherRestoresCaptureCadenceAfterSettlingAndAfterHide(bool experimental)
    {
        StaTest.Run(() =>
        {
            var previous = Environment.GetEnvironmentVariable("GLASSDESK_EXPERIMENTAL_120HZ");
            Environment.SetEnvironmentVariable("GLASSDESK_EXPERIMENTAL_120HZ", experimental ? "1" : null);
            var window = new LauncherWindow();
            window.ApplySettings(new() { HighRefreshAnimationsEnabled = false });
            var surface = (LiquidGlassSurface)window.FindName("GlassSurface");
            using var feed = new SharedBackdropFeed((state, cancellation) => cancellation.WaitHandle.WaitOne());
            InstallFeed(surface, feed);
            try
            {
                var display = DisplayService.Primary()!;
                var source = new PixelRect(display.WorkArea.X + 60, display.WorkArea.Bottom - 84, 48, 48);
                window.ShowWithTransition(source);
                Assert.InRange(CaptureInterval(surface), experimental ? 8 : 16, experimental ? 8.4 : 16);
                typeof(LauncherWindow).GetMethod("CompleteContainerTransform", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, [1d]);
                Assert.Equal(16, CaptureInterval(surface));
                window.CloseWithTransition();
                Assert.InRange(CaptureInterval(surface), experimental ? 8 : 16, experimental ? 8.4 : 16);
                window.Hide();
                Assert.Equal(16, CaptureInterval(surface));
            }
            finally
            {
                window.Close();
                Environment.SetEnvironmentVariable("GLASSDESK_EXPERIMENTAL_120HZ", previous);
            }
        });
    }

    private static double CaptureInterval(LiquidGlassSurface surface) => JsonSerializer.SerializeToElement(surface.InspectPerformance())
        .GetProperty("capture").GetProperty("live").GetProperty("captureIntervalMilliseconds").GetDouble();

    [Fact]
    public void ActiveMorphUsesShorterAcquisitionBudgetAndRestoresDefaultOnCompletion()
    {
        StaTest.Run(() =>
        {
            using var surface = new LiquidGlassSurface();
            var capture = new BudgetCapture();
            using var feed = new SharedBackdropFeed((_, _, _) => capture);
            InstallFeed(surface, feed);
            SetActive(surface, true);
            var active = ReadBudgets(feed, capture);
            Assert.InRange(active, 0, 8.4);
            SetActive(surface, false);
            var resting = ReadBudgets(feed, capture);
            Assert.True(resting > 10, $"Resting acquisition budget={resting:0.00}ms");
        });
    }

    [Fact]
    public void MorphCadenceDoesNotChangeAnotherSurfaceAndSleepClearsIt()
    {
        StaTest.Run(() =>
        {
            using var launcher = new LiquidGlassSurface();
            using var other = new LiquidGlassSurface();
            var launcherCapture = new BudgetCapture(); var otherCapture = new BudgetCapture();
            using var launcherFeed = new SharedBackdropFeed((_, _, _) => launcherCapture);
            using var otherFeed = new SharedBackdropFeed((_, _, _) => otherCapture);
            InstallFeed(launcher, launcherFeed); InstallFeed(other, otherFeed);
            SetActive(launcher, true);
            Assert.InRange(ReadBudgets(launcherFeed, launcherCapture), 0, 8.4);
            Assert.True(ReadBudgets(otherFeed, otherCapture) > 10);
            launcher.SetPerformanceSleeping(true);
            SetActive(launcher, true); // A late animation request must not wake a sleeping surface.
            launcher.SetPerformanceSleeping(false);
            Assert.True(ReadBudgets(launcherFeed, launcherCapture) > 10);
        });
    }

    private static void SetActive(LiquidGlassSurface surface, bool active) =>
        typeof(LiquidGlassSurface).GetMethod("SetContainerAnimationActive")?.Invoke(surface, [active]);

    private static void InstallFeed(LiquidGlassSurface surface, SharedBackdropFeed feed)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var background = typeof(LiquidGlassSurface).GetField("_background", flags)!.GetValue(surface)!;
        var field = background.GetType().GetField("_compatible", flags)!;
        ((IDisposable)field.GetValue(background)!).Dispose();
        field.SetValue(background, feed);
    }

    private static double ReadBudgets(SharedBackdropFeed feed, BudgetCapture capture)
    {
        var startCount = capture.Budgets.Count;
        var watch = Stopwatch.StartNew();
        while (capture.Budgets.Count < startCount + 12 && watch.Elapsed < TimeSpan.FromSeconds(3))
        {
            if (feed.TryRead(new PixelRect(0, 0, 8, 8), 8, 8, out var frame)) frame?.Dispose();
            Thread.Sleep(1);
        }
        var budgets = capture.Budgets.ToArray().Skip(startCount + 2).Order().ToArray();
        Assert.True(budgets.Length >= 8, "Acquisition worker did not produce enough samples.");
        return budgets[budgets.Length / 2];
    }

    private sealed class BudgetCapture : IDesktopSceneCapture
    {
        internal ConcurrentQueue<double> Budgets { get; } = new();
        private readonly byte[] _pixels = Enumerable.Repeat((byte)80, 8 * 8 * 4).ToArray();
        public nint CarrierHandle => 0;
        public bool UpdateExclusions() => false;
        public CapturePixels? TryRead(TimeSpan waitForFrame = default, CancellationToken cancellation = default,
            long sourceNotBeforeTicks = 0, bool reusePixels = false)
        {
            Budgets.Enqueue(waitForFrame.TotalMilliseconds);
            return new CapturePixels(8, 8, _pixels) { SourceQpcTicks = Stopwatch.GetTimestamp() };
        }
        public void ReleaseReadbackBuffers() { }
        public void Dispose() { }
    }
}
