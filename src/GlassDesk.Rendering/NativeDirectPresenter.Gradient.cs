using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace GlassDesk.Rendering;

public sealed partial class NativeDirectPresenter
{
    internal sealed class VectorBrush(uint kind,float x1,float y1,float x2,float y2,NativeMethods.GradientStop[] stops,float opacity)
    {
        internal readonly uint Kind=kind;
        internal readonly float X1=x1,Y1=y1,X2=x2,Y2=y2;
        internal readonly NativeMethods.GradientStop[] Stops=stops;
        internal float Opacity=opacity;
    }

    /// <summary>
    /// Snapshots the production pad/sRGB relative linear or centered radial brush.
    /// Geometry bounds retain WPF's relative mapping; brush opacity can animate
    /// separately without rebuilding source geometry or GPU interval buffers.
    /// </summary>
    public void SetGradientVector(uint key,Geometry geometry,Size sourceSize,GradientBrush brush)
    {
        EnsureUsable();ArgumentNullException.ThrowIfNull(geometry);ArgumentNullException.ThrowIfNull(brush);ValidateVectorSize(sourceSize);
        SetVectorAsset(key,geometry,sourceSize,Colors.White,0,CreateVectorBrush(geometry,sourceSize,brush));
    }
    private static VectorBrush CreateVectorBrush(Geometry geometry,Size sourceSize,GradientBrush brush)
    {
        if(brush.MappingMode!=BrushMappingMode.RelativeToBoundingBox || brush.SpreadMethod!=GradientSpreadMethod.Pad
            || brush.ColorInterpolationMode!=ColorInterpolationMode.SRgbLinearInterpolation || !brush.Transform.Value.IsIdentity || !brush.RelativeTransform.Value.IsIdentity)
            throw new NotSupportedException("This gradient mapping requires the existing renderer.");
        if(brush.GradientStops.Count<2 || brush.GradientStops.Count>8)throw new NotSupportedException("A direct gradient requires two to eight ordered stops.");
        if(!double.IsFinite(brush.Opacity) || brush.Opacity<0 || brush.Opacity>1)throw new ArgumentException("Invalid brush opacity.",nameof(brush));
        var bounds=geometry.Bounds;
        if(bounds.IsEmpty || !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height) || bounds.Width<=0 || bounds.Height<=0)
            throw new NotSupportedException("Degenerate gradient bounds require the existing renderer.");
        Point Relative(Point p)=>new((bounds.X+p.X*bounds.Width)/sourceSize.Width,(bounds.Y+p.Y*bounds.Height)/sourceSize.Height);
        Point first,second;uint kind;
        if(brush is LinearGradientBrush linear){kind=1;first=Relative(linear.StartPoint);second=Relative(linear.EndPoint);}
        else if(brush is RadialGradientBrush radial && radial.Center==radial.GradientOrigin)
        {kind=2;first=Relative(radial.Center);second=new(radial.RadiusX*bounds.Width/sourceSize.Width,radial.RadiusY*bounds.Height/sourceSize.Height);}
        else throw new NotSupportedException("This radial focal point requires the existing renderer.");
        var x1=(float)first.X;var y1=(float)first.Y;var x2=(float)second.X;var y2=(float)second.Y;
        foreach(var value in new[]{x1,y1,x2,y2})if(!float.IsFinite(value) || Math.Abs(value)>32768)throw new ArgumentException("Gradient coordinates exceed the direct resource range.",nameof(brush));
        if((kind==2 && (x2<.0001f || y2<.0001f)) || (kind==1 && (x2-x1)*(x2-x1)+(y2-y1)*(y2-y1)<1e-12f))
            throw new NotSupportedException("This gradient extent requires the existing renderer.");
        var stops=new NativeMethods.GradientStop[brush.GradientStops.Count];var previous=-1f;
        for(var i=0;i<stops.Length;i++)
        {
            var source=brush.GradientStops[i];var offset=(float)source.Offset;var color=source.Color;
            if(!float.IsFinite(offset) || offset<0 || offset>1 || offset<=previous)throw new NotSupportedException("Direct gradient stops must have strictly increasing offsets in [0,1].");
            stops[i]=new(){Offset=offset,R=color.R/255f,G=color.G/255f,B=color.B/255f,A=color.A/255f};previous=offset;
        }
        return new(kind,x1,y1,x2,y2,stops,(float)brush.Opacity);
    }

    public void SetVectorBrushOpacity(uint key,float opacity)
    {
        EnsureUsable();if(!float.IsFinite(opacity) || opacity<0 || opacity>1)throw new ArgumentOutOfRangeException(nameof(opacity));
        if(!_vectorAssets.TryGetValue(key,out var asset))throw new KeyNotFoundException("Vector asset is missing.");
        if(asset.Brush is null)throw new InvalidOperationException("The vector has no gradient brush.");
        asset.Brush.Opacity=opacity;
    }

    private void UploadVectorBrush(uint key,VectorBrush brush)
    {
        var pin=GCHandle.Alloc(brush.Stops,GCHandleType.Pinned);
        try
        {
            var info=new NativeMethods.VectorBrushInfo{Size=40,Version=1,Kind=brush.Kind,Count=(uint)brush.Stops.Length,
                X1=brush.X1,Y1=brush.Y1,X2=brush.X2,Y2=brush.Y2,Stops=pin.AddrOfPinnedObject()};
            Marshal.ThrowExceptionForHR(NativeMethods.PresenterSetVectorBrush(_handle,key,ref info));
        }
        finally{pin.Free();}
    }
}

internal static partial class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]internal struct GradientStop{internal float Offset,R,G,B,A;internal uint Reserved;}
    [StructLayout(LayoutKind.Sequential)]internal struct VectorBrushInfo{internal uint Size,Version,Kind,Count;internal float X1,Y1,X2,Y2;internal nint Stops;}
    [DllImport(Dll,EntryPoint="GD_PresenterSetVectorBrush",CallingConvention=CallingConvention.Cdecl)]internal static extern int PresenterSetVectorBrush(PresenterHandle presenter,uint key,ref VectorBrushInfo info);
}
