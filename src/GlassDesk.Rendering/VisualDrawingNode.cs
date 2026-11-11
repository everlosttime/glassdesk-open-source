using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace GlassDesk.Rendering;

/// <summary>
/// Detached Visual-level state. Keep this layer distinct from its own DrawingGroup:
/// WPF applies Visual clips/opacity masks with different intermediate bounds from
/// DrawingGroup clips/opacity masks. Children are in actual visual paint order.
/// </summary>
public sealed class VisualDrawingNode
{
    internal VisualDrawingNode() { }
    public DrawingGroup Drawing { get; internal init; }=null!;
    public IReadOnlyList<VisualDrawingNode> Children { get; internal init; }=Array.Empty<VisualDrawingNode>();
    public Matrix Transform { get; internal init; }=Matrix.Identity;
    public Vector Offset { get; internal init; }
    public double Opacity { get; internal init; }=1;
    public Geometry? Clip { get; internal init; }
    public Brush? OpacityMask { get; internal init; }
    public DropShadowEffect? DropShadow { get; internal init; }
    public DoubleCollection? XSnappingGuidelines { get; internal init; }
    public DoubleCollection? YSnappingGuidelines { get; internal init; }
    public Rect ContentBounds { get; internal init; }
    public Rect DescendantBounds { get; internal init; }
    public DpiScale Dpi { get; internal init; }
    public BitmapScalingMode BitmapScalingMode { get; internal init; }
    public EdgeMode EdgeMode { get; internal init; }
    public ClearTypeHint ClearTypeHint { get; internal init; }
    public TextFormattingMode TextFormattingMode { get; internal init; }
    public TextRenderingMode TextRenderingMode { get; internal init; }
    public TextHintingMode TextHintingMode { get; internal init; }
}
