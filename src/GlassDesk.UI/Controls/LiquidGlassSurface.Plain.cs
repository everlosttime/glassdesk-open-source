using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using GlassDesk.Core.Models;
using GlassDesk.UI.Theming;

namespace GlassDesk.UI.Controls;

public sealed partial class LiquidGlassSurface
{
    private bool _plainGlassMaterialSuspended;
    private SolidColorBrush? _plainGlassBrush;
    internal bool IsPlainGlass => SelectedMaterial == GlassMaterialMode.PlainGlass;

    private void SuspendPlainGlassMaterial()
    {
        ReleaseNativeStatic();
        if (_plainGlassMaterialSuspended) return;
        _plainGlassMaterialSuspended = true;
        RetireConcealedMaterial();
        _containerAnimationActive = false;
        _background.SetPerformanceSleeping(true);
        _transitionLayer.SetPerformanceSleeping(true);
        _compatibilityLayer.SetPerformanceSleeping(true);
        _optics = new(); _androidGlass = new();
        _opticalFrameTimestamps.Clear(); _opticalRefreshRate = 0;
        _backdropScene = null;
    }

    internal static Color PlainGlassColor(GlassMaterialTarget target)
    {
        var theme = ThemeService.Current;
        // Panel-local frosted overrides must not re-enable effects or replace the user's alpha.
        var transparency = ResolveTransparency(target);
        var alpha = theme?.UseOpaquePanels == true ? (byte)255
            : (byte)Math.Round(255 * (1 - Math.Clamp(transparency, 0, 1)));
        return theme?.IsDark != false ? Color.FromArgb(alpha, 28, 29, 34) : Color.FromArgb(alpha, 246, 247, 250);
    }

    private void RenderPlainGlass(DrawingContext dc)
    {
        SuspendPlainGlassMaterial();
        base.OnRender(dc);
        if (ActualWidth < 1 || ActualHeight < 1) return;
        var bounds = _panelShape ?? new Rect(RenderSize);
        var owner = Window.GetWindow(this) as GlassWindow;
        var radius = _panelShape is null ? owner?.PanelCornerRadius.TopLeft ?? 40 : _panelRadius;
        var color = PlainGlassColor(TransparencyTarget);
        // Windows skips alpha-zero pixels when hit-testing a layered window.
        // Keep the rounded bar hittable even when its material is fully transparent.
        if (color.A == 0 && MaterialTarget is GlassMaterialTarget.Dock or GlassMaterialTarget.StatusBar)
            color = Color.FromArgb(1, 0, 0, 0);
        if (_plainGlassBrush?.Color != color)
        {
            _plainGlassBrush = new SolidColorBrush(color);
            _plainGlassBrush.Freeze();
        }
        var shape = _panelGeometry ?? owner?.CreateGlassPanelGeometry(bounds, radius)
            ?? SquircleGlassGeometry.Create(bounds, radius);
        dc.DrawGeometry(_plainGlassBrush, null, shape);
        _lastRenderTicks = Stopwatch.GetTimestamp(); ++_renderCount;
        _lastRenderMaterialReady = true;
        _firstVisibleRenderMaterialReady ??= true;
        _firstVisibleMaterialSource ??= "PlainGlass";
        SharedMaterialChanged?.Invoke(this, EventArgs.Empty);
    }
}
