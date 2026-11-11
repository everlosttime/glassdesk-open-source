using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace GlassDesk.Rendering;

public readonly record struct DirectVectorDraw(uint Key,Rect Bounds,float Opacity=1);
public sealed record DirectVectorStatistics(uint AssetCount,uint NativeVectorCount,ulong SegmentBytes,ulong GeometryBuilds,ulong Draws,ulong CoverageBuilds,ulong CacheBytes);

public sealed partial class NativeDirectPresenter
{
    private sealed record VectorAsset(PathGeometry Geometry,NativeMethods.VectorSegment[] Commands,Size SourceSize,Color Color,float StrokeWidth,VectorBrush? Brush);
    private sealed record VectorSlot(VectorAsset Asset,Rect Bounds,Size HostSize,uint Width,uint Height);
    private readonly Dictionary<uint,VectorAsset> _vectorAssets=[];
    private readonly VectorSlot?[] _vectorSlots=new VectorSlot[256];
    private ulong _vectorSourceBytes;

    /// <summary>Snapshots a solid filled production geometry. Geometry is uploaded once per active draw slot.</summary>
    public void SetVector(uint key,Geometry geometry,Size sourceSize,Color color)=>SetVectorAsset(key,geometry,sourceSize,color,0);

    /// <summary>
    /// Snapshots production round-cap/round-join stroke geometry. On a changed
    /// device transform, WPF's public geometry widener emits the exact device-space
    /// contour; the native GPU computes coverage. No bitmap or pixel readback.
    /// Nonuniform stroke scaling requires the existing renderer.
    /// </summary>
    public void SetStrokeVector(uint key,Geometry geometry,Size sourceSize,Color color,float thickness)
    {
        EnsureUsable();if(!float.IsFinite(thickness) || thickness<=0 || thickness>32768)throw new ArgumentOutOfRangeException(nameof(thickness));
        SetVectorAsset(key,geometry,sourceSize,color,thickness);
    }

    private void SetVectorAsset(uint key,Geometry geometry,Size sourceSize,Color color,float thickness,VectorBrush? brush=null)
    {
        EnsureUsable();ArgumentNullException.ThrowIfNull(geometry);
        if(key==0)throw new ArgumentOutOfRangeException(nameof(key));ValidateVectorSize(sourceSize);
        var snapshot=PathGeometry.CreateFromGeometry(geometry).CloneCurrentValue();
        var commands=VectorCommands(snapshot);snapshot.Freeze();
        _vectorAssets.TryGetValue(key,out var old);var previous=(ulong)(old?.Commands.Length??0)*32;
        var bytes=(ulong)commands.Length*32;
        if((old is null && _vectorAssets.Count>=256) || _vectorSourceBytes-previous+bytes>4*1024*1024)
            throw new ArgumentException("Vector sources exceed their bounded resource budget.",nameof(geometry));
        if(old is not null)ClearVectorSlots(old);
        _vectorAssets[key]=new(snapshot,commands,sourceSize,color,thickness,brush);_vectorSourceBytes=_vectorSourceBytes-previous+bytes;
    }

    public bool RemoveVector(uint key)
    {
        EnsureUsable();if(!_vectorAssets.TryGetValue(key,out var asset))return false;
        ClearVectorSlots(asset);_vectorAssets.Remove(key);_vectorSourceBytes-=(ulong)asset.Commands.Length*32;return true;
    }

    private void ClearVectorSlots(VectorAsset asset)
    {
        for(var i=0;i<_vectorSlots.Length;i++)if(ReferenceEquals(_vectorSlots[i]?.Asset,asset))
        {Marshal.ThrowExceptionForHR(NativeMethods.PresenterRemoveVector(_handle,(uint)i+1));_vectorSlots[i]=null;}
    }

    public DirectVectorStatistics VectorStatistics
    {
        get
        {
            EnsureUsable();var s=new NativeMethods.VectorStats{Size=56,Version=1};
            Marshal.ThrowExceptionForHR(NativeMethods.PresenterGetVectorStatistics(_handle,ref s));
            return new((uint)_vectorAssets.Count,s.VectorCount,s.SegmentBytes,s.GeometryBuilds,s.Draws,s.CoverageBuilds,s.CacheBytes);
        }
    }

    public void DrawVectors(Size hostSize,ReadOnlySpan<DirectVectorDraw> items)
    {
        EnsureUsable();ValidateVectorSize(hostSize);if(items.Length>256)throw new ArgumentOutOfRangeException(nameof(items));
        var output=Statistics;
        // Validate the complete candidate before changing any source or frame.
        foreach(var item in items)
        {
            if(!_vectorAssets.TryGetValue(item.Key,out var asset))throw new KeyNotFoundException("Vector asset is missing.");
            var b=item.Bounds;
            if(b.IsEmpty || !double.IsFinite(b.X) || !double.IsFinite(b.Y) || Math.Abs(b.X)>131072 || Math.Abs(b.Y)>131072
                || !double.IsFinite(b.Width) || !double.IsFinite(b.Height) || b.Width<.001 || b.Height<.001 || b.Width>32768 || b.Height>32768
                || !float.IsFinite(item.Opacity) || item.Opacity<0 || item.Opacity>1)throw new ArgumentException("Invalid vector draw.",nameof(items));
            if(asset.StrokeWidth>0)
            {
                var sx=b.Width/asset.SourceSize.Width*output.Width/hostSize.Width;var sy=b.Height/asset.SourceSize.Height*output.Height/hostSize.Height;
                if(Math.Abs(sx-sy)>Math.Max(sx,sy)*1e-6)throw new NotSupportedException("Nonuniform stroke transforms require the existing renderer.");
            }
        }
        var native=new NativeMethods.ImageDraw[items.Length];
        for(var i=0;i<items.Length;i++)
        {
            var draw=items[i];var asset=_vectorAssets[draw.Key];var slot=_vectorSlots[i];var key=(uint)i+1;
            if(slot is null || !ReferenceEquals(slot.Asset,asset) || (asset.StrokeWidth>0 &&
                (slot.Bounds!=draw.Bounds || slot.HostSize!=hostSize || slot.Width!=output.Width || slot.Height!=output.Height)))
            {
                var commands=asset.Commands;var size=asset.SourceSize;var fillRule=asset.Geometry.FillRule;
                if(asset.StrokeWidth>0)
                {
                    var sx=draw.Bounds.Width/size.Width*output.Width/hostSize.Width;
                    var sy=draw.Bounds.Height/size.Height*output.Height/hostSize.Height;
                    var matrix=asset.Geometry.Transform?.Value??Matrix.Identity;
                    matrix.Append(new(sx,0,0,sy,draw.Bounds.X*output.Width/hostSize.Width,draw.Bounds.Y*output.Height/hostSize.Height));
                    var device=asset.Geometry.CloneCurrentValue();device.Transform=new MatrixTransform(matrix);
                    var pen=new Pen(Brushes.Black,asset.StrokeWidth*sx){LineJoin=PenLineJoin.Round,StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round};
                    var outline=device.GetWidenedPathGeometry(pen,.25,ToleranceType.Absolute);
                    commands=VectorCommands(outline);fillRule=outline.FillRule;size=new(output.Width,output.Height);
                }
                UploadVector(key,commands,size,asset.Color,fillRule,asset.Brush);
                _vectorSlots[i]=new(asset,draw.Bounds,hostSize,output.Width,output.Height);
            }
            var bounds=asset.StrokeWidth>0?new Rect(new Point(),hostSize):draw.Bounds;
            native[i]=new(){Key=key,X=(float)bounds.X,Y=(float)bounds.Y,Width=(float)bounds.Width,Height=(float)bounds.Height,Opacity=draw.Opacity*(asset.Brush?.Opacity??1)};
        }
        var pin=GCHandle.Alloc(native,GCHandleType.Pinned);
        try
        {
            var batch=new NativeMethods.ImageBatch{Size=32,Version=1,Count=(uint)native.Length,HostWidth=(float)hostSize.Width,HostHeight=(float)hostSize.Height,Items=pin.AddrOfPinnedObject()};
            Marshal.ThrowExceptionForHR(NativeMethods.PresenterDrawVectors(_handle,ref batch));
        }
        finally{pin.Free();}
    }

    private void UploadVector(uint key,NativeMethods.VectorSegment[] commands,Size size,Color color,FillRule fillRule,VectorBrush? brush)
    {
        var pin=GCHandle.Alloc(commands,GCHandleType.Pinned);
        try
        {
            var info=new NativeMethods.VectorInfo{Size=72,Version=1,Width=(float)size.Width,Height=(float)size.Height,
                FillR=color.R/255f,FillG=color.G/255f,FillB=color.B/255f,FillA=color.A/255f,Count=(uint)commands.Length,FillRule=(uint)fillRule,Segments=pin.AddrOfPinnedObject()};
            Marshal.ThrowExceptionForHR(NativeMethods.PresenterSetVector(_handle,key,ref info));
            if(brush is not null)UploadVectorBrush(key,brush);
        }
        finally{pin.Free();}
    }

    private static void ValidateVectorSize(Size size)
    {
        if(!double.IsFinite(size.Width) || !double.IsFinite(size.Height) || size.Width<1 || size.Height<1 || size.Width>32768 || size.Height>32768)
            throw new ArgumentOutOfRangeException(nameof(size));
    }

    private static NativeMethods.VectorSegment[] VectorCommands(PathGeometry geometry)
    {
        var result=new List<NativeMethods.VectorSegment>();
        Point Transform(Point p)=>geometry.Transform?.Transform(p)??p;
        void Add(uint operation,uint flags,Point a,Point b=default,Point c=default)
        {
            a=Transform(a);if(operation==2){b=Transform(b);c=Transform(c);}
            static bool Valid(Point p)=>double.IsFinite(p.X) && double.IsFinite(p.Y) && Math.Abs(p.X)<=131072 && Math.Abs(p.Y)<=131072;
            if(!Valid(a) || !Valid(b) || !Valid(c))throw new ArgumentException("Vector coordinate exceeds the bounded geometry range.");
            if(result.Count==4096)throw new ArgumentException("Vector exceeds the bounded segment count.");
            result.Add(new(){Operation=operation,Flags=flags,X1=(float)a.X,Y1=(float)a.Y,X2=(float)b.X,Y2=(float)b.Y,X3=(float)c.X,Y3=(float)c.Y});
        }
        foreach(var figure in geometry.Figures)
        {
            var current=figure.StartPoint;Add(0,figure.IsFilled?1u:0u,current);
            void Line(Point p){Add(1,0,p);current=p;}
            void Cubic(Point a,Point b,Point c){Add(2,0,a,b,c);current=c;}
            void Quadratic(Point control,Point end)=>Cubic(current+(control-current)*(2.0/3),end+(control-end)*(2.0/3),end);
            foreach(var segment in figure.Segments)switch(segment)
            {
                case LineSegment s:Line(s.Point);break;
                case PolyLineSegment s:foreach(var p in s.Points)Line(p);break;
                case BezierSegment s:Cubic(s.Point1,s.Point2,s.Point3);break;
                case PolyBezierSegment s:for(var i=0;i<s.Points.Count;i+=3)Cubic(s.Points[i],s.Points[i+1],s.Points[i+2]);break;
                case QuadraticBezierSegment s:Quadratic(s.Point1,s.Point2);break;
                case PolyQuadraticBezierSegment s:for(var i=0;i<s.Points.Count;i+=2)Quadratic(s.Points[i],s.Points[i+1]);break;
                default:throw new NotSupportedException("This vector segment requires the existing renderer.");
            }
            if(figure.IsClosed)Add(3,0,default);
        }
        if(result.Count==0)throw new ArgumentException("A nonempty vector is required.");
        return result.ToArray();
    }
}

internal static partial class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]internal struct VectorSegment{internal uint Operation,Flags;internal float X1,Y1,X2,Y2,X3,Y3;}
    [StructLayout(LayoutKind.Sequential)]internal struct VectorInfo
    {internal uint Size,Version;internal float Width,Height,FillR,FillG,FillB,FillA,StrokeR,StrokeG,StrokeB,StrokeA,StrokeWidth;internal uint Count,FillRule,Reserved;internal nint Segments;}
    [StructLayout(LayoutKind.Sequential)]internal struct VectorStats{internal uint Size,Version,VectorCount,Reserved;internal ulong SegmentBytes,GeometryBuilds,Draws,CoverageBuilds,CacheBytes;}
    [DllImport(Dll,EntryPoint="GD_PresenterSetVector",CallingConvention=CallingConvention.Cdecl)]internal static extern int PresenterSetVector(PresenterHandle p,uint key,ref VectorInfo info);
    [DllImport(Dll,EntryPoint="GD_PresenterRemoveVector",CallingConvention=CallingConvention.Cdecl)]internal static extern int PresenterRemoveVector(PresenterHandle p,uint key);
    [DllImport(Dll,EntryPoint="GD_PresenterDrawVectors",CallingConvention=CallingConvention.Cdecl)]internal static extern int PresenterDrawVectors(PresenterHandle p,ref ImageBatch batch);
    [DllImport(Dll,EntryPoint="GD_PresenterGetVectorStatistics",CallingConvention=CallingConvention.Cdecl)]internal static extern int PresenterGetVectorStatistics(PresenterHandle p,ref VectorStats stats);
}
