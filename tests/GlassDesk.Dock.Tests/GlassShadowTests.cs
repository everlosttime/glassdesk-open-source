using System.Windows;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassDesk.Platform;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock.Tests;

public sealed class GlassShadowTests
{
    [Fact]
    public void Chapter08ZeroShadowAdditionsPreserveTheChapter07ShadowPixelsExactly()
    {
        StaTest.Run(() =>
        {
            var owner = new ShadowTestWindow();
            var noAdditions = LiquidGlassFinishingParameters.Default with
            {
                AmbientShadowOpacity = 0,
                ContactShadowOpacity = 0,
            };
            var actual = RenderShadow(owner, LiquidGlassDebugView.None, noAdditions);
            var chapter07 = RenderChapter07Baseline(owner);

            Assert.Equal(chapter07, actual);
        });
    }

    [Fact]
    public void Chapter08ShadowsRemainOutsideTheGlassAndCanBeComparedIndependently()
    {
        StaTest.Run(() =>
        {
            var owner = new ShadowTestWindow();
            var parameters = LiquidGlassFinishingParameters.Default with
            {
                AmbientShadowOpacity = .08,
                AmbientShadowExtentDip = 34,
                AmbientShadowOffsetDip = 0,
                ContactShadowOpacity = .06,
                ContactShadowExtentDip = 6,
                ContactShadowOffsetDip = 0,
            };
            var ambient = RenderShadow(owner, LiquidGlassDebugView.AmbientShadowOnly, parameters);
            var contact = RenderShadow(owner, LiquidGlassDebugView.ContactShadowOnly, parameters);
            var final = RenderShadow(owner, LiquidGlassDebugView.TintAndShadowFinal, parameters);
            var off = RenderShadow(owner, LiquidGlassDebugView.TintOff, parameters);
            var tintOnly = RenderShadow(owner, LiquidGlassDebugView.TintOnly, parameters);
            var zero = RenderShadow(owner, LiquidGlassDebugView.None, parameters with
            {
                AmbientShadowOpacity = 0,
                ContactShadowOpacity = 0,
            });

            byte Alpha(byte[] frame, int x, int y) => frame[(y * 300 + x) * 4 + 3];
            Assert.Equal((byte)0, Alpha(ambient, 130, 100));
            Assert.Equal((byte)0, Alpha(ambient, 62, 100));
            Assert.True(Alpha(ambient, 55, 100) > 0, $"Ambient near-edge alpha={Alpha(ambient, 55, 100)}");
            Assert.True(Alpha(ambient, 40, 100) > 0, $"Ambient far alpha={Alpha(ambient, 40, 100)}");
            Assert.True(Alpha(contact, 57, 100) > 0, $"Contact near-edge alpha={Alpha(contact, 57, 100)}");
            Assert.Equal((byte)0, Alpha(contact, 35, 100));
            Assert.True(Alpha(off, 57, 100) > 0);
            Assert.Equal(off, tintOnly);
            Assert.True(Alpha(final, 57, 100) >= Alpha(ambient, 57, 100));
            Assert.Equal(RenderChapter07Baseline(owner), zero);
        });
    }

    [Fact]
    public void SeparateShadowIsInputTransparentAndTracksOwnerVisibility()
    {
        StaTest.Run(() =>
        {
            var owner = new ShadowTestWindow { Width = 240, Height = 100, ShowActivated = false };
            using var shadow = new GlassShadow(owner);
            try
            {
                owner.Show();
                shadow.Update();
                Assert.True(shadow.IsVisible);
                Assert.True(WindowShadowStyles.IsInputTransparent(shadow.Handle));

                owner.Hide();
                shadow.Update();
                Assert.False(shadow.IsVisible);

                owner.Show();
                shadow.Update();
                Assert.True(shadow.IsVisible);
                owner.WindowState = WindowState.Minimized;
                shadow.Update();
                Assert.False(shadow.IsVisible);
            }
            finally
            {
                owner.Close();
            }

            Assert.Equal(nint.Zero, shadow.Handle);
            shadow.Update(); // Closing the owner must leave no callback that can reopen the shadow.
            Assert.False(shadow.IsVisible);
        });
    }

    private sealed class ShadowTestWindow : GlassWindow;

    private static byte[] RenderShadow(GlassWindow owner, LiquidGlassDebugView view,
        LiquidGlassFinishingParameters parameters)
    {
        var drawingType = typeof(GlassShadow).GetNestedType("ShadowDrawing", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("GlassShadow drawing layer was not found.");
        var drawing = (FrameworkElement)(Activator.CreateInstance(drawingType, nonPublic: true)
            ?? throw new InvalidOperationException("GlassShadow drawing layer could not be created."));
        drawingType.GetMethod("SetGeometry", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(drawing, [owner, new Rect(60, 50, 180, 100), 28d, new Size(300, 200), 1d]);
        drawingType.GetMethod("SetAppearance", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(drawing, [view, parameters]);
        drawing.Measure(new Size(300, 200));
        drawing.Arrange(new Rect(0, 0, 300, 200));
        var bitmap = new RenderTargetBitmap(300, 200, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var pixels = new byte[300 * 200 * 4];
        bitmap.CopyPixels(pixels, 300 * 4, 0);
        return pixels;
    }

    private static byte[] RenderChapter07Baseline(GlassWindow owner)
    {
        var panel = new Rect(60, 50, 180, 100);
        var radius = 28d;
        var size = new Size(300, 200);
        var visual = new System.Windows.Media.DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var outside = new CombinedGeometry(GeometryCombineMode.Exclude,
                new RectangleGeometry(new Rect(size)), owner.CreateGlassPanelGeometry(panel, radius));
            context.PushClip(outside);
            DrawLegacyShadow(context, owner, panel, radius, 34, 8, .15);
            DrawLegacyShadow(context, owner, panel, radius, 10, 2, .10);
            context.Pop();
        }
        var bitmap = new RenderTargetBitmap(300, 200, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[300 * 200 * 4];
        bitmap.CopyPixels(pixels, 300 * 4, 0);
        return pixels;
    }

    private static void DrawLegacyShadow(DrawingContext context, GlassWindow owner, Rect panel,
        double radius, double extent, double offset, double opacity)
    {
        const int steps = 32;
        for (var step = steps; step >= 1; step--)
        {
            var distance = extent * step / steps;
            var previous = extent * (step - 1) / steps;
            var alpha = opacity * (Math.Exp(-4.5 * previous * previous / (extent * extent))
                - Math.Exp(-4.5 * distance * distance / (extent * extent)));
            var shape = panel;
            shape.Offset(0, offset);
            shape.Inflate(distance, distance);
            var brush = new SolidColorBrush(Color.FromRgb(18, 24, 36)) { Opacity = alpha };
            context.DrawGeometry(brush, null, owner.CreateGlassPanelGeometry(shape, radius + distance));
        }
    }
}
