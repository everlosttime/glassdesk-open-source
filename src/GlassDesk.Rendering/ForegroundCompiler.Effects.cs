using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace GlassDesk.Rendering;

internal sealed partial class ForegroundCompiler
{
    private Rect InnerBounds(VisualDrawingNode node)
    {
        if(_innerBounds.TryGetValue(node,out var bounds))return bounds;
        // Public descendant bounds omit effects on children. Include their
        // expanded output before allocating/clipping an ancestor's layer/mask.
        bounds=node.DescendantBounds;
        foreach(var child in node.Children)
        {
            var outer=InnerBounds(child);
            if(child.DropShadow is {} effect)outer=ShadowBounds(outer,effect);
            if(child.Clip is {} clip)outer.Intersect(clip.Bounds);
            var m=child.Transform;m.Translate(child.Offset.X,child.Offset.Y);
            bounds.Union(Bounds(outer,m));
        }
        _innerBounds.Add(node,bounds);return bounds;
    }
    private static Rect ShadowBounds(Rect bounds,DropShadowEffect effect)
    {
        if(bounds.IsEmpty)return bounds;
        bounds.Inflate(effect.BlurRadius,effect.BlurRadius);
        var direction=effect.Direction*Math.PI/180;
        bounds.Union(new Rect(bounds.X+effect.ShadowDepth*Math.Cos(direction),bounds.Y-effect.ShadowDepth*Math.Sin(direction),bounds.Width,bounds.Height));
        return bounds;
    }
    private void EffectNode(VisualDrawingNode node,Matrix m,ForegroundSnap? snap)
    {
        var effect=node.DropShadow!;var device=Compose(m,_device);
        if(new[]{effect.BlurRadius,effect.ShadowDepth,effect.Direction,effect.Opacity}.Any(v=>!double.IsFinite(v)))
            throw Unsupported("Non-finite shadow parameters require the existing renderer.");
        if(!AxisAligned(device) || effect.RenderingBias!=RenderingBias.Performance)
            throw Unsupported("This effect transform/rendering bias requires the existing renderer.");
        if(new[]{effect.Color.ScR,effect.Color.ScG,effect.Color.ScB}.Any(v=>!float.IsFinite(v) || v<0 || v>1))
            throw Unsupported("Extended-range shadow tint requires the existing renderer.");
        var scale=Math.Min((float)device.M11,(float)device.M22);
        var radius=(uint)Math.Min(100,Math.Floor(Math.Floor(effect.BlurRadius)*scale));
        var direction=effect.Direction*Math.PI/180;
        var offsetX=(float)(effect.ShadowDepth*scale*Math.Cos(direction));var offsetY=(float)(-effect.ShadowDepth*scale*Math.Sin(direction));
        if(!float.IsFinite(offsetX) || !float.IsFinite(offsetY) || Math.Abs(offsetX)>256 || Math.Abs(offsetY)>256)throw Unsupported("Shadow offset exceeds the effect budget.");
        var inner=InnerBounds(node);var localBounds=inner;
        if(localBounds.IsEmpty)return;
        localBounds=ShadowBounds(localBounds,effect);
        var effectBounds=Bounds(Bounds(localBounds,m),_device);effectBounds.Inflate(1,1);
        if(effectBounds.Left<0 || effectBounds.Top<0 || effectBounds.Right>pixels.Width || effectBounds.Bottom>pixels.Height)
            throw Unsupported("Configure sufficient bounded overscan for this shadow before selecting native rendering.");
        var clipped=node.Clip is not null;
        if(clipped)Push(1,LayerBounds(localBounds,node.Clip,m));
        if(++_depth>16)throw Unsupported("Foreground group depth exceeds the GPU layer budget.");
        Add(new ForegroundEffectPush(effectBounds));
        // Effects rasterize on the decomposed scale lattice, then sample with
        // the remaining translation. Integer positioning is arbitrary in this
        // pooled full-surface target; only the fractional rest changes samples.
        var shiftX=(float)(device.OffsetX-Math.Floor(device.OffsetX));
        var shiftY=(float)(device.OffsetY-Math.Floor(device.OffsetY));
        var input=m;input.OffsetX-=shiftX/_device.M11;input.OffsetY-=shiftY/_device.M22;
        snap=ForegroundSnap.Create(node.XSnappingGuidelines,node.YSnappingGuidelines,Compose(input,_device),_device);
        var modulated=node.Opacity!=1 || node.OpacityMask is not null;
        if(modulated)Push((float)node.Opacity,LayerBounds(inner,null,input));
        Content(node,input,snap);
        if(modulated){Mask(null,node.OpacityMask,inner,input);Pop();}
        Add(new ForegroundShadow(new(radius,offsetX,offsetY,effect.Color,(float)effect.Opacity,shiftX,shiftY)));Pop();
        if(clipped){Mask(node.Clip,null,inner,m);Pop();}
    }
}
