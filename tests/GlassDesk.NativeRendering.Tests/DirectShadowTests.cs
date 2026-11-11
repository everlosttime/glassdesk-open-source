using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using GlassDesk.UI.Controls;
using GlassDesk.Rendering;

namespace GlassDesk.NativeRendering.Tests;

public sealed class DirectShadowTests
{
    [Theory]
    [InlineData(0,false,false,false,1)]
    [InlineData(26,false,false,false,1)]
    [InlineData(40,false,false,true,1)]
    [InlineData(26,true,false,false,1)]
    [InlineData(40,true,false,true,1)]
    [InlineData(26,true,true,true,1)]
    [InlineData(26,true,true,true,6)]
    [InlineData(0,true,false,true,6)]
    public void NativeShadowMatchesProductionHardwareDrawing(float radius,bool compact,bool reduced,bool fractional,int scale) => NativeEngineTests.OnSta(() =>
    {
        var library=NativeLibrary.Load(Api.LibraryPath);
        try{Assert.True(NativeLibrary.TryGetExport(library,"GD_PresenterRenderShadow",out _),"Production shadow has no native presentation path.");}
        finally{NativeLibrary.Free(library);}
        var w=264*scale;var h=180*scale;radius*=scale;
        var panel=new Rect((fractional?42.35:42)*scale,(fractional?42.2:42)*scale,180*scale,84*scale);
        var reference=Reference(w,h,panel,radius,compact,reduced,LiquidGlassDebugView.None,LiquidGlassFinishingParameters.Default,1);
        Assert.True(reference.Pixels.Where((_,i)=>i%4==3).Max()>10,"Reference shadow did not render.");
        var host=Host(w,h);host.Show();
        var info=Info.Create((uint)reference.Width,(uint)reference.Height,new System.Windows.Interop.WindowInteropHelper(host).Handle);info.Flags=1;
        Assert.Equal(0,DirectPresenterTests.Create(ref info,out var p));
        try
        {
            var layers=DefaultLayers();var pin=GCHandle.Alloc(layers,GCHandleType.Pinned);
            try
            {
                var scene=Scene.Create(w,h,panel,radius,compact,reduced,pin.AddrOfPinnedObject(),(uint)layers.Length);
                Assert.Equal(0,Render(p,ref scene));
                var pixels=new byte[reference.Pixels.Length];Assert.Equal(0,DirectGlassTests.ReadPixels(p,pixels,(ulong)pixels.Length,(uint)reference.Width*4));
                double sum=0;int maximum=0;var worst=0;
                for(var i=0;i<pixels.Length;i++){var error=Math.Abs(pixels[i]-reference.Pixels[i]);sum+=error;if(error>maximum){maximum=error;worst=i;}}
                var path=System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Api.LibraryPath)!,"shadow-parity");System.IO.Directory.CreateDirectory(path);
                var label=$"r{radius}-c{compact}-s{reduced}-f{fractional}-scale{scale}";
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(path,label+"-native.bgra"),pixels);System.IO.File.WriteAllBytes(System.IO.Path.Combine(path,label+"-wpf.bgra"),reference.Pixels);
                System.IO.File.WriteAllText(System.IO.Path.Combine(path,label+".json"),System.Text.Json.JsonSerializer.Serialize(new{mean=sum/pixels.Length,maximum,reference.Width,reference.Height,x=worst/4%reference.Width,y=worst/4/reference.Width,channel=worst%4,native=pixels[worst],wpf=reference.Pixels[worst]}));
                Assert.True(sum/pixels.Length<1.5,$"Mean shadow channel error {sum/pixels.Length:F4}.");
                Assert.True(maximum<=8,$"Maximum shadow channel error {maximum}.");
                var center=(reference.Height/2*reference.Width+reference.Width/2)*4;Assert.Equal(0,pixels[center+3]);
                var stats=new Stats{Size=48,Version=1};Assert.Equal(0,Statistics(p,ref stats));Assert.Equal(1ul,stats.GeometryBuilds);Assert.Equal(1ul,stats.Draws);
                Assert.Equal(1ul,stats.TargetAllocations);Assert.Equal((ulong)reference.Width*(ulong)reference.Height*4,stats.TargetBytes);
                scene.PanelX+=1;scene.Opacity=.4f;Assert.Equal(0,Render(p,ref scene));
                Assert.Equal(0,Statistics(p,ref stats));Assert.Equal(1ul,stats.GeometryBuilds);Assert.Equal(2ul,stats.Draws);Assert.Equal(1ul,stats.TargetAllocations);
                Assert.Equal(0,DirectPresenterTests.Resize(p,(uint)reference.Width+8,(uint)reference.Height+8));
                Assert.Equal(0,Statistics(p,ref stats));Assert.Equal(0ul,stats.TargetBytes);
                Assert.Equal(0,Render(p,ref scene));Assert.Equal(0,Statistics(p,ref stats));Assert.Equal(1ul,stats.GeometryBuilds);
                scene.Radius+=1;Assert.Equal(0,Render(p,ref scene));Assert.Equal(0,Statistics(p,ref stats));Assert.Equal(2ul,stats.GeometryBuilds);
                Assert.Equal(2ul,stats.TargetAllocations);Assert.Equal((ulong)(reference.Width+8)*(ulong)(reference.Height+8)*4,stats.TargetBytes);
                pixels=new byte[(reference.Width+8)*(reference.Height+8)*4];Assert.Equal(0,DirectGlassTests.ReadPixels(p,pixels,(ulong)pixels.Length,(uint)(reference.Width+8)*4));
                scene.PanelX=float.NaN;Assert.Equal(unchecked((int)0x80070057),Render(p,ref scene));
                var after=new byte[pixels.Length];Assert.Equal(0,DirectGlassTests.ReadPixels(p,after,(ulong)after.Length,(uint)(reference.Width+8)*4));Assert.Equal(pixels,after);
            }
            finally{pin.Free();}
        }
        finally{DirectPresenterTests.Destroy(p);host.Close();}
    });

    [Theory]
    [InlineData(LiquidGlassDebugView.None,1,false)]
    [InlineData(LiquidGlassDebugView.None,.4f,true)]
    [InlineData(LiquidGlassDebugView.AmbientShadowOnly,1,true)]
    [InlineData(LiquidGlassDebugView.ContactShadowOnly,.4f,true)]
    [InlineData(LiquidGlassDebugView.BrdfSupportWeight,1,false)]
    public void ManagedShadowPresentsProductionAppearanceWithoutCpuReadback(LiquidGlassDebugView view,float opacity,bool extreme) => NativeEngineTests.OnSta(() =>
    {
        const int w=264,h=180;var panel=new Rect(42.35,42.2,180,84);
        var parameters=LiquidGlassFinishingParameters.Default;
        if(extreme)parameters=parameters with{AmbientShadowOpacity=.12,AmbientShadowExtentDip=34,AmbientShadowOffsetDip=8,
            ContactShadowOpacity=.10,ContactShadowExtentDip=10,ContactShadowOffsetDip=3};
        var reference=Reference(w,h,panel,26,true,false,view,parameters,opacity);
        var host=Host(w,h);host.Show();host.UpdateLayout();
        try
        {
            using var presenter=new NativeDirectPresenter(new System.Windows.Interop.WindowInteropHelper(host).Handle,reference.Width,reference.Height);
            Span<DirectShadowLayer> layers=new DirectShadowLayer[4];var count=GlassShadow.CopyDirectLayers(layers,view,parameters,1);
            var scene=new DirectShadowScene(new(w,h),panel,26,opacity);
            presenter.RenderShadow(scene,layers[..count]);Assert.True(presenter.Present());
            var capture=WgcWindowCapture.Capture(new System.Windows.Interop.WindowInteropHelper(host).Handle,TimeSpan.FromSeconds(3))
                ?? throw new InvalidOperationException("Native normal-path shadow produced no WGC frame.");
            Assert.Equal(reference.Width,capture.Width);Assert.Equal(reference.Height,capture.Height);
            var maximum=0;double sum=0;
            for(var i=0;i<reference.Pixels.Length;i++){var error=Math.Abs(capture.Pixels[i]-reference.Pixels[i]);maximum=Math.Max(maximum,error);sum+=error;}
            Assert.True(sum/reference.Pixels.Length<1.5,$"Presented mean {sum/reference.Pixels.Length:F4}.");Assert.True(maximum<=8,$"Presented maximum {maximum}.");
            var evidence=System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Api.LibraryPath)!,"shadow-parity");System.IO.Directory.CreateDirectory(evidence);
            System.IO.File.WriteAllText(System.IO.Path.Combine(evidence,$"presented-{view}-{opacity}-{extreme}.json"),System.Text.Json.JsonSerializer.Serialize(new{view=view.ToString(),opacity,extreme,mean=sum/reference.Pixels.Length,maximum,capture.Width,capture.Height,count,presenter.ShadowStatistics}));
            Assert.Equal((uint)(count*32),presenter.ShadowStatistics.ContourCount);
            Assert.IsAssignableFrom<InvalidOperationException>(Task.Run(()=>Record.Exception(()=>{_=presenter.ShadowStatistics;})).GetAwaiter().GetResult());
            presenter.Dispose();Assert.Throws<ObjectDisposedException>(()=>{_=presenter.ShadowStatistics;});
        }
        finally{host.Close();}
    });

    [Fact]
    public void InvalidShadowAbiAndLayersAreRejectedBeforeAllocation()
    {
        var scene=Scene.Create(264,180,new(42,42,180,84),26,true,false,0,0);
        scene.Version=2;Assert.Equal(unchecked((int)0x80004002),Render(0,ref scene));scene.Version=1;
        scene.Size=63;Assert.Equal(unchecked((int)0x80070057),Render(0,ref scene));scene.Size=64;
        scene.Count=1;Assert.Equal(unchecked((int)0x80070057),Render(0,ref scene));scene.Count=0;
        scene.Steps=8;Assert.Equal(unchecked((int)0x80070057),Render(0,ref scene));scene.Steps=32;
        var layers=DefaultLayers();var pin=GCHandle.Alloc(layers,GCHandleType.Pinned);
        try
        {
            scene.Count=1;scene.Layers=pin.AddrOfPinnedObject();
            foreach(var bad in new[]{float.NaN,float.PositiveInfinity,0,-1f,1025f})
            {layers[0].Extent=bad;Assert.Equal(unchecked((int)0x80070057),Render(0,ref scene));}
            layers[0].Extent=34;layers[0].R=1.1f;Assert.Equal(unchecked((int)0x80070057),Render(0,ref scene));
            layers[0].R=0;Assert.Equal(unchecked((int)0x80004003),Render(0,ref scene));
        }
        finally{pin.Free();}
    }

    private static CapturePixels Reference(int w,int h,Rect panel,float radius,bool compact,bool reduced,
        LiquidGlassDebugView view,LiquidGlassFinishingParameters parameters,float opacity)
    {
        var type=typeof(GlassShadow).GetNestedType("ShadowDrawing",BindingFlags.NonPublic)!;
        var drawing=(FrameworkElement)Activator.CreateInstance(type,true)!;drawing.Opacity=opacity;
        void Invoke(string name,params object[] args)=>type.GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(drawing,args);
        var owner=new ShadowOwner();Invoke("SetGeometry",owner,panel,(double)radius,new Size(w,h),1d);
        Invoke("SetCompactShape",compact);Invoke("SetReducedDetail",reduced);Invoke("SetAppearance",view,parameters);
        var host=Host(w,h);host.Content=drawing;
        try
        {
            host.Show();host.UpdateLayout();return WgcWindowCapture.Capture(new System.Windows.Interop.WindowInteropHelper(host).Handle,TimeSpan.FromSeconds(3))
                ?? throw new InvalidOperationException("Production shadow reference produced no WGC frame.");
        }
        finally{host.Close();owner.Close();}
    }
    private sealed class ShadowOwner:GlassWindow { }
    internal static Window Host(int w,int h)=>new(){Width=w,Height=h,Left=0,Top=0,WindowStyle=WindowStyle.None,AllowsTransparency=true,Background=Brushes.Transparent,ShowActivated=false,ShowInTaskbar=false};
    internal static Layer[] DefaultLayers()=>[new(34,8,.15f,18,24,36),new(10,2,.10f,18,24,36),new(32,6,.06f,18,24,36),new(6,1,.045f,12,17,27)];
    [StructLayout(LayoutKind.Sequential)] internal struct Layer
    {
        internal float Extent,Offset,Opacity,R,G,B,A;internal uint Reserved;
        internal Layer(float extent,float offset,float opacity,byte r,byte g,byte b){Extent=extent;Offset=offset;Opacity=opacity;R=r/255f;G=g/255f;B=b/255f;A=1;Reserved=0;}
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Scene
    {
        internal uint Size,Version;internal float HostWidth,HostHeight,PanelX,PanelY,PanelWidth,PanelHeight,Radius,Opacity;
        internal uint Outline,Steps,Count,Reserved;internal nint Layers;
        internal static Scene Create(int w,int h,Rect panel,float radius,bool compact,bool reduced,nint layers,uint count)=>new(){Size=64,Version=1,HostWidth=w,HostHeight=h,PanelX=(float)panel.X,PanelY=(float)panel.Y,PanelWidth=(float)panel.Width,PanelHeight=(float)panel.Height,Radius=radius,Opacity=1,Outline=compact?1u:0u,Steps=reduced?16u:32u,Count=count,Layers=layers};
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Stats{internal uint Size,Version,ContourCount,Reserved;internal ulong GeometryBuilds,Draws,TargetAllocations,TargetBytes;}
    [DllImport("GlassDesk.Rendering.Native.dll",EntryPoint="GD_PresenterRenderShadow",CallingConvention=CallingConvention.Cdecl)] internal static extern int Render(nint p,ref Scene scene);
    [DllImport("GlassDesk.Rendering.Native.dll",EntryPoint="GD_PresenterGetShadowStatistics",CallingConvention=CallingConvention.Cdecl)] internal static extern int Statistics(nint p,ref Stats stats);
}
