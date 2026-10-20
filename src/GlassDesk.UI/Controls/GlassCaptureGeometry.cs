using System.Windows;
using GlassDesk.Platform.Displays;

namespace GlassDesk.UI.Controls;

internal readonly record struct GlassCaptureGeometry(Size SampleSizeDip, Rect GlassBoundsDip, Rect FrameBoundsDip)
{
    internal static GlassCaptureGeometry Create(PixelRect requestedRegion, PixelRect clippedRegion,
        Point panelScreenOrigin, Rect panel, double dpiScale, bool expanded)
    {
        // Clipping restricts the physical capture, not the optical coordinate domain.
        // A sliding panel must retain its complete silhouette even at the Primary edge.
        var region = requestedRegion;
        var size = expanded ? new Size(region.Width / dpiScale, region.Height / dpiScale) : panel.Size;
        var glass = expanded
            ? new Rect((panelScreenOrigin.X - region.X) / dpiScale,
                (panelScreenOrigin.Y - region.Y) / dpiScale, panel.Width, panel.Height)
            : new Rect(panel.Size);
        var frame = expanded
            ? new Rect(panel.X + (region.X - panelScreenOrigin.X) / dpiScale,
                panel.Y + (region.Y - panelScreenOrigin.Y) / dpiScale, size.Width, size.Height)
            : panel;
        return new(size, glass, frame);
    }
}
