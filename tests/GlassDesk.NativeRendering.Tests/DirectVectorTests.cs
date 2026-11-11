using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace GlassDesk.NativeRendering.Tests;

public sealed class DirectVectorTests
{
    internal const string PcPath="M6,6 H42 Q45,6 45,9 V29 Q45,32 42,32 H6 Q3,32 3,29 V9 Q3,6 6,6 Z M24,32 V39 M15,40 H33";

    [Fact]
    public void VectorResourcesAreBoundedAtomicAndReleaseRasterCacheOnResize()=>NativeEngineTests.OnSta(()=>
    {
        var host=DirectShadowTests.Host(80,80);host.Show();var info=Info.Create(100,100,new System.Windows.Interop.WindowInteropHelper(host).Handle);info.Flags=1;
        Assert.Equal(0,DirectPresenterTests.Create(ref info,out var p));
        try
        {
            // The expanded stats are required to observe GPU/CPU interval cache
            // ownership, not infer release from source-geometry bytes alone.
            var stats=new VectorStats{Size=56,Version=1};Assert.Equal(0,Statistics(p,ref stats));Assert.Equal(0ul,stats.CacheBytes);
            var segments=new[]{Move(0,0),Line(40,0),Line(40,40),Line(0,40),new Segment{Operation=3}};
            var pin=GCHandle.Alloc(segments,GCHandleType.Pinned);
            try
            {
                var vector=new VectorInfo{Size=72,Version=1,Width=48,Height=48,FillR=1,FillA=1,Count=5,Segments=pin.AddrOfPinnedObject()};
                Assert.Equal(0,SetVector(p,1,ref vector));Assert.Equal(0,Statistics(p,ref stats));Assert.Equal(160ul,stats.SegmentBytes);
                Assert.Equal(0,Draw(p,new(){Key=1,Width=48,Height=48,Opacity=1}));Assert.Equal(0,Statistics(p,ref stats));
                Assert.True(stats.CacheBytes>0);var initial=stats;
                Assert.Equal(0,Draw(p,new(){Key=1,Width=48,Height=48,Opacity=.4f}));Assert.Equal(0,Statistics(p,ref stats));
                Assert.Equal(initial.CoverageBuilds,stats.CoverageBuilds);Assert.Equal(initial.CacheBytes,stats.CacheBytes);
                vector.Version=2;Assert.Equal(unchecked((int)0x80004002),SetVector(p,1,ref vector));vector.Version=1;
                segments[1].X1=float.NaN;Assert.Equal(unchecked((int)0x80070057),SetVector(p,1,ref vector));segments[1].X1=40;
                Assert.Equal(0,Statistics(p,ref stats));Assert.Equal(initial.GeometryBuilds,stats.GeometryBuilds);
                var before=new byte[40000];Assert.Equal(0,DirectGlassTests.ReadPixels(p,before,40000,400));
                var items=new[]{new DirectImageTests.ImageDraw{Key=1,Width=48,Height=48,Opacity=1},new DirectImageTests.ImageDraw{Key=99,Width=48,Height=48,Opacity=1}};
                var batchPin=GCHandle.Alloc(items,GCHandleType.Pinned);
                try{var batch=new DirectImageTests.ImageBatch{Size=32,Version=1,Count=2,HostWidth=80,HostHeight=80,Items=batchPin.AddrOfPinnedObject()};Assert.Equal(unchecked((int)0x80070490),DrawVectors(p,ref batch));}
                finally{batchPin.Free();}
                var after=new byte[40000];Assert.Equal(0,DirectGlassTests.ReadPixels(p,after,40000,400));Assert.Equal(before,after);
                Assert.Equal(0,DirectPresenterTests.Resize(p,120,120));Assert.Equal(0,Statistics(p,ref stats));Assert.Equal(0ul,stats.CacheBytes);Assert.Equal(160ul,stats.SegmentBytes);
                Assert.Equal(0,Draw(p,new(){Key=1,Width=48,Height=48,Opacity=1}));Assert.Equal(0,Statistics(p,ref stats));Assert.True(stats.CoverageBuilds>initial.CoverageBuilds);
                for(uint key=2;key<=256;key++)Assert.Equal(0,SetVector(p,key,ref vector));
                Assert.Equal(unchecked((int)0x8007000e),SetVector(p,257,ref vector));
                Assert.Equal(0,Remove(p,1));Assert.Equal(1,Remove(p,1));
            }
            finally{pin.Free();}
        }
        finally{DirectPresenterTests.Destroy(p);host.Close();}
    });

    [Fact]
    public void DeviceSpaceOutlineRetainsTheWpfHardwareStrokeContract()=>NativeEngineTests.OnSta(()=>
    {
        var geometry=Geometry.Parse(PcPath);var pen=new Pen(Brushes.White,3.5){LineJoin=PenLineJoin.Round,StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round};
        var original=Capture(geometry,true);var results=new List<object>();
        foreach(var tolerance in new[]{.25,.01,.001})
        {
            var outline=geometry.GetWidenedPathGeometry(pen,tolerance,ToleranceType.Absolute);
            var widened=Capture(outline,false);var max=0;double sum=0;
            for(var i=0;i<original.Pixels.Length;i++){var d=Math.Abs(original.Pixels[i]-widened.Pixels[i]);max=Math.Max(max,d);sum+=d;}
            results.Add(new{tolerance,max,mean=sum/original.Pixels.Length,figures=outline.Figures.Count,segments=outline.Figures.SelectMany(f=>f.Segments).Select(s=>s.GetType().Name).Distinct().ToArray()});
        }
        var scale=original.Width/80.0;
        var deviceGeometry=geometry.CloneCurrentValue();deviceGeometry.Transform=new MatrixTransform(scale,0,0,scale,12*scale,10*scale);
        var devicePen=pen.CloneCurrentValue();devicePen.Thickness*=scale;
        var deviceOutline=deviceGeometry.GetWidenedPathGeometry(devicePen,.25,ToleranceType.Absolute);
        var deviceSegments=Segments(deviceOutline);
        deviceOutline.Transform=new MatrixTransform(1/scale,0,0,1/scale,-12,-10);
        var deviceCapture=Capture(deviceOutline,false);var deviceMax=0;double deviceSum=0;
        for(var i=0;i<original.Pixels.Length;i++){var d=Math.Abs(original.Pixels[i]-deviceCapture.Pixels[i]);deviceMax=Math.Max(deviceMax,d);deviceSum+=d;}
        results.Add(new{deviceSpace=true,max=deviceMax,mean=deviceSum/original.Pixels.Length});
        Assert.Equal(0,deviceMax);
        var nativeHost=DirectShadowTests.Host(80,80);nativeHost.Show();
        try
        {
            var info=Info.Create((uint)original.Width,(uint)original.Height,new System.Windows.Interop.WindowInteropHelper(nativeHost).Handle);info.Flags=1;
            Assert.Equal(0,DirectPresenterTests.Create(ref info,out var p));
            try
            {
                var pin=GCHandle.Alloc(deviceSegments,GCHandleType.Pinned);
                try{var vector=new VectorInfo{Size=72,Version=1,Width=original.Width,Height=original.Height,FillR=1,FillG=1,FillB=1,FillA=1,FillRule=(uint)deviceOutline.FillRule,Count=(uint)deviceSegments.Length,Segments=pin.AddrOfPinnedObject()};Assert.Equal(0,SetVector(p,1,ref vector));}
                finally{pin.Free();}
                Assert.Equal(0,DirectPresenterTests.Clear(p,0,0,0,0));Assert.Equal(0,Draw(p,new(){Key=1,Width=80,Height=80,Opacity=1}));
                var pixels=new byte[original.Pixels.Length];Assert.Equal(0,DirectGlassTests.ReadPixels(p,pixels,(ulong)pixels.Length,(uint)original.Width*4));var maximum=0;double sum=0;
                for(var i=0;i<pixels.Length;i++){var d=Math.Abs(original.Pixels[i]-pixels[i]);maximum=Math.Max(maximum,d);sum+=d;}
                results.Add(new{nativeDeviceOutline=true,max=maximum,mean=sum/pixels.Length});
                Assert.True(sum/pixels.Length<1.5);Assert.InRange(maximum,0,8);
            }
            finally{DirectPresenterTests.Destroy(p);}
        }
        finally{nativeHost.Close();}
        var directory=System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Api.LibraryPath)!,"vector-parity");System.IO.Directory.CreateDirectory(directory);
        System.IO.File.WriteAllText(System.IO.Path.Combine(directory,"wpf-stroke-vs-widened.json"),System.Text.Json.JsonSerializer.Serialize(results));
        static CapturePixels Capture(Geometry data,bool stroke)
        {
            var path=new Path{Data=data,Stroke=stroke?Brushes.White:null,Fill=stroke?Brushes.Transparent:Brushes.White,StrokeThickness=3.5,
                StrokeLineJoin=PenLineJoin.Round,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round};
            var canvas=new Canvas();canvas.Children.Add(path);Canvas.SetLeft(path,12);Canvas.SetTop(path,10);
            var host=DirectShadowTests.Host(80,80);host.Content=canvas;
            try{host.Show();host.UpdateLayout();return WgcWindowCapture.Capture(new System.Windows.Interop.WindowInteropHelper(host).Handle,TimeSpan.FromSeconds(3))??throw new InvalidOperationException("Characterization reference missing.");}
            finally{host.Close();}
        }
    });

    [Theory]
    [InlineData(48f,1f,0f)]
    [InlineData(55.2f,.4f,.35f)]
    [InlineData(45.12f,.7f,.2f)]
    public void NativeCachedPcGlyphPreservesProductionRoundStrokes(float size,float opacity,float offset)=>NativeEngineTests.OnSta(()=>
    {
        var library=NativeLibrary.Load(Api.LibraryPath);
        try{Assert.True(NativeLibrary.TryGetExport(library,"GD_PresenterSetVector",out _),"Direct output cannot draw production vector glyphs yet.");}
        finally{NativeLibrary.Free(library);}
        var root=new Canvas();
        var path=new Path{Data=Geometry.Parse(PcPath),Fill=Brushes.Transparent,Stroke=Brushes.White,StrokeThickness=3.5,
            StrokeLineJoin=PenLineJoin.Round,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round,
            Opacity=opacity,RenderTransform=new ScaleTransform(size/48,size/48)};
        Canvas.SetLeft(path,12+offset);Canvas.SetTop(path,10+offset);root.Children.Add(path);
        var referenceHost=DirectShadowTests.Host(80,80);referenceHost.Content=root;
        var nativeHost=DirectShadowTests.Host(80,80);
        try
        {
            referenceHost.Show();referenceHost.UpdateLayout();
            var reference=WgcWindowCapture.Capture(new System.Windows.Interop.WindowInteropHelper(referenceHost).Handle,TimeSpan.FromSeconds(3))??throw new InvalidOperationException("Production vector reference missing.");
            nativeHost.Show();
            var info=Info.Create((uint)reference.Width,(uint)reference.Height,new System.Windows.Interop.WindowInteropHelper(nativeHost).Handle);info.Flags=1;
            Assert.Equal(0,DirectPresenterTests.Create(ref info,out var p));
            try
            {
                // Hardware WPF widens strokes after the world->device transform.
                // Preserve those vector semantics; no reference pixels are uploaded.
                var scale=reference.Width/80.0*size/48;
                var source=Geometry.Parse(PcPath).CloneCurrentValue();source.Transform=new MatrixTransform(scale,0,0,scale,(12+offset)*reference.Width/80,(10+offset)*reference.Height/80);
                var pen=new Pen(Brushes.White,3.5*scale){LineJoin=PenLineJoin.Round,StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round};
                var outline=source.GetWidenedPathGeometry(pen,.25,ToleranceType.Absolute);var segments=Segments(outline);
                var pin=GCHandle.Alloc(segments,GCHandleType.Pinned);
                try{var vector=new VectorInfo{Size=72,Version=1,Width=reference.Width,Height=reference.Height,FillR=1,FillG=1,FillB=1,FillA=1,FillRule=(uint)outline.FillRule,Count=(uint)segments.Length,Segments=pin.AddrOfPinnedObject()};Assert.Equal(0,SetVector(p,1,ref vector));}
                finally{pin.Free();}
                Assert.Equal(0,DirectPresenterTests.Clear(p,0,0,0,0));
                Assert.Equal(0,Draw(p,new(){Key=1,Width=80,Height=80,Opacity=opacity}));
                var actual=new byte[reference.Pixels.Length];Assert.Equal(0,DirectGlassTests.ReadPixels(p,actual,(ulong)actual.Length,(uint)reference.Width*4));
                var maximum=0;double sum=0;for(var i=0;i<actual.Length;i++){var error=Math.Abs(actual[i]-reference.Pixels[i]);maximum=Math.Max(maximum,error);sum+=error;}
                var directory=System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Api.LibraryPath)!,"vector-parity");System.IO.Directory.CreateDirectory(directory);
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(directory,$"pc-{size}-{opacity}.wpf.bgra"),reference.Pixels);
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(directory,$"pc-{size}-{opacity}.native.bgra"),actual);
                System.IO.File.WriteAllText(System.IO.Path.Combine(directory,$"pc-{size}-{opacity}.json"),System.Text.Json.JsonSerializer.Serialize(new{size,opacity,offset,reference.Width,reference.Height,maximum,mean=sum/actual.Length}));
                Assert.True(sum/actual.Length<1.5,$"Vector mean {sum/actual.Length:F4}, max {maximum}.");Assert.InRange(maximum,0,8);
            }
            finally{DirectPresenterTests.Destroy(p);}
        }
        finally{referenceHost.Close();nativeHost.Close();}
    });

    internal static Segment Move(float x,float y)=>new(){Operation=0,Flags=1,X1=x,Y1=y};
    internal static Segment[] Segments(PathGeometry geometry)
    {
        var result=new List<Segment>();
        Point Transform(Point p)=>geometry.Transform?.Transform(p)??p;
        foreach(var figure in geometry.Figures)
        {
            var start=Transform(figure.StartPoint);var move=Move((float)start.X,(float)start.Y);move.Flags=figure.IsFilled?1u:0u;result.Add(move);
            foreach(var segment in figure.Segments)
            {
                switch(segment)
                {
                    case LineSegment line:var p=Transform(line.Point);result.Add(Line((float)p.X,(float)p.Y));break;
                    case PolyLineSegment lines:foreach(var point in lines.Points){p=Transform(point);result.Add(Line((float)p.X,(float)p.Y));}break;
                    case BezierSegment curve:AddCubic(curve.Point1,curve.Point2,curve.Point3);break;
                    case PolyBezierSegment curves:for(var i=0;i<curves.Points.Count;i+=3)AddCubic(curves.Points[i],curves.Points[i+1],curves.Points[i+2]);break;
                    default:throw new NotSupportedException(segment.GetType().Name);
                }
            }
            if(figure.IsClosed)result.Add(new(){Operation=3});
        }
        return result.ToArray();
        void AddCubic(Point a,Point b,Point c){a=Transform(a);b=Transform(b);c=Transform(c);result.Add(Cubic((float)a.X,(float)a.Y,(float)b.X,(float)b.Y,(float)c.X,(float)c.Y));}
    }
    internal static Segment Line(float x,float y)=>new(){Operation=1,X1=x,Y1=y};
    internal static Segment Cubic(float a,float b,float c,float d,float e,float f)=>new(){Operation=2,X1=a,Y1=b,X2=c,Y2=d,X3=e,Y3=f};
    internal static void Upload(nint p,uint key,Segment[] segments)
    {
        var pin=GCHandle.Alloc(segments,GCHandleType.Pinned);
        try{var info=new VectorInfo{Size=72,Version=1,Width=48,Height=48,StrokeR=1,StrokeG=1,StrokeB=1,StrokeA=1,StrokeWidth=3.5f,Count=(uint)segments.Length,Segments=pin.AddrOfPinnedObject()};Assert.Equal(0,SetVector(p,key,ref info));}
        finally{pin.Free();}
    }
    internal static int Draw(nint p,DirectImageTests.ImageDraw item)
    {
        var pin=GCHandle.Alloc(new[]{item},GCHandleType.Pinned);
        try{var batch=new DirectImageTests.ImageBatch{Size=32,Version=1,Count=1,HostWidth=80,HostHeight=80,Items=pin.AddrOfPinnedObject()};return DrawVectors(p,ref batch);}
        finally{pin.Free();}
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Segment{internal uint Operation,Flags;internal float X1,Y1,X2,Y2,X3,Y3;}
    [StructLayout(LayoutKind.Sequential)] internal struct VectorInfo
    {internal uint Size,Version;internal float Width,Height,FillR,FillG,FillB,FillA,StrokeR,StrokeG,StrokeB,StrokeA,StrokeWidth;internal uint Count,FillRule,Reserved;internal nint Segments;}
    [DllImport("GlassDesk.Rendering.Native.dll",EntryPoint="GD_PresenterSetVector",CallingConvention=CallingConvention.Cdecl)]internal static extern int SetVector(nint p,uint key,ref VectorInfo info);
    [DllImport("GlassDesk.Rendering.Native.dll",EntryPoint="GD_PresenterDrawVectors",CallingConvention=CallingConvention.Cdecl)]internal static extern int DrawVectors(nint p,ref DirectImageTests.ImageBatch batch);
    [StructLayout(LayoutKind.Sequential)]internal struct VectorStats{internal uint Size,Version,Count,Reserved;internal ulong SegmentBytes,GeometryBuilds,Draws,CoverageBuilds,CacheBytes;}
    [DllImport("GlassDesk.Rendering.Native.dll",EntryPoint="GD_PresenterGetVectorStatistics",CallingConvention=CallingConvention.Cdecl)]internal static extern int Statistics(nint p,ref VectorStats stats);
    [DllImport("GlassDesk.Rendering.Native.dll",EntryPoint="GD_PresenterRemoveVector",CallingConvention=CallingConvention.Cdecl)]internal static extern int Remove(nint p,uint key);
}
