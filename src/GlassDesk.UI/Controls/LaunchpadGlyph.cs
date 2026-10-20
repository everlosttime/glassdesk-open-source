using System.Windows;
using System.Windows.Media;

namespace GlassDesk.UI.Controls;

/// <summary>The same nine-app glyph travels from the Dock into the expanding container.</summary>
public sealed class LaunchpadGlyph : FrameworkElement
{
    public static readonly DependencyProperty OutlineBrushProperty = DependencyProperty.Register(
        nameof(OutlineBrush), typeof(Brush), typeof(LaunchpadGlyph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsFilledProperty = DependencyProperty.Register(
        nameof(IsFilled), typeof(bool), typeof(LaunchpadGlyph),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public bool IsFilled
    {
        get => (bool)GetValue(IsFilledProperty);
        set => SetValue(IsFilledProperty, value);
    }

    /// <summary>Optional monochrome brush; group tiles retain their colorful glyph.</summary>
    public Brush? OutlineBrush
    {
        get => (Brush?)GetValue(OutlineBrushProperty);
        set => SetValue(OutlineBrushProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var dc = drawingContext;
        base.OnRender(dc);
        var unit = Math.Min(ActualWidth, ActualHeight) / 48;
        if (unit <= 0) return;
        if (OutlineBrush is { } outline)
        {
            var pen = IsFilled ? null : new Pen(outline, 3.5 * unit) { LineJoin = PenLineJoin.Round };
            for (var i = 0; i < 9; i++)
            {
                var inset = IsFilled ? 4 : 5.75;
                var size = IsFilled ? 12 : 8.5;
                var radius = (IsFilled ? 3.5 : 1.8) * unit;
                var box = new Rect((inset + i % 3 * 14) * unit, (inset + i / 3 * 14) * unit,
                    size * unit, size * unit);
                dc.DrawRoundedRectangle(IsFilled ? outline : null, pen, box, radius, radius);
            }
            return;
        }
        var colors = new[] { "#BEDBFF", "#C2EEE7", "#E6DCFF", "#D3E5FF", "#F1DCCA", "#C8E9EB", "#DCD9FA", "#BBDEEE", "#EADFEB" };
        for (var i = 0; i < 9; i++)
        {
            var box = new Rect((8 + i % 3 * 11) * unit, (8 + i / 3 * 11) * unit, 9 * unit, 9 * unit);
            dc.DrawGeometry(new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i])), null,
                SquircleGlassGeometry.Create(box, 3 * unit));
        }
    }
}
