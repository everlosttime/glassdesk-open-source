using System.Windows;
using System.Windows.Media;
using GlassDesk.UI.Theming;

namespace GlassDesk.UI.Controls;

/// <summary>Rounded frosted glass sharing its owner's material, without optical effects or capture work.</summary>
public sealed class SharedGlassLens : FrameworkElement
{
    private LiquidGlassSurface? _source;
    public LiquidGlassSurface? Source
    {
        get => _source;
        set
        {
            if (_source is not null) _source.SharedMaterialChanged -= OnMaterialChanged;
            _source = value;
            if (IsLoaded && _source is not null) _source.SharedMaterialChanged += OnMaterialChanged;
            InvalidateVisual();
        }
    }
    public static readonly DependencyProperty HighlightProperty = DependencyProperty.Register(nameof(Highlight), typeof(double),
        typeof(SharedGlassLens), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Highlight { get => (double)GetValue(HighlightProperty); set => SetValue(HighlightProperty, value); }
    public double Radius { get; set; } = 24;

    public SharedGlassLens()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) => { if (_source is not null) _source.SharedMaterialChanged += OnMaterialChanged; };
        Unloaded += (_, _) => { if (_source is not null) _source.SharedMaterialChanged -= OnMaterialChanged; };
    }

    private void OnMaterialChanged(object? sender, EventArgs e) { if (IsVisible) InvalidateVisual(); }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var rect = new Rect(RenderSize);
        var shape = SquircleGlassGeometry.CreateCompact(rect, Radius);
        var dark = ThemeService.Current?.IsDark != false;
        var opaque = ThemeService.Current?.UseOpaquePanels == true || BackdropRecovery.RequiresBasicRendering;
        // Vector shadow rings avoid one bitmap blur layer per folder icon.
        for (var i = 4; i >= 1; i--)
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb((byte)(3 + i), 0, 0, 0)), i * 2), shape);
        dc.PushClip(shape);
        if (!opaque && _source is { IsLoaded: true })
        {
            var origin = TranslatePoint(new Point(), _source);
            _source.DrawSharedMaterial(dc, new Rect(origin, RenderSize));
        }
        // A neutral veil preserves the sampled backdrop's hue instead of adding a blue tint.
        dc.DrawRectangle(new SolidColorBrush(opaque
            ? ThemeService.Current?.Palette.FallbackPanelBackground
                ?? (dark ? Color.FromRgb(35, 35, 35) : Color.FromRgb(242, 242, 242))
            : dark ? Color.FromArgb(28, 0, 0, 0) : Color.FromArgb(36, 255, 255, 255)), null, rect);
        var sheen = new LinearGradientBrush(Color.FromArgb((byte)(18 + Math.Clamp(Highlight, 0, 1) * 18), 255, 255, 255),
            Color.FromArgb(2, 255, 255, 255), new Point(0, 0), new Point(.65, 1));
        dc.DrawRectangle(sheen, null, rect);
        dc.Pop();
        var edge = new LinearGradientBrush(new GradientStopCollection {
            new(Color.FromArgb((byte)(100 + Math.Clamp(Highlight, 0, 1) * 100),255,255,255),0),
            new(Color.FromArgb(18,255,255,255),.46), new(Color.FromArgb(65,255,255,255),1)
        }, new Point(0, 0), new Point(.8, 1));
        dc.DrawGeometry(null, new Pen(edge, .9 + Math.Clamp(Highlight, 0, 1) * .45), shape);
    }
}
