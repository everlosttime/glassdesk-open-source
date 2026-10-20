using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock.Tests;

public sealed class GlassRevealTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HiddenPreparationUsesTheSelfExcludedNativeFrameWhenDesktopSamplingIsUnavailable(bool black)
    {
        StaTest.Run(() =>
        {
            var scene = new GlassBackdropScene();
            using var surface = new LiquidGlassSurface { ReadBackdropScene = () => scene, IsBackdropActive = () => false,
                CaptureRevealBackdrop = (_, _, _) => null };
            using var feed = new SharedBackdropFeed((state, stop) =>
            {
                long sequence = 0;
                while (!stop.IsCancellationRequested)
                {
                    if (state.CurrentRequest is { } request)
                    {
                        var pixels = new byte[request.Width * request.Height * 4];
                        for (var i = 0; i < pixels.Length; i += 4) { pixels[i + 1] = black ? (byte)0 : (byte)180; pixels[i + 3] = 255; }
                        var now = System.Diagnostics.Stopwatch.GetTimestamp();
                        state.Publish(new BackdropFrame(request, ++sequence, now, now, pixels, now));
                    }
                    if (stop.WaitHandle.WaitOne(16)) return;
                }
            });
            var background = typeof(LiquidGlassSurface).GetField("_background", Private)!.GetValue(surface)!;
            typeof(GlassBackgroundLayer).GetField("_compatible", Private)!.SetValue(background, feed);
            var owner = new RevealWindow { Content = surface, Width = 200, Height = 100, ShowActivated = false };
            try
            {
                owner.Show(); surface.UpdateLayout(); surface.SetPanelShape(new(0, 0, 200, 100), 20);
                var host = GlassDesk.Platform.Displays.DisplayService.GetWindowPixelBounds(new System.Windows.Interop.WindowInteropHelper(owner).Handle)!.Value;
                var ready = false; var clock = System.Diagnostics.Stopwatch.StartNew();
                while (!ready && clock.Elapsed < TimeSpan.FromSeconds(2))
                {
                    surface.PrepareRevealMaterial(host, host, VisualTreeHelper.GetDpi(owner).PixelsPerDip * 96);
                    ready = (bool)typeof(LiquidGlassSurface).GetField("_compatibilityIsDesktopSnapshot", Private)!.GetValue(surface)!;
                    if (!ready) Thread.Sleep(16);
                }
                Assert.True(ready, "Valid self-excluded native pixels were discarded instead of preparing the hidden Dock's first frame.");
            }
            finally { owner.Close(); }
        });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void APreparedSnapshotUsesTheSameBoundedVisibleSceneHandoffAsCompletedLiveMaterial(bool unknown, bool repeatChange)
    {
        StaTest.Run(() =>
        {
            GlassBackdropScene? scene = new(new GlassBackdropScene.SceneWindow((nint)101, 42, 0, 0, 800, 600));
            var active = false;
            using var surface = new LiquidGlassSurface { ReadBackdropScene = () => scene, IsBackdropActive = () => active,
                CaptureRevealBackdrop = (_, width, height) => Enumerable.Range(0, width * height)
                    .SelectMany(_ => new byte[] { 20, 180, 20, 255 }).ToArray() };
            using var feed = new SharedBackdropFeed((_, stop) => stop.WaitHandle.WaitOne());
            var background = typeof(LiquidGlassSurface).GetField("_background", Private)!.GetValue(surface)!;
            typeof(GlassBackgroundLayer).GetField("_compatible", Private)!.SetValue(background, feed);
            var owner = new RevealWindow { Content = surface, Width = 200, Height = 100, ShowActivated = false };
            try
            {
                owner.Show(); surface.UpdateLayout(); surface.SetPanelShape(new(0, 0, 200, 100), 20);
                var host = GlassDesk.Platform.Displays.DisplayService.GetWindowPixelBounds(new System.Windows.Interop.WindowInteropHelper(owner).Handle)!.Value;
                surface.PrepareRevealMaterial(host, host, VisualTreeHelper.GetDpi(owner).PixelsPerDip * 96);
                active = true;
                Assert.True((bool)typeof(LiquidGlassSurface).GetField("_compatibilityIsDesktopSnapshot", Private)!.GetValue(surface)!,
                    "The first current desktop snapshot was not prepared: " + surface.RevealMaterialInvalidationReason);
                var initial = RenderCenterPixel(surface);
                Assert.True((bool)typeof(LiquidGlassSurface).GetField("_compatibilityIsDesktopSnapshot", Private)!.GetValue(surface)!,
                    "Rendering retired the prepared snapshot: " + surface.RevealMaterialInvalidationReason);
                scene = unknown ? null : new();
                surface.RequestBackdropRefresh();
                if (unknown)
                {
                    Assert.False((bool)typeof(LiquidGlassSurface).GetField("_compatibilityIsDesktopSnapshot", Private)!.GetValue(surface)!);
                    Assert.NotEqual(initial, RenderCenterPixel(surface));
                }
                else
                {
                    Assert.True((bool)typeof(LiquidGlassSurface).GetField("_compatibilityIsDesktopSnapshot", Private)!.GetValue(surface)!,
                        "A known window close discarded the displayed snapshot before the replacement.");
                    Assert.Equal(initial, RenderCenterPixel(surface));
                    if (repeatChange)
                    {
                        var deadline = (long)typeof(LiquidGlassSurface).GetField("_materialRetentionUntil", Private)!.GetValue(surface)!;
                        scene = new(new GlassBackdropScene.SceneWindow((nint)202, 43, 0, 0, 800, 600));
                        surface.RequestBackdropRefresh();
                        Assert.Equal(deadline, (long)typeof(LiquidGlassSurface).GetField("_materialRetentionUntil", Private)!.GetValue(surface)!);
                    }
                    Set(surface, "_materialRetentionUntil", 1L);
                    surface.RequestBackdropRefresh();
                    Assert.False((bool)typeof(LiquidGlassSurface).GetField("_compatibilityIsDesktopSnapshot", Private)!.GetValue(surface)!);
                    Assert.NotEqual(initial, RenderCenterPixel(surface));
                }
            }
            finally { owner.Close(); }
        });
    }

    [Fact]
    public void ConcealingAnInactivePanelDoesNotGenerateAVisibleMaterialLossIncident()
    {
        StaTest.Run(() =>
        {
            using var surface = new LiquidGlassSurface { ReadBackdropScene = () => new(), IsBackdropActive = () => false };
            var owner = new RevealWindow { Content = surface, Width = 200, Height = 100, ShowActivated = false };
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "GlassDesk-concealed-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                owner.Show(); surface.UpdateLayout(); SeedMaterial(surface);
                Assert.True(RenderCenter(surface) > 100);
                surface.RetireConcealedMaterial(); Assert.True(RenderCenter(surface) < 100);
                var id = (int)typeof(LiquidGlassSurface).GetField("_diagnosticId", Private)!.GetValue(surface)!;
                GlassDesk.UI.BackdropRecovery.ExportDiagnosticReport(path);
                using var report = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(path));
                Assert.DoesNotContain(report.RootElement.GetProperty("capture").GetProperty("RecentEvents").EnumerateArray(),
                    e => e.GetProperty("Stage").GetString() == "MaterialLoss" && e.GetProperty("SurfaceId").GetInt32() == id);
            }
            finally { owner.Close(); if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
        });
    }

    [Fact]
    public void ATransientWorkerFailureKeepsTheAlreadyDisplayedLivePixels()
    {
        StaTest.Run(() =>
        {
            var scene = new GlassBackdropScene();
            using var surface = new LiquidGlassSurface { ReadBackdropScene = () => scene };
            using var feed = new SharedBackdropFeed((_, _) => throw new InvalidOperationException("transient source"));
            var background = (GlassBackgroundLayer)typeof(LiquidGlassSurface).GetField("_background", Private)!.GetValue(surface)!;
            typeof(GlassBackgroundLayer).GetField("_compatible", Private)!.SetValue(background, feed);
            var owner = new RevealWindow { Content = surface, Width = 200, Height = 100, ShowActivated = false };
            try
            {
                owner.Show(); surface.UpdateLayout(); SeedMaterial(surface);
                Set(surface, "_materialStamp", typeof(LiquidGlassSurface).GetMethod("ReadMaterialStamp", Private)!.Invoke(surface, null)!);
                Assert.True(RenderCenter(surface) > 100);
                typeof(LiquidGlassSurface).GetMethod("OnFrame", Private)!.Invoke(surface, [null, EventArgs.Empty]);
                Assert.True(SpinWait.SpinUntil(() => feed.Failure is not null, TimeSpan.FromSeconds(2)));
                typeof(LiquidGlassSurface).GetMethod("OnFrame", Private)!.Invoke(surface, [null, EventArgs.Empty]);
                Assert.True(RenderCenter(surface) > 100, "A single recoverable failure discarded complete live material.");
                typeof(GlassBackgroundLayer).GetField("_pendingSince", Private)!.SetValue(background,
                    System.Diagnostics.Stopwatch.GetTimestamp() - System.Diagnostics.Stopwatch.Frequency * 4);
                typeof(LiquidGlassSurface).GetMethod("OnFrame", Private)!.Invoke(surface, [null, EventArgs.Empty]);
                Assert.True(RenderCenter(surface) < 100, "Sustained failure must still retire stale content.");
            }
            finally { owner.Close(); }
        });
    }

    [Fact]
    public void AnUnknownVisibleSceneRecordsTheCauseOfItsMaterialRetirement()
    {
        StaTest.Run(() =>
        {
            GlassBackdropScene? scene = new();
            using var surface = new LiquidGlassSurface { ReadBackdropScene = () => scene };
            var owner = new RevealWindow { Content = surface, Width = 200, Height = 100, ShowActivated = false };
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "GlassDesk-material-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                owner.Show(); surface.UpdateLayout(); SeedMaterial(surface);
                Assert.True(RenderCenter(surface) > 100);
                scene = null; surface.RequestBackdropRefresh();
                Assert.True(RenderCenter(surface) < 100);
                GlassDesk.UI.BackdropRecovery.ExportDiagnosticReport(path);
                using var report = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(path));
                Assert.Contains(report.RootElement.GetProperty("capture").GetProperty("RecentEvents").EnumerateArray(),
                    e => e.GetProperty("Stage").GetString() == "MaterialLoss" && e.GetProperty("Reason").GetString() == "SceneUnknown");
            }
            finally { owner.Close(); if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VisibleWindowCloseOrMovementKeepsMaterialUntilBoundedFreshFrameHandoff(bool move)
    {
        StaTest.Run(() =>
        {
            var original = new GlassBackdropScene.SceneWindow((nint)101, 42, 0, 0, 800, 600);
            var scene = new GlassBackdropScene(original);
            using var surface = new LiquidGlassSurface { ReadBackdropScene = () => scene };
            using var feed = new SharedBackdropFeed((state, stop) => stop.WaitHandle.WaitOne());
            var background = typeof(LiquidGlassSurface).GetField("_background", Private)!.GetValue(surface)!;
            typeof(GlassBackgroundLayer).GetField("_compatible", Private)!.SetValue(background, feed);
            var owner = new RevealWindow { Content = surface, Width = 200, Height = 100, ShowActivated = false };
            try
            {
                owner.Show(); surface.UpdateLayout();
                SeedMaterial(surface);
                scene = move ? new(original with { Left = 20 }) : new();
                surface.RequestBackdropRefresh();
                Assert.True(RenderCenter(surface) > 100,
                    "An ordinary visible scene change must not replace complete glass with the dark waiting material.");
                Set(surface, "_materialRetentionUntil", 1L);
                surface.RequestBackdropRefresh();
                Assert.True(RenderCenter(surface) < 100,
                    "The handoff expires if fresh capture stalls; it cannot freeze a closed window indefinitely.");
            }
            finally { owner.Close(); }
        });
    }

    [Fact]
    public void VisibleHandoffStillRejectsPixelsAcrossARecoveryBoundary()
    {
        StaTest.Run(() =>
        {
            var scene = new GlassBackdropScene();
            using var surface = new LiquidGlassSurface { ReadBackdropScene = () => scene };
            var owner = new RevealWindow { Content = surface, Width = 200, Height = 100, ShowActivated = false };
            try
            {
                owner.Show(); surface.UpdateLayout(); SeedMaterial(surface);
                GlassDesk.UI.BackdropRecovery.RetryBackground();
                scene = new();
                surface.RequestBackdropRefresh();
                Assert.True(RenderCenter(surface) < 100, "Recovery cannot authorize the old desktop texture.");
            }
            finally { owner.Close(); }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VisibleWindowMotionDoesNotSuspendTheProducerOrCancelCurrentComposition(bool previousMaterialExpired)
    {
        StaTest.Run(() =>
        {
            var original = new GlassBackdropScene.SceneWindow((nint)101, 42, 0, 0, 800, 600);
            var scene = new GlassBackdropScene(original);
            using var surface = new LiquidGlassSurface { ReadBackdropScene = () => scene };
            using var feed = new SharedBackdropFeed((state, stop) => stop.WaitHandle.WaitOne());
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var background = typeof(LiquidGlassSurface).GetField("_background", Private)!.GetValue(surface)!;
            typeof(GlassBackgroundLayer).GetField("_compatible", Private)!.SetValue(background, feed);
            var worker = (GlassFrameWorker)typeof(LiquidGlassSurface).GetField("_worker", Private)!.GetValue(surface)!;
            var owner = new RevealWindow { Content = surface, Width = 200, Height = 100, ShowActivated = false };
            try
            {
                owner.Show(); surface.UpdateLayout(); SeedMaterial(surface);
                feed.TryRead(new(0, 0, 4, 4), 4, 4, out var unused);
                unused?.Dispose();
                var state = (LatestBackdropState)typeof(SharedBackdropFeed).GetField("_state", Private)!.GetValue(feed)!;
                Assert.NotNull(state.CurrentRequest);
                var pending = worker.RenderBufferedAsync(4, (pixels, _) =>
                {
                    entered.Set();
                    if (!release.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("Composition gate timed out.");
                    pixels[0] = 192; pixels[3] = 255;
                });
                Assert.True(entered.Wait(TimeSpan.FromSeconds(1)));
                scene = new(original with { Left = 20 });
                if (previousMaterialExpired) Set(surface, "_materialRetentionUntil", 1L);
                surface.RequestBackdropRefresh();
                Assert.NotNull(state.CurrentRequest);
                release.Set();
                using var result = pending.GetAwaiter().GetResult();
                Assert.NotNull(result);
                Assert.True(worker.IsCurrent(result));
                Assert.Equal(192, result.Pixels[0]);
            }
            finally { release.Set(); owner.Close(); }
        });
    }

    [Fact]
    public void UnverifiedSceneCannotDisplayPreviouslyCapturedWindowPixels()
    {
        StaTest.Run(() =>
        {
            GlassBackdropScene? scene = new(new GlassBackdropScene.SceneWindow((nint)101, 42, 0, 0, 800, 600));
            using var surface = new LiquidGlassSurface { ReadBackdropScene = () => scene, IsBackdropActive = () => true };
            var owner = new RevealWindow { Content = surface, Width = 200, Height = 100, ShowActivated = false };
            try
            {
                owner.Show(); surface.RequestBackdropRefresh();
                typeof(LiquidGlassSurface).GetMethod("OnFrame", Private)!.Invoke(surface, [null, EventArgs.Empty]);
                SeedMaterial(surface);
                scene = null;
                Assert.True(RenderCenter(surface) < 100, "An unverified scene cannot authorize reuse of another window's pixels.");
                surface.RequestBackdropRefresh(revealing: true);
                Assert.True(RenderCenter(surface) < 100, "A reveal must not reuse material whose current scene cannot be validated.");
            }
            finally {owner.Close();}
        });
    }

    [Theory]
    [InlineData("recent", true)]
    [InlineData("expired", false)]
    [InlineData("future", false)]
    [InlineData("unknown", false)]
    [InlineData("recovery", false)]
    [InlineData("pose", false)]
    public void CompletedVisibleCompositionRequiresFreshPixelsAndTheOriginalCapturePose(string change, bool expected)
    {
        StaTest.Run(() =>
        {
            var original = new GlassBackdropScene(new GlassBackdropScene.SceneWindow((nint)101, 42, 0, 0, 800, 600));
            GlassBackdropScene? scene = original;
            using var surface = new LiquidGlassSurface { ReadBackdropScene = () => scene };
            var owner = new RevealWindow { Content = surface, Width = 200, Height = 100, ShowActivated = false };
            try
            {
                owner.Show(); surface.UpdateLayout(); SeedMaterial(surface);
                var capturedStamp = typeof(LiquidGlassSurface).GetMethod("ReadMaterialStamp", Private)!.Invoke(surface, null);
                if (change == "recovery") GlassDesk.UI.BackdropRecovery.RetryBackground();
                scene = change == "unknown" ? null : new();
                surface.RequestBackdropRefresh();
                if (change == "pose") owner.Left += 40;
                var ticks = System.Diagnostics.Stopwatch.GetTimestamp();
                if (change == "expired") ticks -= System.Diagnostics.Stopwatch.Frequency;
                if (change == "future") ticks += System.Diagnostics.Stopwatch.Frequency;
                var accepted = (bool)typeof(LiquidGlassSurface).GetMethod("CanCommitCapturedScene", Private)!
                    .Invoke(surface, [original, capturedStamp, ticks])!;
                Assert.Equal(expected, accepted);
            }
            finally { owner.Close(); }
        });
    }

    [Fact]
    public void ChangedLiveSceneCompletesBoundedHandoffAndDoesNotReturnAfterHiding()
    {
        StaTest.Run(() =>
        {
            var scene = new GlassBackdropScene(new GlassBackdropScene.SceneWindow((nint)101, 42, 0, 0, 800, 600));
            using var surface = new LiquidGlassSurface { ReadBackdropScene = () => scene, IsBackdropActive = () => true };
            var owner = new RevealWindow { Content = surface, Width = 200, Height = 100, ShowActivated = false };
            try
            {
                owner.Show();surface.RequestBackdropRefresh();
                typeof(LiquidGlassSurface).GetMethod("OnFrame", Private)!.Invoke(surface, [null, EventArgs.Empty]);
                SeedMaterial(surface);
                scene = new(new GlassBackdropScene.SceneWindow((nint)202, 42, 0, 0, 800, 600));
                // Simulate an already-running backdrop without letting a real capture
                // replace the controlled texture during this synchronous render.
                surface.UpdateLayout();
                Assert.True(RenderCenter(surface) > 100, "An already-visible surface keeps complete glass during the bounded handoff.");
                owner.Hide();owner.Show();
                Assert.True(RenderCenter(surface) < 100, "A retained live frame from the old scene must not return after hiding.");
            }
            finally {owner.Close();}
        });
    }

    [Fact]
    public void ChangedDesktopSceneCannotPaintEitherCachedTextureOnReveal()
    {
        StaTest.Run(() =>
        {
            var scene = new GlassBackdropScene(new GlassBackdropScene.SceneWindow((nint)101, 42, 0, 0, 800, 600));
            using var surface = new LiquidGlassSurface { ReadBackdropScene = () => scene };
            surface.RequestBackdropRefresh();
            SeedMaterial(surface);
            var backdrop = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] {20,180,20,255}, 4);
            backdrop.Freeze();
            Set(surface, "_transitionBackdrop", backdrop);
            Set(surface, "_transitionBackdropBounds", new Rect(0, 0, 200, 100));
            surface.SetContainerTransitionBounds(new Rect(0, 0, 200, 100));
            scene = new(new GlassBackdropScene.SceneWindow((nint)202, 42, 0, 0, 800, 600));
            surface.RequestBackdropRefresh();
            Assert.Null(typeof(LiquidGlassSurface).GetField("_frame", Private)!.GetValue(surface));
            Assert.Null(typeof(LiquidGlassSurface).GetField("_transitionBackdrop", Private)!.GetValue(surface));
            Assert.True(RenderCenter(surface) < 100, "The previous window must not survive in the first reveal render.");
            var layer = (TransitionGlassLayer)typeof(LiquidGlassSurface).GetField("_transitionLayer", Private)!.GetValue(surface)!;
            Assert.Equal(0, layer.Opacity);
        });
    }

    [Fact]
    public void SameDesktopSceneKeepsTheCompleteMaterialForImmediateReveal()
    {
        StaTest.Run(() =>
        {
            using var surface = new LiquidGlassSurface
            {
                ReadBackdropScene = () => new(new GlassBackdropScene.SceneWindow((nint)101, 42, 0, 0, 800, 600)),
            };
            surface.RequestBackdropRefresh();
            SeedMaterial(surface);
            surface.RequestBackdropRefresh();
            Assert.True(RenderCenter(surface) > 100);
        });
    }

    [Fact]
    public void DesktopSceneIncludesWindowOrderAndBounds()
    {
        var first = new GlassBackdropScene.SceneWindow((nint)101, 42, 0, 0, 800, 600);
        var second = new GlassBackdropScene.SceneWindow((nint)202, 43, 0, 0, 600, 400);
        var scene = new GlassBackdropScene(first, second);
        Assert.Equal(scene, new GlassBackdropScene(first, second));
        Assert.NotEqual(scene, new GlassBackdropScene(second, first));
        Assert.NotEqual(scene, new GlassBackdropScene(first with { Left = 20 }, second));
    }

    [Fact]
    public void AddedOverlayKeepsCompleteMaterialUntilFreshPixelsArrive()
    {
        StaTest.Run(() =>
        {
            var first = new GlassBackdropScene.SceneWindow((nint)101, 42, 0, 0, 800, 600);
            var scene = new GlassBackdropScene(first);
            using var surface = new LiquidGlassSurface { ReadBackdropScene = () => scene };
            surface.RequestBackdropRefresh(); SeedMaterial(surface);
            scene = new(new GlassBackdropScene.SceneWindow((nint)202, 43, 0, 0, 800, 600), first);
            surface.RequestBackdropRefresh();
            Assert.True(RenderCenter(surface) > 100,
                "Adding an overlay must replace valid material when fresh pixels arrive, without first clearing the glass.");
        });
    }

    [Fact]
    public void RemovedTransientOverlayHasABoundedMaterialReplacementWindow()
    {
        StaTest.Run(() =>
        {
            var first = new GlassBackdropScene.SceneWindow((nint)101, 42, 0, 0, 800, 600);
            var overlay = new GlassBackdropScene.SceneWindow((nint)202, 43, 0, 0, 800, 600, IsTransientOverlay: true);
            var scene = new GlassBackdropScene(overlay, first);
            using var surface = new LiquidGlassSurface { ReadBackdropScene = () => scene };
            surface.RequestBackdropRefresh(); SeedMaterial(surface);
            scene = new(first); surface.RequestBackdropRefresh();
            Assert.True(RenderCenter(surface) > 100, "A transient pointer-transparent overlay must not punch a hole in the material.");
            Set(surface, "_materialRetentionUntil", 1L);
            surface.RequestBackdropRefresh();
            Assert.True(RenderCenter(surface) < 100, "If replacement capture stalls, the bounded overlay grace period must expire.");
        });
    }

    [Fact]
    public void SlidingDockMaterialUsesTheSameMovedOutlineAsItsSharedClip()
    {
        StaTest.Run(() =>
        {
            var dock = new DockWindow();
            dock.BindPrimaryDisplay(new GlassDesk.Platform.Displays.PrimaryDisplaySession(GlassDesk.Platform.Displays.DisplayService.Primary()!));
            var surface = (LiquidGlassSurface)dock.FindName("GlassSurface");
            surface.IsBackdropActive = () => false;
            try
            {
                dock.Show(); surface.UpdateLayout();
                var width = surface.ActualWidth; var height = surface.ActualHeight;
                var backdrop = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] {20,180,20,255}, 4);
                backdrop.Freeze();
                Set(surface, "_transitionBackdrop", backdrop);
                Set(surface, "_transitionBackdropBounds", new Rect(0, 0, width, height * 4));
                surface.SetContainerTransitionBounds(new Rect(0, 0, width, height * 4));
                var resting = (GlassDesk.Platform.Displays.PixelRect)typeof(DockWindow).GetField("_restingRect", Private)!.GetValue(dock)!;
                typeof(DockWindow).GetMethod("ApplySlide", Private)!.Invoke(dock, [resting, resting.Height / 3d]);
                surface.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(surface);
                var pixel = new byte[4];
                bitmap.CopyPixels(new Int32Rect((int)(width / 2), 4, 1, 1), pixel, 4, 0);
                Assert.Equal(0, pixel[3]);
                bitmap.CopyPixels(new Int32Rect((int)(width / 2), (int)(height * .7), 1, 1), pixel, 4, 0);
                Assert.True(pixel[3] > 100, "The moved outline must retain a complete material body.");
            }
            finally { dock.Close(); }
        });
    }

    [Fact]
    public void ShowingAgainPaintsThePreviousMaterialBeforeTheNextCapture()
    {
        StaTest.Run(() =>
        {
            using var surface = new LiquidGlassSurface { IsBackdropActive = () => false };
            var owner = new RevealWindow { Content = surface, Width = 200, Height = 100, ShowActivated = false };
            try
            {
                owner.Show();
                SeedMaterial(surface);
                owner.Hide();
                owner.Show();
                Assert.True(RenderCenter(surface) > 100, "Reveal lost the complete material and painted only the faint waiting body.");
            }
            finally { owner.Close(); }
        });
    }

    [Fact]
    public void InactiveDockTickKeepsMaterialReadyForReveal()
    {
        StaTest.Run(() =>
        {
            using var surface = new LiquidGlassSurface { IsBackdropActive = () => false };
            var owner = new RevealWindow { Content = surface, Width = 200, Height = 100, ShowActivated = false };
            try
            {
                owner.Show();
                SeedMaterial(surface);
                typeof(LiquidGlassSurface).GetMethod("OnFrame", Private)!.Invoke(surface, [null, EventArgs.Empty]);
                Assert.True(RenderCenter(surface) > 100, "The hidden Dock poll discarded its ready material.");
            }
            finally { owner.Close(); }
        });
    }

    [Fact]
    public void LauncherRetainsUnmaskedTextureAcrossHideForTheNextMorph()
    {
        StaTest.Run(() =>
        {
            using var surface = new LiquidGlassSurface { IsBackdropActive = () => false };
            var owner = new RevealWindow { Content = surface, Width = 200, Height = 100, ShowActivated = false };
            try
            {
                owner.Show();
                var backdrop = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 20, 180, 20, 255 }, 4);
                backdrop.Freeze();
                Set(surface, "_transitionBackdrop", backdrop);
                Set(surface, "_transitionBackdropBounds", new Rect(0, 0, 200, 100));
                surface.SetContainerTransitionBounds(new Rect(0, 0, 200, 100));
                owner.Hide();
                owner.Show();
                surface.SetPanelShape(new Rect(20, 10, 160, 80), 20);
                Assert.True(RenderCenter(surface) > 100, "The next morph has no ready texture and waits for a cold capture.");
            }
            finally { owner.Close(); }
        });
    }

    private static void SeedMaterial(LiquidGlassSurface surface)
    {
        var bitmap = new WriteableBitmap(1, 1, 96, 96, PixelFormats.Bgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, 1, 1), new byte[] { 20, 180, 20, 255 }, 4, 0);
        Set(surface, "_frame", bitmap);
        Set(surface, "_frameUsesAndroidGlass", true);
        Set(surface, "_sampledPanelBounds", new Rect(0, 0, 200, 100));
        Set(surface, "_sampledGlassPanelBounds", new Rect(0, 0, 200, 100));
        Set(surface, "_sampledGlassPanelRadius", 20d);
        surface.SetPanelShape(new Rect(0, 0, 200, 100), 20);
    }

    private static byte RenderCenter(LiquidGlassSurface surface)
    {
        surface.Measure(new Size(200, 100));
        surface.Arrange(new Rect(0, 0, 200, 100));
        surface.UpdateLayout();
        var rendered = new RenderTargetBitmap(200, 100, 96, 96, PixelFormats.Pbgra32);
        rendered.Render(surface);
        var pixels = new byte[200 * 100 * 4];
        rendered.CopyPixels(pixels, 800, 0);
        return pixels[(50 * 200 + 100) * 4 + 1];
    }

    private static byte[] RenderCenterPixel(LiquidGlassSurface surface)
    {
        surface.Measure(new Size(200, 100)); surface.Arrange(new Rect(0, 0, 200, 100)); surface.UpdateLayout();
        var rendered = new RenderTargetBitmap(200, 100, 96, 96, PixelFormats.Pbgra32); rendered.Render(surface);
        var pixel = new byte[4]; rendered.CopyPixels(new Int32Rect(100, 50, 1, 1), pixel, 4, 0); return pixel;
    }

    private static void Set(LiquidGlassSurface surface, string name, object value) =>
        typeof(LiquidGlassSurface).GetField(name, Private)!.SetValue(surface, value);

    private sealed class RevealWindow : GlassWindow;
}
