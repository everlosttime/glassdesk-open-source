using System.Windows;
using System.Windows.Media.Imaging;
using GlassDesk.Core.Models;
using GlassDesk.UI.Theming;

namespace GlassDesk.UI.Controls;

internal readonly record struct PreparedTransitionLayer(BitmapSource Bitmap, Rect Bounds, double Opacity, long Revision);
internal readonly record struct PreparedTransitionFrame(PreparedTransitionLayer Primary, PreparedTransitionLayer? Overlay, bool Frosted);

public sealed partial class LiquidGlassSurface
{
    private long _transitionBackdropRevision;
    internal bool HasReadyPaintAfter(long requestedAt) => _lastRenderTicks >= requestedAt && _lastRenderMaterialReady;
    internal object InspectPreparedTransition() => new { material = Material.ToString(), bitmap = _transitionBackdrop is not null,
        transitionOpacity = _transitionLayer.Opacity, compatibilityOpacity = _compatibilityLayer.Opacity,
        compatibilityBitmap = _compatibilityBackdrop is not null, _compatibilityIsDesktopSnapshot,
        basic = _background.RequiresBasicBackground, sleeping = _performanceSleeping,
        opaque = ThemeService.Current?.UseOpaquePanels, debug = LiquidGlassDebugState.View.ToString() };

    internal bool TryGetPreparedTransitionMaterial(out PreparedTransitionFrame frame)
    {
        frame = default;
        // Hidden reveal material has already passed PrepareRevealMaterial's
        // scene/freshness boundary. Match the production paint check when visible.
        if (Window.GetWindow(this)?.IsVisible == true && NeedsPaintSceneVerification()) EnsureBackdropScene();
        UpdateTransitionLayer();
        if (_performanceSleeping || _background.RequiresBasicBackground || BackdropRecovery.RequiresBasicRendering
            || ThemeService.Current?.UseOpaquePanels == true || LiquidGlassDebugState.View != LiquidGlassDebugView.None
            || Material is not (GlassMaterialMode.AndroidLiquidGlass or GlassMaterialMode.FrostedGlass)) return false;
        PreparedTransitionLayer? primary = _transitionLayer.Opacity > 0 && _transitionBackdrop is not null
            ? new(_transitionBackdrop, _transitionBackdropBounds, _transitionLayer.Opacity, _transitionBackdropRevision) : null;
        PreparedTransitionLayer? overlay = _compatibilityLayer.Opacity > 0 && _compatibilityBackdrop is not null
            ? new(_compatibilityBackdrop, _compatibilityBounds, _compatibilityLayer.Opacity, 0) : null;
        if (primary is null)
        {
            if (!_compatibilityIsDesktopSnapshot || overlay is null) return false;
            primary = overlay; overlay = null;
        }
        frame = new(primary.Value, overlay, Material == GlassMaterialMode.FrostedGlass);
        return true;
    }
}
