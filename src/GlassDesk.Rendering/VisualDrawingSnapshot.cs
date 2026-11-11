using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace GlassDesk.Rendering;

public sealed class VisualSnapshotOptions
{
    public int MaximumVisuals { get; init; }=2048;
    public int MaximumDrawings { get; init; }=16384;
    public int MaximumDepth { get; init; }=128;
    public int MaximumGlyphs { get; init; }=65536;
    public long MaximumBitmapBytes { get; init; }=32*1024*1024;
    public Predicate<Visual>? Exclude { get; init; }
    /// <summary>Dedicated, unanimated presentation wrapper only. Capture its
    /// source opacity as one without changing any application visual property.</summary>
    public Visual? PresentationOpacityCarrier { get; init; }
}

/// <summary>
/// Detached retained drawings in the root visual's parent coordinates. Includes
/// visual offsets/transforms, clips, opacity groups, snapping and child paint
/// order. This is source data for a renderer, not a native-rendering success or
/// a pixel bitmap. No Visual, Window, callback or live visual brush is retained.
/// Capture on the owning dispatcher after layout/render has settled. Finalized
/// WPF GlyphRuns retain their original measuring mode and must be treated as
/// immutable resources; native compilation copies their arrays at upload.
/// </summary>
public sealed class VisualDrawingSnapshot
{
    public VisualDrawingNode Root { get; }
    public int VisualCount { get; }
    public int DrawingCount { get; }
    public int GlyphCount { get; }
    // Logical immutable bitmap sources only; not process/GPU memory accounting.
    public long BitmapSourceBytes { get; }

    private VisualDrawingSnapshot(VisualDrawingNode root,Builder builder)
    {Root=root;VisualCount=builder.Visuals;DrawingCount=builder.Drawings;GlyphCount=builder.Glyphs;BitmapSourceBytes=builder.BitmapBytes;}

    public static VisualDrawingSnapshot Capture(Visual root,VisualSnapshotOptions? options=null)
    {
        ArgumentNullException.ThrowIfNull(root);root.VerifyAccess();options??=new();
        ValidateOptions(options);return CaptureCore(root,options,null);
    }
    internal static void ValidateOptions(VisualSnapshotOptions options)
    {
        if(options.MaximumVisuals is <1 or >16384 || options.MaximumDrawings is <1 or >65536
            || options.MaximumDepth is <1 or >256 || options.MaximumGlyphs is <1 or >1048576
            || options.MaximumBitmapBytes is <1 or >128*1024*1024)throw new ArgumentOutOfRangeException(nameof(options));
    }
    internal static VisualDrawingSnapshot CaptureRetained(Visual root,VisualSnapshotOptions options,RetainedVisualDrawingSource.DrawingCache cache)=>CaptureCore(root,options,cache);
    private static VisualDrawingSnapshot CaptureCore(Visual root,VisualSnapshotOptions options,RetainedVisualDrawingSource.DrawingCache? cache)
    {
        var builder=new Builder(options,cache);var empty=new DrawingGroup();empty.Freeze();
        return new(builder.Visual(root,0)??new VisualDrawingNode{Drawing=empty},builder);
    }

    private sealed class Builder(VisualSnapshotOptions options,RetainedVisualDrawingSource.DrawingCache? cache)
    {
        internal int Visuals,Drawings,Glyphs;internal long BitmapBytes;
        private readonly HashSet<BitmapSource> _bitmaps=new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<object> _active=new(ReferenceEqualityComparer.Instance);

        internal VisualDrawingNode? Visual(Visual visual,int depth)
        {
            Depth(depth);if(options.Exclude?.Invoke(visual)==true)return null;
            if(++Visuals>options.MaximumVisuals)throw Unsupported("Visual count exceeds the snapshot budget.");
            var effect=VisualTreeHelper.GetEffect(visual);
            if(effect is not null and not DropShadowEffect || VisualTreeHelper.GetCacheMode(visual) is not null)
                throw Unsupported("Unsupported visual effects and cache modes require the existing renderer.");
            if(visual is UIElement {Visibility:not Visibility.Visible})return null;
            DrawingGroup drawing;Rect contentBounds;
            if(cache is not null && cache.TryGet(visual,out var cached))
            {drawing=cached!.Drawing;if(cached.HasDrawing)CountFrozen(drawing,depth+1);contentBounds=cached.Bounds;}
            else
            {
                cache?.Capturing();var own=VisualTreeHelper.GetDrawing(visual);
                drawing=own is not null?(DrawingGroup)Clone(own,depth+1):new DrawingGroup();Freeze(drawing);
                contentBounds=cache is not null?cache.ContentBounds(visual):VisualTreeHelper.GetContentBounds(visual);
                cache?.Store(visual,own,drawing,contentBounds);
            }
            var children=new List<VisualDrawingNode>();var childCount=VisualTreeHelper.GetChildrenCount(visual);
            // GetChild is the actual visual paint order (including Panel.ZIndex),
            // not the logical collection order or a second magnification layout.
            for(var i=0;i<childCount;i++)
            {
                if(VisualTreeHelper.GetChild(visual,i) is not Visual child)throw Unsupported("3D visual content requires the existing renderer.");
                var copied=Visual(child,depth+1);if(copied is not null)children.Add(copied);
            }
            return new()
            {
                Drawing=drawing,Children=children.AsReadOnly(),Transform=VisualTreeHelper.GetTransform(visual)?.Value??Matrix.Identity,
                Offset=VisualTreeHelper.GetOffset(visual),Opacity=ReferenceEquals(visual,options.PresentationOpacityCarrier)?1:VisualTreeHelper.GetOpacity(visual),
                Clip=Freeze(CopyValue(VisualTreeHelper.GetClip(visual))),OpacityMask=Freeze(Brush(VisualTreeHelper.GetOpacityMask(visual),depth+1)),
                DropShadow=Freeze(CopyValue(effect as DropShadowEffect)),
                XSnappingGuidelines=Freeze(CopyValue(VisualTreeHelper.GetXSnappingGuidelines(visual))),
                YSnappingGuidelines=Freeze(CopyValue(VisualTreeHelper.GetYSnappingGuidelines(visual))),
                ContentBounds=contentBounds,DescendantBounds=cache is not null?cache.DescendantBounds(visual,childCount,contentBounds):VisualTreeHelper.GetDescendantBounds(visual),Dpi=VisualTreeHelper.GetDpi(visual),
                BitmapScalingMode=RenderOptions.GetBitmapScalingMode(visual),EdgeMode=VisualTreeHelper.GetEdgeMode(visual),
                ClearTypeHint=RenderOptions.GetClearTypeHint(visual),TextFormattingMode=TextOptions.GetTextFormattingMode(visual),
                TextRenderingMode=TextOptions.GetTextRenderingMode(visual),TextHintingMode=TextOptions.GetTextHintingMode(visual)
            };
        }
        // A frozen Freezable has no mutable inheritance context. Reuse its
        // value graph while still walking drawings/brushes for budgets and
        // unsupported live resources; mutable values are always detached.
        private static T? CopyValue<T>(T? source) where T:Freezable
        {if(source is null || source.IsFrozen)return source;return (T)source.CloneCurrentValue();}
        private static T? Freeze<T>(T? resource) where T:Freezable
        {if(resource is null)return null;if(!resource.CanFreeze)throw Unsupported("The foreground contains a live drawing resource.");resource.Freeze();return resource;}

        private Drawing Clone(Drawing source,int depth)
        {
            Depth(depth);if(++Drawings>options.MaximumDrawings)throw Unsupported("Drawing count exceeds the snapshot budget.");
            Enter(source);
            try
            {
                switch(source)
                {
                    case DrawingGroup group:
                        if(group.BitmapEffect is not null)throw Unsupported("Drawing effects require the existing renderer.");
                        var copy=new DrawingGroup{Transform=CopyValue(group.Transform),Opacity=group.Opacity,
                            ClipGeometry=CopyValue(group.ClipGeometry),OpacityMask=Brush(group.OpacityMask,depth+1),
                            GuidelineSet=CopyValue(group.GuidelineSet)};
                        CopyOptions(group,copy);
                        foreach(var item in group.Children)copy.Children.Add(Clone(item,depth+1));return copy;
                    case GeometryDrawing geometry:
                        Pen? pen=null;
                        if(geometry.Pen is {} original)
                        {
                            var brush=Brush(original.Brush,depth+1);
                            if(original.IsFrozen && ReferenceEquals(brush,original.Brush))pen=original;
                            else{pen=original.CloneCurrentValue();pen.Brush=brush;}
                        }
                        return new GeometryDrawing(Brush(geometry.Brush,depth+1),pen,CopyValue(geometry.Geometry));
                    case GlyphRunDrawing glyph:
                        return new GlyphRunDrawing(Brush(glyph.ForegroundBrush,depth+1),CopyGlyph(glyph.GlyphRun));
                    case ImageDrawing image:
                        return new ImageDrawing(Image(image.ImageSource),image.Rect);
                    default:throw Unsupported("Video or unknown drawings require the existing renderer.");
                }
            }
            finally{_active.Remove(source);}
        }

        private void CountFrozen(Drawing drawing,int depth)
        {
            Depth(depth);if(++Drawings>options.MaximumDrawings)throw Unsupported("Drawing count exceeds the snapshot budget.");
            switch(drawing)
            {
                case DrawingGroup group:
                    if(group.BitmapEffect is not null)throw Unsupported("Drawing effects require the existing renderer.");
                    CountBrush(group.OpacityMask,depth+1);foreach(var child in group.Children)CountFrozen(child,depth+1);break;
                case GeometryDrawing geometry:CountBrush(geometry.Pen?.Brush,depth+1);CountBrush(geometry.Brush,depth+1);break;
                case GlyphRunDrawing glyph:CountBrush(glyph.ForegroundBrush,depth+1);CopyGlyph(glyph.GlyphRun);break;
                case ImageDrawing image:Image(image.ImageSource);break;
                default:throw Unsupported("Video or unknown drawings require the existing renderer.");
            }
        }
        private void CountBrush(Brush? brush,int depth)
        {
            if(brush is null)return;Depth(depth);
            if(brush is DrawingBrush drawing && drawing.Drawing is {} content)CountFrozen(content,depth+1);
            else if(brush is ImageBrush image)Image(image.ImageSource);
            else if(brush is VisualBrush or BitmapCacheBrush)throw Unsupported("Live visual brushes require the existing renderer.");
        }

        private GlyphRun? CopyGlyph(GlyphRun? run)
        {
            if(run is null)return null;
            if(run.GlyphIndices.Count>4096 || (Glyphs+=run.GlyphIndices.Count)>options.MaximumGlyphs)
                throw Unsupported("Glyph count exceeds the snapshot budget.");
            // A finalized WPF GlyphRun is a retained drawing resource. Rebuilding
            // it through the public constructor loses its internal Display/Ideal
            // measuring mode (real WGC regression: max 115/255). Like WPF's own
            // Drawing.CloneCurrentValue, retain this layout resource; never mutate
            // its arrays. NativeDirectPresenter.SetGlyphRun snapshots those arrays
            // when compiling a native resource, and takes explicit formatting mode.
            return run;
        }

        private ImageSource? Image(ImageSource? source)
        {
            if(source is null)return null;
            if(source is not BitmapSource bitmap || !bitmap.IsFrozen)
                throw Unsupported("Only immutable bitmap image sources can enter a detached snapshot.");
            if(_bitmaps.Add(bitmap))
            {
                var bytes=checked((long)bitmap.PixelWidth*bitmap.PixelHeight*Math.Max(1,(bitmap.Format.BitsPerPixel+7)/8));
                if(bytes>options.MaximumBitmapBytes-BitmapBytes)throw Unsupported("Bitmap sources exceed the snapshot budget.");
                BitmapBytes+=bytes;
            }
            return bitmap; // Immutable source asset; never CopyPixels or rasterize a Visual.
        }
        private Brush? Brush(Brush? source,int depth)
        {
            if(source is null)return null;Depth(depth);Enter(source);
            try
            {
                if(source.IsFrozen && source is SolidColorBrush or LinearGradientBrush or RadialGradientBrush)return source;
                if(source is VisualBrush or BitmapCacheBrush)throw Unsupported("Live visual brushes require the existing renderer.");
                Brush copy;
                // Walk drawing brushes before cloning. Cloning their entire
                // source graph first would defeat depth/count/cycle limits.
                if(source is DrawingBrush drawing)
                {
                    var tile=new DrawingBrush(drawing.Drawing is null?null:Clone(drawing.Drawing,depth+1));
                    CopyTile(drawing,tile);copy=tile;
                }
                else if(source is ImageBrush image)
                {
                    var tile=new ImageBrush(Image(image.ImageSource));CopyTile(image,tile);copy=tile;
                }
                else copy=source.CloneCurrentValue();
                if(!copy.CanFreeze)throw Unsupported("The brush cannot be detached from its source.");
                return copy;
            }
            finally{_active.Remove(source);}
        }
        private static void CopyOptions(DependencyObject source,DependencyObject target)
        {
            // Inner DrawingGroups must not reset the captured Visual's effective
            // text/render settings merely because a property has a default value.
            foreach(var property in new[]{RenderOptions.BitmapScalingModeProperty,RenderOptions.EdgeModeProperty,
                RenderOptions.ClearTypeHintProperty,TextOptions.TextFormattingModeProperty,TextOptions.TextRenderingModeProperty,
                TextOptions.TextHintingModeProperty})
                if(source.ReadLocalValue(property)!=DependencyProperty.UnsetValue)target.SetValue(property,source.GetValue(property));
        }
        private static void CopyTile(TileBrush source,TileBrush target)
        {
            target.Opacity=source.Opacity;target.Transform=source.Transform?.CloneCurrentValue();
            target.RelativeTransform=source.RelativeTransform?.CloneCurrentValue();target.Viewbox=source.Viewbox;target.Viewport=source.Viewport;
            target.ViewboxUnits=source.ViewboxUnits;target.ViewportUnits=source.ViewportUnits;target.Stretch=source.Stretch;
            target.TileMode=source.TileMode;target.AlignmentX=source.AlignmentX;target.AlignmentY=source.AlignmentY;
        }
        private void Depth(int depth){if(depth>options.MaximumDepth)throw Unsupported("Snapshot nesting exceeds its depth budget.");}
        private void Enter(object value){if(!_active.Add(value))throw Unsupported("Cyclic drawing resources require the existing renderer.");}
        private static NotSupportedException Unsupported(string message)=>new(message);
    }
}
