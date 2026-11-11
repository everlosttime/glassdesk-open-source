using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace GlassDesk.NativeRendering.Tests;

public sealed class DirectGradientTests
{
    [Fact]
    public void InvalidBrushAbiStopsAndMissingKeysNeverMutateTheExistingFrame()=>NativeEngineTests.OnSta(()=>
    {
        var host=DirectShadowTests.Host(80,80);host.Show();var create=Info.Create(100,100,new WindowInteropHelper(host).Handle);create.Flags=1;
        Assert.Equal(0,DirectPresenterTests.Create(ref create,out var p));
        try
        {
            var segments=new[]{DirectVectorTests.Move(0,0),DirectVectorTests.Line(48,0),DirectVectorTests.Line(48,64),DirectVectorTests.Line(0,64),new DirectVectorTests.Segment{Operation=3}};
            var pin=GCHandle.Alloc(segments,GCHandleType.Pinned);
            try{var info=new DirectVectorTests.VectorInfo{Size=72,Version=1,Width=48,Height=64,FillA=1,Count=5,Segments=pin.AddrOfPinnedObject()};Assert.Equal(0,DirectVectorTests.SetVector(p,1,ref info));}
            finally{pin.Free();}
            var stops=Stops(2);pin=GCHandle.Alloc(stops,GCHandleType.Pinned);
            try
            {
                var brush=BrushInfo.Create(2,pin.AddrOfPinnedObject(),3);
                Assert.Equal(unchecked((int)0x80070490),SetBrush(p,99,ref brush));Assert.Equal(0,SetBrush(p,1,ref brush));
                Assert.Equal(0,DirectPresenterTests.Clear(p,0,0,0,0));Assert.Equal(0,DirectVectorTests.Draw(p,new(){Key=1,Width=48,Height=64,Opacity=.46f}));
                var before=new byte[40000];Assert.Equal(0,DirectGlassTests.ReadPixels(p,before,40000,400));
                var stats=new DirectVectorTests.VectorStats{Size=56,Version=1};Assert.Equal(0,DirectVectorTests.Statistics(p,ref stats));var initial=stats;
                brush.Version=2;Assert.Equal(unchecked((int)0x80004002),SetBrush(p,1,ref brush));brush.Version=1;
                brush.Count=9;Assert.Equal(unchecked((int)0x80070057),SetBrush(p,1,ref brush));brush.Count=3;
                foreach(var bad in new[]{float.NaN,float.PositiveInfinity,-.1f,1.1f}){stops[1].Offset=bad;Assert.Equal(unchecked((int)0x80070057),SetBrush(p,1,ref brush));}stops[1].Offset=.55f;
                stops[1].A=2;Assert.Equal(unchecked((int)0x80070057),SetBrush(p,1,ref brush));stops[1].A=24/255f;
                brush.X2=0;Assert.Equal(unchecked((int)0x80070057),SetBrush(p,1,ref brush));brush.X2=.72f;
                Assert.Equal(0,DirectPresenterTests.Clear(p,0,0,0,0));Assert.Equal(0,DirectVectorTests.Draw(p,new(){Key=1,Width=48,Height=64,Opacity=.46f}));
                var after=new byte[40000];Assert.Equal(0,DirectGlassTests.ReadPixels(p,after,40000,400));Assert.Equal(before,after);
                Assert.Equal(0,DirectVectorTests.Statistics(p,ref stats));Assert.Equal(initial.GeometryBuilds,stats.GeometryBuilds);Assert.Equal(initial.CoverageBuilds,stats.CoverageBuilds);Assert.Equal(initial.CacheBytes,stats.CacheBytes);
            }
            finally{pin.Free();}
        }
        finally{DirectPresenterTests.Destroy(p);host.Close();}
    });

    [Theory]
    [InlineData(2,48f,.46f,false)]
    [InlineData(2,55.2f,.18f,true)]
    [InlineData(1,28f,1f,false)]
    [InlineData(1,32.2f,.4f,true)]
    [InlineData(3,28f,1f,true)]
    public void ProductionPressGlowAndEdgeFadesMatchNormalGpuOutput(int kind,float width,float opacity,bool background)=>NativeEngineTests.OnSta(()=>
    {
        var library=NativeLibrary.Load(Api.LibraryPath);
        try{Assert.True(NativeLibrary.TryGetExport(library,"GD_PresenterSetVectorBrush",out _),"Production gradient brushes are missing from direct rendering.");}
        finally{NativeLibrary.Free(library);}
        const float height=64;var color=background?Color.FromArgb(220,40,90,130):Colors.Transparent;
        var brush=ProductionBrush(kind);brush.Opacity=opacity;
        var root=new Canvas{Background=new SolidColorBrush(color)};
        var panel=new Border{Width=width,Height=height,Background=brush};root.Children.Add(panel);Canvas.SetLeft(panel,12.35);Canvas.SetTop(panel,6.2);
        var reference=ManagedVectorTests.Capture(root);var host=DirectShadowTests.Host(80,80);host.Show();
        try
        {
            var hwnd=new WindowInteropHelper(host).Handle;var create=Info.Create((uint)reference.Width,(uint)reference.Height,hwnd);
            Assert.Equal(0,DirectPresenterTests.Create(ref create,out var p));
            try
            {
                var segments=new[]{DirectVectorTests.Move(0,0),DirectVectorTests.Line(width,0),DirectVectorTests.Line(width,height),DirectVectorTests.Line(0,height),new DirectVectorTests.Segment{Operation=3}};
                var pin=GCHandle.Alloc(segments,GCHandleType.Pinned);
                try{var info=new DirectVectorTests.VectorInfo{Size=72,Version=1,Width=width,Height=height,FillR=1,FillG=1,FillB=1,FillA=1,Count=5,Segments=pin.AddrOfPinnedObject()};Assert.Equal(0,DirectVectorTests.SetVector(p,1,ref info));}
                finally{pin.Free();}
                var stops=Stops(kind);pin=GCHandle.Alloc(stops,GCHandleType.Pinned);
                try{var paint=BrushInfo.Create(kind,pin.AddrOfPinnedObject(),(uint)stops.Length);Assert.Equal(0,SetBrush(p,1,ref paint));}
                finally{pin.Free();}
                Assert.Equal(0,DirectPresenterTests.Clear(p,color.R/255f,color.G/255f,color.B/255f,color.A/255f));
                Assert.Equal(0,DirectVectorTests.Draw(p,new(){Key=1,X=12.35f,Y=6.2f,Width=width,Height=height,Opacity=opacity}));Assert.Equal(0,DirectPresenterTests.Present(p));
                var actual=WgcWindowCapture.Capture(hwnd,TimeSpan.FromSeconds(3))??throw new InvalidOperationException("Gradient output missing.");
                ManagedVectorTests.Compare($"gradient-{kind}-{width}-{opacity}-{background}",reference,actual);
                Assert.Equal(unchecked((int)0x80070005),DirectPresenterTests.ReadPixel(p,1,1,out _));
            }
            finally{DirectPresenterTests.Destroy(p);}
        }
        finally{host.Close();}
    });

    internal static GradientBrush ProductionBrush(int kind)
    {
        if(kind==2)return new RadialGradientBrush{Center=new(.5,.48),GradientOrigin=new(.5,.48),RadiusX=.72,RadiusY=.72,
            GradientStops=[new(Color.FromArgb(75,255,255,255),0),new(Color.FromArgb(24,255,255,255),.55),new(Colors.Transparent,1)]};
        return new LinearGradientBrush{StartPoint=new(kind==3?1:0,.5),EndPoint=new(kind==3?0:1,.5),
            GradientStops=[new(Color.FromArgb(136,0,0,0),0),new(Color.FromArgb(0,0,0,0),1)]};
    }
    internal static Stop[] Stops(int kind)=>kind==2?[new(0,1,1,1,75/255f),new(.55f,1,1,1,24/255f),new(1,1,1,1,0)]:[new(0,0,0,0,136/255f),new(1,0,0,0,0)];
    [StructLayout(LayoutKind.Sequential)]internal struct Stop(float offset,float r,float g,float b,float a){internal float Offset=offset,R=r,G=g,B=b,A=a;internal uint Reserved=0;}
    [StructLayout(LayoutKind.Sequential)]internal struct BrushInfo
    {
        internal uint Size,Version,Kind,Count;internal float X1,Y1,X2,Y2;internal nint Stops;
        internal static BrushInfo Create(int kind,nint stops,uint count)=>new(){Size=40,Version=1,Kind=kind==2?2u:1u,Count=count,X1=kind==2?.5f:kind==3?1:0,Y1=kind==2?.48f:.5f,X2=kind==2?.72f:kind==3?0:1,Y2=kind==2?.72f:.5f,Stops=stops};
    }
    [DllImport("GlassDesk.Rendering.Native.dll",EntryPoint="GD_PresenterSetVectorBrush",CallingConvention=CallingConvention.Cdecl)]internal static extern int SetBrush(nint p,uint key,ref BrushInfo brush);
}
