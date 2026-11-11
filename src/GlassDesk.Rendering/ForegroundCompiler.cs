using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GlassDesk.Rendering;

internal sealed partial class ForegroundCompiler(Size host,Size pixels,Func<uint> key,ForegroundVectorCompileCache cache)
{
    private readonly ForegroundPlan _plan=new();
    private readonly Matrix _device=new(pixels.Width/host.Width,0,0,pixels.Height/host.Height,0,0);
    private int _depth;
    private readonly Dictionary<BitmapSource,uint> _images=new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<VisualDrawingNode,Rect> _innerBounds=[];
    internal static ForegroundPlan Compile(VisualDrawingSnapshot snapshot,Size host,Size pixels,Func<uint> key,ForegroundVectorCompileCache cache,Vector origin=default)
    {
        if(!double.IsFinite(host.Width) || !double.IsFinite(host.Height) || host.Width<1 || host.Height<1 || host.Width>32768 || host.Height>32768)
            throw new ArgumentOutOfRangeException(nameof(host));
        var compiler=new ForegroundCompiler(host,pixels,key,cache);var root=Matrix.Identity;root.Translate(origin.X,origin.Y);compiler.Node(snapshot.Root,root);
        return compiler._plan;
    }
    private void Add(ForegroundCommand command){if(_plan.Commands.Count>=4096)throw Unsupported("Foreground command budget exceeded.");_plan.Commands.Add(command);}
    private void Push(float opacity,Rect? bounds){if(++_depth>16)throw Unsupported("Foreground group depth exceeds the GPU layer budget.");Add(new ForegroundPush(opacity,bounds));}
    private void Pop(){Add(new ForegroundPop());--_depth;}
    private static NotSupportedException Unsupported(string message)=>new(message);
    private static Matrix Compose(Matrix local,Matrix parent){local.Append(parent);return local;}
    private static bool AxisAligned(Matrix m)=>m.M12==0 && m.M21==0 && m.M11>0 && m.M22>0;
    private static Geometry Transform(Geometry geometry,Matrix matrix)
    {var copy=geometry.CloneCurrentValue();copy.Transform=new MatrixTransform(Compose(copy.Transform?.Value??Matrix.Identity,matrix));copy.Freeze();return copy;}
    private static Rect Bounds(Rect r,Matrix m)=>r.IsEmpty?Rect.Empty:new MatrixTransform(m).TransformBounds(r);
    private Rect LayerBounds(Rect inner,Geometry? clip,Matrix m)
    {
        var bounds=Bounds(inner,m);if(clip is not null)bounds.Intersect(Bounds(clip.Bounds,m));
        bounds=Bounds(bounds,_device);if(bounds.IsEmpty)return new Rect(0,0,0,0);bounds.Inflate(1,1);return bounds;
    }
    private void Node(VisualDrawingNode node,Matrix parent)
    {
        if(node.Opacity==0)return;
        if(node.EdgeMode==EdgeMode.Aliased || node.ClearTypeHint==ClearTypeHint.Enabled || node.TextRenderingMode!=TextRenderingMode.Auto || node.TextHintingMode!=TextHintingMode.Auto)
            throw Unsupported("Explicit foreground raster options require the existing renderer.");
        var local=node.Transform;local.Translate(node.Offset.X,node.Offset.Y);var m=Compose(local,parent);
        // Every Visual pushes its own guideline frame, including an empty one;
        // a child Visual never inherits its parent's snapping frame.
        var snap=ForegroundSnap.Create(node.XSnappingGuidelines,node.YSnappingGuidelines,Compose(m,_device),_device);
        if(node.DropShadow is not null){EffectNode(node,m,snap);return;}
        var grouped=node.Opacity!=1 || node.Clip is not null || node.OpacityMask is not null;
        var inner=InnerBounds(node);
        if(grouped)Push((float)node.Opacity,LayerBounds(inner,node.Clip,m));
        Content(node,m,snap);
        if(grouped){Mask(node.Clip,node.OpacityMask,inner,m);Pop();}
    }
    private void Content(VisualDrawingNode node,Matrix m,ForegroundSnap? snap)
    {Drawing(node.Drawing,m,node.TextFormattingMode,snap,node.BitmapScalingMode);foreach(var child in node.Children)Node(child,m);}
    private void Drawing(Drawing drawing,Matrix parent,TextFormattingMode mode,ForegroundSnap? snap,BitmapScalingMode bitmapMode)
    {
        switch(drawing)
        {
            case DrawingGroup group:
                if(group.Opacity==0)return;
                var m=Compose(group.Transform?.Value??Matrix.Identity,parent);
                if(RenderOptions.GetEdgeMode(group)==EdgeMode.Aliased || RenderOptions.GetClearTypeHint(group)==ClearTypeHint.Enabled
                    || TextOptions.GetTextRenderingMode(group)!=TextRenderingMode.Auto || TextOptions.GetTextHintingMode(group)!=TextHintingMode.Auto)
                    throw Unsupported("Explicit drawing raster options require the existing renderer.");
                if(group.ReadLocalValue(RenderOptions.BitmapScalingModeProperty)!=DependencyProperty.UnsetValue)bitmapMode=RenderOptions.GetBitmapScalingMode(group);
                if(group.GuidelineSet is {} guide && (guide.GuidelinesX.Count>0 || guide.GuidelinesY.Count>0))
                {
                    if(guide.GuidelinesX.Count==0 && guide.GuidelinesY.Count==2 && guide.GuidelinesY[1]==0
                        && group.Children.Count==1 && group.Children[0] is GlyphRunDrawing {GlyphRun:{} guidedRun}
                        && guide.GuidelinesY[0]==guidedRun.BaselineOrigin.Y)snap=null; // Native glyph baseline realization applies this guide.
                    else snap=ForegroundSnap.Create(guide.GuidelinesX,guide.GuidelinesY,Compose(m,_device),_device);
                }
                if(group.ReadLocalValue(TextOptions.TextFormattingModeProperty)!=DependencyProperty.UnsetValue)mode=TextOptions.GetTextFormattingMode(group);
                var grouped=group.Opacity!=1 || group.ClipGeometry is not null || group.OpacityMask is not null;
                if(grouped)Push((float)group.Opacity,null);
                foreach(var child in group.Children)Drawing(child,m,mode,snap,bitmapMode);
                if(grouped){var inner=Rect.Empty;foreach(var child in group.Children)inner.Union(child.Bounds);Mask(group.ClipGeometry,group.OpacityMask,inner,m);Pop();}
                break;
            case GeometryDrawing geometry:
                if(geometry.Geometry is null || geometry.Geometry.IsEmpty())return;
                if(geometry.Brush is {} fill)Vector(geometry.Geometry,host,fill,parent,snap);
                if(geometry.Pen is {Thickness:>0,Brush:not null} pen)
                {
                    var device=Compose(parent,_device);
                    var sx=Math.Sqrt(device.M11*device.M11+device.M12*device.M12);var sy=Math.Sqrt(device.M21*device.M21+device.M22*device.M22);
                    if(Math.Abs(sx-sy)>1e-6*Math.Max(sx,sy) || Math.Abs(device.M11*device.M21+device.M12*device.M22)>1e-6*sx*sy)
                        throw Unsupported("Nonuniform foreground stroke transform requires the existing renderer.");
                    if(pen.Brush is not SolidColorBrush)throw Unsupported("Gradient foreground strokes require an adapted brush map.");
                    Vector(geometry.Geometry,pixels,pen.Brush,device,snap,true,pen);
                }
                break;
            case ImageDrawing image when image.ImageSource is BitmapSource bitmap:
                if(image.Rect.IsEmpty || image.Rect.Width==0 || image.Rect.Height==0)return;
                if(!AxisAligned(parent))throw Unsupported("Rotated bitmap foreground requires the existing renderer.");
                var imageBounds=Bounds(image.Rect,parent);
                if(bitmapMode==BitmapScalingMode.NearestNeighbor || (bitmapMode!=BitmapScalingMode.HighQuality
                    && (imageBounds.Width*_device.M11/bitmap.PixelWidth<=1/Math.Sqrt(2) || imageBounds.Height*_device.M22/bitmap.PixelHeight<=1/Math.Sqrt(2))))
                    throw Unsupported("This bitmap sampling mode requires the existing renderer.");
                if(!_images.TryGetValue(bitmap,out var imageKey)){imageKey=key();_images.Add(bitmap,imageKey);_plan.Assets.Add(new ForegroundImage(imageKey,bitmap));}
                // WPF FillPath applies guidelines to coverage, leaving the
                // bitmap brush's world-to-device sampling transform intact.
                var coverage=snap is null?imageBounds:new Rect(snap.Point(imageBounds.TopLeft,false),snap.Point(imageBounds.BottomRight,false));
                if(coverage.Width==0 || coverage.Height==0)return;
                Add(new ForegroundDrawImage(new(imageKey,coverage,1,snap is null?null:imageBounds)));break;
            case GlyphRunDrawing glyph when glyph.GlyphRun is {} run:
                if(glyph.ForegroundBrush is not SolidColorBrush ink || !AxisAligned(parent))throw Unsupported("Transformed/painted glyph foreground requires the existing renderer.");
                if(!run.GlyphTypeface.FontUri.IsAbsoluteUri || !run.GlyphTypeface.FontUri.IsFile || run.FontRenderingEmSize is <.01 or >1024)
                    throw Unsupported("This glyph source requires the existing renderer.");
                var glyphKey=key();_plan.Assets.Add(new ForegroundGlyph(glyphKey,run,mode));
                var baseline=parent.Transform(run.BaselineOrigin);if(snap is not null)baseline=snap.Point(baseline,false);
                Add(new ForegroundDrawGlyph(new(glyphKey,baseline,ink.Color,(float)ink.Opacity,(float)parent.M11,(float)parent.M22)));break;
            default:throw Unsupported("Unsupported foreground drawing requires the existing renderer.");
        }
    }
    private void Vector(Geometry geometry,Size source,Brush paint,Matrix matrix,ForegroundSnap? snap=null,bool deviceCoordinates=false,Pen? pen=null)
    {
        if(paint.Opacity==0 || paint is SolidColorBrush {Color.A:0})return;
        if(paint is not SolidColorBrush and not GradientBrush)throw Unsupported("Unsupported foreground brush.");
        if(paint is GradientBrush && !AxisAligned(matrix))throw Unsupported("Rotated gradients require an adapted brush map.");
        // Canonical solid fills keep shape data in source coordinates. The
        // existing native vector rectangle carries pose without re-uploading.
        var placed=pen is null && snap is null && paint is SolidColorBrush && AxisAligned(matrix)
            && matrix.M11>=.001 && matrix.M22>=.001 && matrix.M11<=32768 && matrix.M22<=32768;
        var compileMatrix=placed?Matrix.Identity:matrix;var compileSize=placed?new Size(1,1):source;
        var compileKey=cache.Find(geometry,paint,pen,compileMatrix,compileSize,snap,out var prepared);
        if(prepared is null)
        {
            var shape=placed?geometry:pen is null?Transform(geometry,matrix):WidenStroke(geometry,pen,matrix,Math.Sqrt(matrix.M11*matrix.M11+matrix.M12*matrix.M12));
            var mapping=shape;if(snap is not null)shape=snap.Geometry(shape,deviceCoordinates);
            prepared=NativeDirectPresenter.PrepareForegroundVector(shape,mapping,compileSize,paint);
        }
        if(placed)ValidatePlacement(prepared,matrix);
        var id=key();_plan.Assets.Add(new ForegroundVector(id,prepared,compileKey));
        var bounds=placed?new Rect(matrix.OffsetX,matrix.OffsetY,matrix.M11,matrix.M22):new Rect(new Point(),host);
        Add(new ForegroundDrawVector(id,paint is SolidColorBrush?(float)paint.Opacity:1,bounds));
    }
    private static void ValidatePlacement(NativeDirectPresenter.PreparedForegroundVector prepared,Matrix matrix)
    {
        static bool Valid(double value)=>double.IsFinite(value) && Math.Abs(value)<=131072;
        if(!Valid(matrix.OffsetX) || !Valid(matrix.OffsetY))throw new ArgumentException("Vector placement exceeds the bounded coordinate range.");
        foreach(var command in prepared.Commands)
        {
            bool Point(float x,float y)=>Valid(x*matrix.M11+matrix.OffsetX) && Valid(y*matrix.M22+matrix.OffsetY);
            if(!Point(command.X1,command.Y1) || command.Operation==2 && (!Point(command.X2,command.Y2) || !Point(command.X3,command.Y3)))
                throw new ArgumentException("Vector placement exceeds the bounded coordinate range.");
        }
    }
    private void Mask(Geometry? clip,Brush? mask,Rect inner,Matrix m)
    {
        if(clip is null && mask is null)return;Add(new ForegroundMask());
        if(clip is not null && mask is not null)
        {
            Push(1,null);Vector(new RectangleGeometry(inner),host,mask,m);Add(new ForegroundMask());
            Vector(clip,host,Brushes.White,m);Pop();
        }
        else if(clip is not null)Vector(clip,host,Brushes.White,m);
        else Vector(new RectangleGeometry(inner),host,mask!,m);
    }
}
