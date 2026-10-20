using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Media.Imaging;
using GlassDesk.UI.Effects;

namespace GlassDesk.UI.Controls;

/// <summary>Fixed desktop texture; its lens reads the live shared silhouette every frame.</summary>
internal sealed class TransitionGlassLayer : FrameworkElement
{
    private BitmapSource? _backdrop;
    private TransitionGlassEffect? _lens;
    private readonly DrawingGroup _drawing = new();
    private bool _sleeping;
    private bool _frosted;
    private Rect _panel;
    private double _radius, _strength;
    public bool UsesGpuLens => Effect is TransitionGlassEffect;

    public TransitionGlassLayer()
    {
        IsHitTestVisible = false;
        Opacity = 0;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        if (RenderCapability.IsPixelShaderVersionSupported(3, 0))
        {
            _lens = new TransitionGlassEffect();
            Effect = _lens;
        }
    }

    public void SetBackdrop(BitmapSource? backdrop)
    {
        if (ReferenceEquals(backdrop, _backdrop)) return;
        _backdrop = backdrop;
        if (backdrop is null) _drawing.Children.Clear();
        InvalidateVisual();
    }

    public void SetPerformanceSleeping(bool sleeping)
    {
        _sleeping = sleeping;
        if (!sleeping) return;
        SetBackdrop(null);
        _drawing.Children.Clear();
        Effect = null;
        _lens = null;
    }

    public void SetPanel(Rect panel, Rect backdropBounds, double radius, double strength, bool launchpadOptics = false,
        bool frosted = false)
    {
        if (backdropBounds.Width <= 0 || backdropBounds.Height <= 0) return;
        _panel = new Rect(panel.X - backdropBounds.X, panel.Y - backdropBounds.Y, panel.Width, panel.Height);
        _radius = radius;
        _strength = strength;
        _frosted = frosted;
        if (_sleeping) return;
        if (frosted || GlassDesk.UI.BackdropRecovery.RequiresBasicRendering || !RenderCapability.IsPixelShaderVersionSupported(3, 0))
        {
            Effect = null;
            InvalidateVisual();
            return;
        }
        _lens ??= new TransitionGlassEffect();
        Effect = _lens;
        _lens.Panel = new Point4D((panel.X - backdropBounds.X) / backdropBounds.Width,
            (panel.Y - backdropBounds.Y) / backdropBounds.Height,
            panel.Width / backdropBounds.Width, panel.Height / backdropBounds.Height);
        _lens.Material = new Point4D(backdropBounds.Width, backdropBounds.Height, radius, strength);
        _lens.Profile = new Point4D(launchpadOptics ? 1 : 0, 0, 0, 0);
        var dispersion = LiquidGlassDebugState.DispersionParameters.Normalize();
        _lens.Dispersion = new Point4D(
            dispersion.DispersionStrength / LiquidGlassDispersionParameters.DefaultDispersionStrength,
            dispersion.InnerDispersionGain / LiquidGlassDispersionParameters.DefaultInnerDispersionGain,
            dispersion.OuterDispersionGain / LiquidGlassDispersionParameters.DefaultOuterDispersionGain, 0);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        using (var content = _drawing.Open()) DrawBackdrop(content);
        dc.DrawDrawing(_drawing);
    }

    private void DrawBackdrop(DrawingContext dc)
    {
        if (_frosted && _panel.Width > 0 && _panel.Height > 0)
            dc.PushClip(SquircleGlassGeometry.Create(_panel, _radius));
        if (_backdrop is not null) dc.DrawImage(_backdrop, new Rect(RenderSize));
        if (_backdrop is not null && !UsesGpuLens && _panel.Width > 0 && _panel.Height > 0)
        {
            var rim = new LinearGradientBrush(new GradientStopCollection
            {
                new(Color.FromArgb(100, 255, 255, 255), 0),
                new(Color.FromArgb(20, 255, 255, 255), .4),
                new(Color.FromArgb(75, 12, 17, 27), 1),
            }, new Point(0, 0), new Point(1, 1));
            dc.DrawGeometry(null, new Pen(rim, 1.5 * _strength), SquircleGlassGeometry.Create(_panel, _radius));
        }
        if (_frosted && _panel.Width > 0 && _panel.Height > 0) dc.Pop();
    }
}
