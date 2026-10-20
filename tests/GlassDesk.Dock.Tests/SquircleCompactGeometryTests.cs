using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock.Tests;

public sealed class SquircleCompactGeometryTests
{
    [Theory]
    [InlineData(3)]
    [InlineData(12)]
    [InlineData(32)]
    [InlineData(80)]
    [InlineData(128)]
    [InlineData(256)]
    [InlineData(1024)]
    public void CompactBoundaryStaysWithinOneTenthDipOfTheOriginalAndExactSuperellipse(double radius)
    {
        var bounds = new Rect(17.37, -28.23, radius * 2 + 93.4, radius * 2 + 17.8);
        var compact = CreateCompact(bounds, radius);
        var compactEdges = Edges(compact);
        var legacyEdges = Edges(SquircleGlassGeometry.Create(bounds, radius));

        foreach (var edge in compactEdges)
            Assert.InRange(Distance(edge.Start, legacyEdges), 0, .1);
        foreach (var edge in legacyEdges)
            Assert.InRange(Distance(edge.Start, compactEdges), 0, .1);

        // Independent analytic reference; sampling includes both axis endpoints.
        for (var corner = 0; corner < 4; corner++)
        for (var sample = 0; sample <= 512; sample++)
        {
            var theta = sample * Math.PI / 1024;
            var point = new Point(radius * Math.Pow(Math.Sin(theta), 2 / 2.6),
                -radius * Math.Pow(Math.Cos(theta), 2 / 2.6));
            point = corner switch
            {
                0 => new Point(bounds.Right - radius + point.X, bounds.Top + radius + point.Y),
                1 => new Point(bounds.Right - radius - point.Y, bounds.Bottom - radius + point.X),
                2 => new Point(bounds.Left + radius - point.X, bounds.Bottom - radius - point.Y),
                _ => new Point(bounds.Left + radius + point.Y, bounds.Top + radius - point.X),
            };
            var distance = Distance(point, compactEdges);
            Assert.True(distance <= .1, $"radius={radius}, corner={corner}, sample={sample}, distance={distance}");
        }
        Assert.True(compact.IsFrozen);
        Assert.InRange(Math.Abs(bounds.Left - compact.Bounds.Left), 0, .0001);
        Assert.InRange(Math.Abs(bounds.Top - compact.Bounds.Top), 0, .0001);
        Assert.InRange(Math.Abs(bounds.Right - compact.Bounds.Right), 0, .0001);
        Assert.InRange(Math.Abs(bounds.Bottom - compact.Bounds.Bottom), 0, .0001);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(32)]
    [InlineData(256)]
    public void CompactCornersUseAtMostEightCubicsAndKeepTangentsAndCurvatureContinuous(double radius)
    {
        var path = PathGeometry.CreateFromGeometry(CreateCompact(new Rect(0, 0, radius * 2 + 100, radius * 2 + 80), radius));
        var figure = Assert.Single(path.Figures);
        var cubics = Cubics(figure);
        Assert.InRange(cubics.Count, 4, 32);
        Assert.True(cubics.Count * 3 + 4 <= 100, "Animated contour control points must stay below the per-frame geometry budget.");
        Assert.All(cubics, curve => Assert.True(curve.End.X >= 0 && curve.End.Y >= 0));
        for (var i = 1; i < cubics.Count; i++)
        {
            var previous = cubics[i - 1];
            var current = cubics[i];
            if ((previous.End - current.Start).Length > 1e-8) continue; // A straight panel side separates corners.
            var incoming = previous.End - previous.Control2;
            var outgoing = current.Control1 - current.Start;
            Assert.True(Vector.Multiply(incoming, outgoing) > 0, "The contour must never reverse at a spline join.");
            Assert.InRange(Math.Abs(Vector.CrossProduct(incoming, outgoing)) / (incoming.Length * outgoing.Length), 0, 1e-8);
            Assert.InRange(Math.Abs(Curvature(previous, 1) - Curvature(current, 0)), 0, 1e-7);
        }
        // Exponent 2.6 has zero curvature where a corner meets the straight side.
        Assert.InRange(Math.Abs(Curvature(cubics[0], 0)), 0, 1e-8);
        Assert.InRange(Math.Abs(Curvature(cubics[7], 1)), 0, 1e-8);
    }

    [Fact]
    public void EmptyAndClampedCompactRadiiKeepTheExistingShapeContract()
    {
        Assert.True(CreateCompact(Rect.Empty, 20).IsEmpty());
        Assert.True(CreateCompact(new Rect(0, 0, 0, 50), 20).IsEmpty());
        var bounds = new Rect(3, 4, 180, 100);
        foreach (var radius in new[] { 0, -5, double.NaN, double.PositiveInfinity })
            Assert.Equal(SquircleGlassGeometry.Create(bounds, radius).GetArea(), CreateCompact(bounds, radius).GetArea());
        Assert.Equal(CreateCompact(bounds, 50).ToString(), CreateCompact(bounds, 500).ToString());
    }

    [Fact]
    public void AnimationShadowKeepsAll128LayersAndSubpixelAlphaParityWhileStableShadowUsesTheOriginalOutline()
    {
        StaTest.Run(() =>
        {
            var owner = new ShadowFixture();
            using var shadow = new GlassShadow(owner);
            try
            {
                var panel = new Rect(0, 0, 280, 180); // Transition bounds are owner-local; shadow padding is added by production.
                SetField(shadow, "_ownerScale", 1d);
                SetField(shadow, "_shadowScale", 1d);
                SetField(shadow, "_shadowSize", new Size(400, 300));
                SetField(shadow, "_ownerPixelWidth", 280);
                SetField(shadow, "_ownerPixelHeight", 180);
                SetField(shadow, "_paddingLeftPixels", 60);
                SetField(shadow, "_paddingTopPixels", 50);
                var drawing = (FrameworkElement)GetField(shadow, "_drawing")!;
                drawing.GetType().GetMethod("SetAppearance", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(drawing, [LiquidGlassDebugView.None, LiquidGlassFinishingParameters.Default]);
                var stablePixels = RenderShadow(shadow, drawing);
                var stable = (DrawingGroup)GetField(drawing, "_drawing")!;
                var stableLayers = Layers(stable).ToArray();
                Assert.Equal(128, stableLayers.Length);
                var stableFirst = stableLayers[0];
                Assert.Empty(Cubics(PathGeometry.CreateFromGeometry(stableFirst.Geometry).Figures[0]));

                SetField(shadow, "_transitionPanel", panel);
                SetField(shadow, "_transitionRadius", 80d);
                var animatedPixels = RenderShadow(shadow, drawing);
                var animated = (DrawingGroup)GetField(drawing, "_drawing")!;
                var animatedLayers = Layers(animated).ToArray();
                Assert.Equal(128, animatedLayers.Length);
                Assert.All(animatedLayers, layer =>
                    Assert.InRange(Cubics(PathGeometry.CreateFromGeometry(layer.Geometry).Figures[0]).Count, 4, 32));

                long totalDifference = 0, totalAlpha = 0;
                var maximum = 0;
                var maximumIndex = 0;
                for (var i = 3; i < animatedPixels.Length; i += 4)
                {
                    var difference = Math.Abs(stablePixels[i] - animatedPixels[i]);
                    if (difference > maximum) { maximum = difference; maximumIndex = i; }
                    totalDifference += difference;
                    totalAlpha += stablePixels[i];
                }
                // WPF rasterizes cubics and lines with different AA coverage at a few
                // edge pixels: permit <5% absolute alpha while retaining >99% of the
                // integrated shadow. Spatial parity is tested above at 0.1 DIP.
                Assert.True(maximum <= 12, $"Maximum alpha difference={maximum} at ({maximumIndex / 4 % 400},{maximumIndex / 4 / 400}), stable={stablePixels[maximumIndex]}, compact={animatedPixels[maximumIndex]}, relative alpha difference={totalDifference / (double)totalAlpha}");
                Assert.True(totalDifference <= totalAlpha * .01, $"Relative alpha difference={totalDifference / (double)totalAlpha}");
                Assert.Equal((byte)0, animatedPixels[(140 * 400 + 200) * 4 + 3]);

                SetField(shadow, "_transitionPanel", null);
                Assert.Equal(stablePixels, RenderShadow(shadow, drawing));
            }
            finally { owner.Close(); }
        });
    }

    private static Geometry CreateCompact(Rect bounds, double radius)
    {
        var method = typeof(SquircleGlassGeometry).GetMethod("CreateCompact", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        return Assert.IsAssignableFrom<Geometry>(method.Invoke(null, [bounds, radius]));
    }

    private static List<(Point Start, Point End)> Edges(Geometry geometry)
    {
        var result = new List<(Point, Point)>();
        foreach (var figure in geometry.GetFlattenedPathGeometry(.001, ToleranceType.Absolute).Figures)
        {
            var previous = figure.StartPoint;
            foreach (var segment in figure.Segments)
            {
                var points = segment switch { LineSegment line => new[] { line.Point }, PolyLineSegment lines => lines.Points.ToArray(), _ => [] };
                foreach (var point in points) { result.Add((previous, point)); previous = point; }
            }
            if (figure.IsClosed) result.Add((previous, figure.StartPoint));
        }
        return result;
    }

    private static double Distance(Point point, List<(Point Start, Point End)> edges)
    {
        var minimum = double.PositiveInfinity;
        foreach (var edge in edges)
        {
            var delta = edge.End - edge.Start;
            var t = delta.LengthSquared == 0 ? 0 : Math.Clamp(Vector.Multiply(point - edge.Start, delta) / delta.LengthSquared, 0, 1);
            minimum = Math.Min(minimum, (point - (edge.Start + t * delta)).Length);
        }
        return minimum;
    }

    private static List<(Point Start, Point Control1, Point Control2, Point End)> Cubics(PathFigure figure)
    {
        var result = new List<(Point, Point, Point, Point)>();
        var previous = figure.StartPoint;
        foreach (var segment in figure.Segments)
        {
            if (segment is BezierSegment cubic)
            {
                result.Add((previous, cubic.Point1, cubic.Point2, cubic.Point3)); previous = cubic.Point3;
            }
            else if (segment is PolyBezierSegment cubics)
            {
                for (var i = 0; i < cubics.Points.Count; i += 3)
                {
                    result.Add((previous, cubics.Points[i], cubics.Points[i + 1], cubics.Points[i + 2])); previous = cubics.Points[i + 2];
                }
            }
            else if (segment is LineSegment line) previous = line.Point;
            else if (segment is PolyLineSegment lines) previous = lines.Points[^1];
        }
        return result;
    }

    private static double Curvature((Point Start, Point Control1, Point Control2, Point End) curve, double t)
    {
        var first = t == 0 ? 3 * (curve.Control1 - curve.Start) : 3 * (curve.End - curve.Control2);
        var second = t == 0 ? 6 * ((curve.Control2 - curve.Control1) - (curve.Control1 - curve.Start))
            : 6 * ((curve.End - curve.Control2) - (curve.Control2 - curve.Control1));
        return Vector.CrossProduct(first, second) / Math.Pow(first.Length, 3);
    }

    private static IEnumerable<GeometryDrawing> Layers(Drawing drawing)
    {
        if (drawing is GeometryDrawing layer) yield return layer;
        else if (drawing is DrawingGroup group)
            foreach (var child in group.Children)
            foreach (var childLayer in Layers(child)) yield return childLayer;
    }

    private static byte[] RenderShadow(GlassShadow shadow, FrameworkElement drawing)
    {
        typeof(GlassShadow).GetMethod("UpdatePanelGeometry", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(shadow, null);
        drawing.Measure(new Size(400, 300)); drawing.Arrange(new Rect(0, 0, 400, 300));
        var bitmap = new RenderTargetBitmap(400, 300, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var pixels = new byte[400 * 300 * 4]; bitmap.CopyPixels(pixels, 400 * 4, 0);
        return pixels;
    }

    private static object? GetField(object instance, string name) => instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance);
    private static void SetField(object instance, string name, object? value) => instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);
    private sealed class ShadowFixture : GlassWindow
    {
        public ShadowFixture() => PanelCornerRadius = new CornerRadius(80);
    }
}
