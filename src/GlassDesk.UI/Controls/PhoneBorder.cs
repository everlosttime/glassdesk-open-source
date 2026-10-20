using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GlassDesk.UI.Controls;

/// <summary>Draws a framed control with the same continuous corner as the glass windows.</summary>
public sealed class PhoneBorder : Border
{
    protected override void OnRender(DrawingContext dc)
    {
        var bounds = new Rect(RenderSize);
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return;

        var radius = ResolveRadius();
        dc.DrawGeometry(Background, null, SquircleGlassGeometry.Create(bounds, radius));

        var thickness = BorderThickness;
        var stroke = Math.Max(Math.Max(thickness.Left, thickness.Top),
            Math.Max(thickness.Right, thickness.Bottom));
        if (BorderBrush is null || stroke <= 0 || bounds.Width <= stroke || bounds.Height <= stroke) return;

        var inset = stroke / 2;
        bounds.Inflate(-inset, -inset);
        dc.DrawGeometry(null, new Pen(BorderBrush, stroke),
            SquircleGlassGeometry.Create(bounds, Math.Max(0, radius - inset)));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var arranged = base.ArrangeOverride(finalSize);
        UpdateClip(arranged);
        return arranged;
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == CornerRadiusProperty) UpdateClip(RenderSize);
    }

    private void UpdateClip(Size size)
    {
        if (size.Width <= 0 || size.Height <= 0) return;
        Clip = SquircleGlassGeometry.Create(new Rect(size), ResolveRadius());
    }

    private double ResolveRadius()
    {
        var corners = CornerRadius;
        return Math.Max(Math.Max(corners.TopLeft, corners.TopRight),
            Math.Max(corners.BottomLeft, corners.BottomRight));
    }
}
