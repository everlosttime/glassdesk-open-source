using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using GlassDesk.Dock;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock.Tests;

public sealed class ContentIsolationTests
{
    [Theory]
    [InlineData(96d)]
    [InlineData(144d)]
    [InlineData(192d)]
    public void ProductionDockContentPixelsStayIdenticalAcrossAllGlassViews(double dpi)
    {
        StaTest.Run(() =>
        {
            var oldView = LiquidGlassDebugState.View;
            var oldSampling = LiquidGlassDebugState.SamplingParameters;
            var oldWeights = LiquidGlassDebugState.WeightParameters;
            var oldLod = LiquidGlassDebugState.LodParameters;
            var oldFinishing = LiquidGlassDebugState.FinishingParameters;
            var oldBrdf = LiquidGlassDebugState.BrdfParameters;
            var oldDispersion = LiquidGlassDebugState.DispersionParameters;
            var oldOverlay = LiquidGlassDebugState.ShowPerformanceOverlay;
            var dock = new DockWindow();
            try
            {
                var surface = (LiquidGlassSurface)dock.FindName("GlassSurface");
                var content = (StackPanel)dock.FindName("DockContentLayer");
                var host = (ContentControl)dock.FindName("TileStripHost");
                var grid = (Grid)VisualTreeHelper.GetParent(surface);
                var contentRoot = (Grid)dock.FindName("DockVisualRoot");
                Assert.Same(grid, VisualTreeHelper.GetParent(contentRoot));
                Assert.True(grid.Children.Contains(surface));
                Assert.True(grid.Children.IndexOf(surface) < grid.Children.IndexOf(contentRoot),
                    "The production glass background must be drawn before Dock Content.");
                Assert.False(surface.IsHitTestVisible);
                Assert.True(content.IsHitTestVisible);

                host.Content = CreateRepresentativeForeground();
                content.Measure(new Size(380, 96));
                content.Arrange(new Rect(0, 0, 380, 96));
                content.UpdateLayout();

                SetAllGlassEffects(false);
                surface.Visibility = Visibility.Collapsed;
                var offPixels = RenderContent(content, dpi);
                var offBounds = GetAlphaBounds(offPixels, PixelWidth(content, dpi), PixelHeight(content, dpi));
                var offEdgeEnergy = EdgeEnergy(offPixels, PixelWidth(content, dpi), PixelHeight(content, dpi));
                Assert.True(offBounds.PixelCount > 100, "The deterministic Content fixture must render visibly.");

                SetAllGlassEffects(true);
                surface.Visibility = Visibility.Visible;
                var onPixels = RenderContent(content, dpi);
                Assert.Equal(offPixels, onPixels);
                Assert.Equal(offBounds, GetAlphaBounds(onPixels, PixelWidth(content, dpi), PixelHeight(content, dpi)));
                Assert.Equal(offEdgeEnergy, EdgeEnergy(onPixels, PixelWidth(content, dpi), PixelHeight(content, dpi)));

                foreach (var view in Enum.GetValues<LiquidGlassDebugView>())
                {
                    LiquidGlassDebugState.View = view;
                    var viewPixels = RenderContent(content, dpi);
                    Assert.Equal(offPixels, viewPixels);
                }
            }
            finally
            {
                surfaceReset(dock);
                dock.Close();
                LiquidGlassDebugState.View = oldView;
                LiquidGlassDebugState.SamplingParameters = oldSampling;
                LiquidGlassDebugState.WeightParameters = oldWeights;
                LiquidGlassDebugState.LodParameters = oldLod;
                LiquidGlassDebugState.FinishingParameters = oldFinishing;
                LiquidGlassDebugState.BrdfParameters = oldBrdf;
                LiquidGlassDebugState.DispersionParameters = oldDispersion;
                LiquidGlassDebugState.ShowPerformanceOverlay = oldOverlay;
            }
        });
    }

    private static UIElement CreateRepresentativeForeground()
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Height = 62,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var icon = new Grid { Width = 42, Height = 42, Margin = new Thickness(0, 0, 6, 0) };
        icon.Children.Add(new PhoneBorder
        {
            Width = 42,
            Height = 42,
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromRgb(65, 126, 220)),
            Child = new Viewbox
            {
                Margin = new Thickness(9),
                Child = new Path
                {
                    Data = Geometry.Parse("M3,3 H21 V21 H3 Z M7,8 H17 M7,12 H17 M7,16 H14"),
                    Stroke = Brushes.White,
                    StrokeThickness = 1.5,
                },
            },
        });
        icon.Children.Add(new Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = Brushes.LimeGreen,
            Stroke = Brushes.White,
            StrokeThickness = 1,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
        });
        icon.Children.Add(new Border
        {
            Width = 15,
            Height = 15,
            CornerRadius = new CornerRadius(8),
            Background = Brushes.Crimson,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = "3",
                Foreground = Brushes.White,
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        });
        row.Children.Add(icon);
        row.Children.Add(new TextBlock
        {
            Text = "Files",
            Width = 42,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brushes.White,
            FontSize = 11,
        });
        row.Children.Add(new Button
        {
            Width = 30,
            Height = 30,
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(0),
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.White,
            Background = Brushes.Transparent,
            Foreground = Brushes.White,
            Content = "⋯",
        });
        return row;
    }

    private static void SetAllGlassEffects(bool enabled)
    {
        LiquidGlassDebugState.View = enabled ? LiquidGlassDebugView.None : LiquidGlassDebugView.DispersionOff;
        LiquidGlassDebugState.SamplingParameters = LiquidGlassSamplingParameters.Default;
        LiquidGlassDebugState.WeightParameters = LiquidGlassWeightParameters.Default;
        LiquidGlassDebugState.LodParameters = enabled
            ? LiquidGlassLodParameters.Default
            : new LiquidGlassLodParameters(0, 0, 0);
        LiquidGlassDebugState.FinishingParameters = enabled
            ? LiquidGlassFinishingParameters.Default
            : LiquidGlassFinishingParameters.Default with
            {
                TintOpacity = 0,
                AmbientShadowOpacity = 0,
                ContactShadowOpacity = 0,
            };
        LiquidGlassDebugState.BrdfParameters = enabled
            ? LiquidGlassBrdfParameters.Default
            : LiquidGlassBrdfParameters.Default with
            {
                SpecularStrength = 0,
                FresnelStrength = 0,
                LightIntensity = 0,
            };
        LiquidGlassDebugState.DispersionParameters = enabled
            ? LiquidGlassDispersionParameters.Default
            : LiquidGlassDispersionParameters.Default with { DispersionStrength = 0 };
        LiquidGlassDebugState.ShowPerformanceOverlay = false;
    }

    private static byte[] RenderContent(FrameworkElement content, double dpi)
    {
        var width = PixelWidth(content, dpi);
        var height = PixelHeight(content, dpi);
        var bitmap = new RenderTargetBitmap(width, height, dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

    private static int PixelWidth(FrameworkElement content, double dpi) =>
        Math.Max(1, (int)Math.Ceiling(content.ActualWidth * dpi / 96));

    private static int PixelHeight(FrameworkElement content, double dpi) =>
        Math.Max(1, (int)Math.Ceiling(content.ActualHeight * dpi / 96));

    private static AlphaBounds GetAlphaBounds(byte[] pixels, int width, int height)
    {
        var left = width;
        var top = height;
        var right = -1;
        var bottom = -1;
        var count = 0;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            if (pixels[(y * width + x) * 4 + 3] == 0) continue;
            left = Math.Min(left, x);
            top = Math.Min(top, y);
            right = Math.Max(right, x);
            bottom = Math.Max(bottom, y);
            count++;
        }
        return new AlphaBounds(left, top, right, bottom, count);
    }

    private static long EdgeEnergy(byte[] pixels, int width, int height)
    {
        long energy = 0;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var index = (y * width + x) * 4;
            if (x + 1 < width)
                for (var channel = 0; channel < 4; channel++)
                    energy += Math.Abs(pixels[index + channel] - pixels[index + 4 + channel]);
            if (y + 1 < height)
                for (var channel = 0; channel < 4; channel++)
                    energy += Math.Abs(pixels[index + channel] - pixels[index + width * 4 + channel]);
        }
        return energy;
    }

    private static void surfaceReset(DockWindow dock)
    {
        if (dock.FindName("GlassSurface") is LiquidGlassSurface surface)
        {
            surface.Visibility = Visibility.Visible;
            surface.Dispose();
        }
    }

    private readonly record struct AlphaBounds(int Left, int Top, int Right, int Bottom, int PixelCount);
}
