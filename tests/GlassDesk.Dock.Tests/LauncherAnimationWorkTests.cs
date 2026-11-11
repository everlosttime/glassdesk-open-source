using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassDesk.Launcher;
using GlassDesk.Platform.Displays;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock.Tests;

public sealed class LauncherAnimationWorkTests
{
    [Fact]
    public void MorphShadowStrengthKeepsHwndOpacityStableAndPreservesCompositeAlpha()
    {
        StaTest.Run(() =>
        {
            var previous = Environment.GetEnvironmentVariable("GLASSDESK_LAUNCHER_VISUAL_SHADOW_OPACITY");
            Environment.SetEnvironmentVariable("GLASSDESK_LAUNCHER_VISUAL_SHADOW_OPACITY", "1");
            using var fixture = new Fixture();
            try
            {
                var shadow = typeof(GlassWindow).GetField("_shadow", Private)!.GetValue(fixture.Window)!;
                var host = (Window)Get(shadow, "_window");
                var visual = (FrameworkElement)Get(shadow, "_drawing");
                fixture.Apply(new Rect(60, 50, 180, 100), 28, .5);
                var opacity = host.Opacity;
                Assert.Equal(.675, host.Opacity * visual.Opacity, 6);
                fixture.Apply(new Rect(60, 50, 180, 100), 28, .25);
                Assert.Equal(opacity, host.Opacity);
                Assert.Equal(.5125, host.Opacity * visual.Opacity, 6);
            }
            finally { Environment.SetEnvironmentVariable("GLASSDESK_LAUNCHER_VISUAL_SHADOW_OPACITY", previous); }
        });
    }

    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void MorphFramePublishesOneConsistentLensMaterial()
    {
        StaTest.Run(() =>
        {
            using var fixture = new Fixture();
            var surface = (LiquidGlassSurface)fixture.Window.FindName("GlassSurface");
            surface.Measure(new Size(1000, 800)); surface.Arrange(new Rect(0, 0, 1000, 800));
            Set(surface, "_transitionCaptureBounds", new Rect(0, 0, 1000, 800));
            Set(surface, "_transitionBackdropBounds", new Rect(0, 0, 1000, 800));
            Set(surface, "_transitionBackdrop", BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null,
                new byte[] { 80, 100, 120, 255 }, 4));
            Set(Get(surface, "_background"), "<RequiresBasicBackground>k__BackingField", false);
            fixture.Apply(new Rect(30, 30, 400, 300), 35, .3);
            var layer = (FrameworkElement)Get(surface, "_transitionLayer");
            Assert.True(layer.Opacity > 0);
            // The shader path is also used by the production morph on this machine.
            if (layer.Effect is null) return;
            var effect = layer.Effect;
            var property = (DependencyProperty)effect.GetType().GetField("MaterialProperty")!.GetValue(null)!;
            Assert.Equal(.72, ((System.Windows.Media.Media3D.Point4D)effect.GetValue(property)).W, 8);
            var descriptor = DependencyPropertyDescriptor.FromProperty(property, effect.GetType());
            var updates = 0;
            EventHandler changed = (_, _) => updates++;
            descriptor.AddValueChanged(effect, changed);
            try
            {
                fixture.Apply(new Rect(40, 40, 600, 450), 50, .7);
                Assert.Equal(1, updates);
                Assert.Equal(.7, surface.TransitionProgress);
                Assert.Equal(.24, surface.TransitionBlurRadius, 8);
            }
            finally { descriptor.RemoveValueChanged(effect, changed); }
        });
    }

    [Fact]
    public void MorphShadowReducesGeometryWorkAndRestoresFullDetailAtRest()
    {
        StaTest.Run(() =>
        {
            using var fixture = new Fixture();
            fixture.Apply(new Rect(60, 50, 180, 100), 28, .5);
            var moving = fixture.Draw(new Rect(60, 50, 180, 100), 28);
            var movingCount = CountShapes(moving);
            fixture.Rest();
            var resting = fixture.Draw(new Rect(60, 50, 180, 100), 28);
            Assert.InRange(movingCount, 1, 70);
            Assert.True(CountShapes(resting) > movingCount);
            var movingPixels = Pixels(moving); var restingPixels = Pixels(resting);
            // A shadow may never fill the glass interior, even with fewer contours.
            Assert.Equal(0, movingPixels[(100 * 300 + 130) * 4 + 3]);
            var maximumAlphaError = Enumerable.Range(0, 300 * 200)
                .Max(i => Math.Abs(movingPixels[i * 4 + 3] - restingPixels[i * 4 + 3]));
            Assert.InRange(maximumAlphaError, 0, 12);
        });
    }

    [Fact]
    public void RecordMorphShadowConstructionCost()
    {
        var output = Environment.GetEnvironmentVariable("GLASSDESK_MOTION_EVIDENCE");
        if (string.IsNullOrEmpty(output)) return;
        StaTest.Run(() =>
        {
            using var fixture = new Fixture();
            fixture.Apply(new Rect(60, 50, 180, 100), 28, .5);
            var samples = new List<double>(); long allocated = 0;
            DrawingGroup? drawing = null;
            for (var i = 0; i < 180; i++)
            {
                var panel = new Rect(60, 50, 180 + i % 60 * 8, 100 + i % 60 * 5);
                var before = GC.GetAllocatedBytesForCurrentThread(); var start = Stopwatch.GetTimestamp();
                drawing = fixture.Draw(panel, 28 + i % 60 * .4);
                var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                if (i >= 30) { samples.Add(elapsed); allocated += GC.GetAllocatedBytesForCurrentThread() - before; }
            }
            samples.Sort(); Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "shadow-cost.json"), JsonSerializer.Serialize(new
            {
                frames = samples.Count, p50Milliseconds = samples[samples.Count / 2],
                p95Milliseconds = samples[(int)(samples.Count * .95)],
                allocatedBytesPerFrame = allocated / samples.Count, shapes = CountShapes(drawing!),
            }, new JsonSerializerOptions { WriteIndented = true }));
            var last = fixture.Draw(new Rect(60, 50, 180, 100), 28);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(Render(last)));
            using var file = File.Create(Path.Combine(output, "moving-shadow.png")); encoder.Save(file);
        });
    }

    private static int CountShapes(Drawing drawing) => drawing is GeometryDrawing ? 1
        : drawing is DrawingGroup group ? group.Children.Sum(CountShapes) : 0;
    private static RenderTargetBitmap Render(Drawing drawing)
    {
        var visual = new DrawingVisual(); using (var dc = visual.RenderOpen()) dc.DrawDrawing(drawing);
        var bitmap = new RenderTargetBitmap(300, 200, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); return bitmap;
    }
    private static byte[] Pixels(Drawing drawing)
    {
        var pixels = new byte[300 * 200 * 4]; Render(drawing).CopyPixels(pixels, 300 * 4, 0); return pixels;
    }
    private static object Get(object owner, string field) => owner.GetType().GetField(field, Private)!.GetValue(owner)!;
    private static void Set(object owner, string field, object value) => owner.GetType().GetField(field, Private)!.SetValue(owner, value);

    private sealed class Fixture : IDisposable
    {
        internal LauncherWindow Window { get; } = new();
        private readonly FrameworkElement _drawing;
        internal Fixture()
        {
            _ = new WindowInteropHelper(Window).EnsureHandle();
            Set(Window, "_transformHostBounds", new PixelRect(0, 0, 1000, 800));
            var shadow = typeof(GlassWindow).GetField("_shadow", Private)!.GetValue(Window)!;
            _drawing = (FrameworkElement)Get(shadow, "_drawing");
        }
        internal void Apply(Rect bounds, double radius, double strength)
        {
            typeof(LauncherWindow).GetMethod("ApplyContainerTransformFrame", Private)!.Invoke(Window,
                [new ContainerMorphFrame(bounds, radius, strength, .8 * (1 - strength), .5, .98, .2)]);
            // A visible launcher selects compact squircle curves. Keep the benchmark
            // on that production path without showing an extra desktop window.
            _drawing.GetType().GetMethod("SetCompactShape", Private)!.Invoke(_drawing, [true]);
        }
        internal void Rest() => typeof(LauncherWindow).GetMethod("SetPanelRootLayout", Private)!.Invoke(Window,
            [new PixelRect(60, 50, 180, 100), new PixelRect(0, 0, 300, 200), 1d]);
        internal DrawingGroup Draw(Rect panel, double radius)
        {
            _drawing.GetType().GetMethod("SetGeometry", Private)!.Invoke(_drawing,
                [Window, panel, radius, new Size(1000, 800), 1d]);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen()) _drawing.GetType().GetMethod("OnRender", Private)!.Invoke(_drawing, [dc]);
            return (DrawingGroup)Get(_drawing, "_drawing");
        }
        public void Dispose() => Window.Close();
    }
}
