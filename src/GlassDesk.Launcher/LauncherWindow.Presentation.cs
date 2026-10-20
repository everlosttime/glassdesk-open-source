using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GlassDesk.UI.Controls;

namespace GlassDesk.Launcher;

public partial class LauncherWindow
{
    private void ApplyContentReveal(double opacity)
    {
        // A single opacity/transform update keeps the final grid layout and text
        // metrics stable. The much larger glass morph never scales these visuals.
        opacity = Math.Clamp(opacity, 0, 1);
        LauncherContent.Opacity = opacity;
        ContentScaleTransform.ScaleX = ContentScaleTransform.ScaleY = .96 + .04 * opacity;
    }
}
