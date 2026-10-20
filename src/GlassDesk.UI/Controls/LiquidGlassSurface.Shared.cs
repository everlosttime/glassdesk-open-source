using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GlassDesk.UI.Controls;

public sealed partial class LiquidGlassSurface
{
    internal event EventHandler? SharedMaterialChanged;

    /// <summary>Draw an existing live material frame, without a capture or bitmap allocation.</summary>
    internal void DrawSharedMaterial(DrawingContext dc, Rect region)
    {
        BitmapSource? frame = null;
        Rect bounds = default;
        if (_transitionBackdrop is not null && _transitionBackdropScene?.Equals(_backdropScene) == true)
        { frame = _transitionBackdrop; bounds = _transitionBackdropBounds; }
        else if (_frame is not null && _frameScene?.Equals(_backdropScene) == true)
        { frame = _frame; bounds = _sampledPanelBounds; }
        else if (_compatibilityBackdrop is not null && _compatibilityScene?.Equals(_backdropScene) == true)
        { frame = _compatibilityBackdrop; bounds = _compatibilityBounds; }
        if (frame is null || bounds.Width <= 0 || bounds.Height <= 0) return;
        bounds.Offset(-region.X, -region.Y);
        dc.DrawImage(frame, bounds);
    }
}
