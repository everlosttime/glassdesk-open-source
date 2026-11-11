using System.Runtime.InteropServices;
using System.Windows;

namespace GlassDesk.NativeRendering.Tests;

public sealed class SharedDirectDeviceTests
{
    [Fact]
    public void SharedContextKeepsInterleavedMaterialImagesAndShadowsIndependent() => NativeEngineTests.OnSta(() =>
    {
        var hosts=Enumerable.Range(0,3).Select(_=>DirectShadowTests.Host(80,80)).ToArray();
        var handles=new nint[3];
        try
        {
            foreach(var host in hosts)host.Show();
            for(var i=0;i<handles.Length;i++)
            {
                var info=Info.Create(100,100,new System.Windows.Interop.WindowInteropHelper(hosts[i]).Handle);info.Flags=1;
                Assert.Equal(0,i==1?CreateSibling(handles[0],ref info,out handles[i]):DirectPresenterTests.Create(ref info,out handles[i]));
                var pixels=new byte[16*16*4];
                for(var j=0;j<pixels.Length;j+=4){pixels[j]=(byte)(j%256);pixels[j+1]=60;pixels[j+2]=180;pixels[j+3]=255;}
                var pin=GCHandle.Alloc(pixels,GCHandleType.Pinned);
                try{var upload=new StaticMaterialTests.Upload{Size=32,Version=1,Width=16,Height=16,Stride=64,Pixels=pin.AddrOfPinnedObject()};Assert.Equal(0,DirectPresenterTests.UploadBitmap(handles[i],ref upload));}
                finally{pin.Free();}
                DirectImageTests.Upload(handles[i],1,16,16,pixels);
                var segments=new[]{DirectVectorTests.Move(0,0),DirectVectorTests.Line(12,0),DirectVectorTests.Line(12,5),DirectVectorTests.Line(0,5),new DirectVectorTests.Segment{Operation=3}};
                var vectorPin=GCHandle.Alloc(segments,GCHandleType.Pinned);
                try{var vector=new DirectVectorTests.VectorInfo{Size=72,Version=1,Width=80,Height=80,FillB=1,FillA=.4f,Count=5,Segments=vectorPin.AddrOfPinnedObject()};Assert.Equal(0,DirectVectorTests.SetVector(handles[i],1,ref vector));}
                finally{vectorPin.Free();}
                var stops=DirectGradientTests.Stops(2);var brushPin=GCHandle.Alloc(stops,GCHandleType.Pinned);
                try{var brush=DirectGradientTests.BrushInfo.Create(2,brushPin.AddrOfPinnedObject(),3);Assert.Equal(0,DirectGradientTests.SetBrush(handles[i],1,ref brush));}
                finally{brushPin.Free();}
                DirectTextTests.Upload(handles[i],1,ManagedTextTests.MakeRun(out _,out _));
            }
            Assert.Equal(Snapshot(handles[0]).Identity,Snapshot(handles[1]).Identity);
            Assert.NotEqual(Snapshot(handles[0]).Identity,Snapshot(handles[2]).Identity);
            for(var iteration=0;iteration<12;iteration++)
            {
                Material(handles[0]);Shadow(handles[1]);Image(handles[0]);
                Material(handles[2]);Image(handles[2]);Assert.Equal(Pixels(handles[2]),Pixels(handles[0]));
                Material(handles[1]);Shadow(handles[0]);Image(handles[1]);
                Material(handles[2]);Image(handles[2]);Assert.Equal(Pixels(handles[2]),Pixels(handles[1]));
                Image(handles[0]);Shadow(handles[2]);Image(handles[2]);Assert.Equal(Pixels(handles[2]),Pixels(handles[0]));
            }
        }
        finally{foreach(var p in handles)if(p!=0)DirectPresenterTests.Destroy(p);foreach(var host in hosts)host.Close();}

        static void Material(nint p)
        {
            var material=new StaticMaterialTests.Material{Size=48,Version=1,DipWidth=80,DipHeight=80,Strength=1,Clarity=.5f,TintOpacity=.16f,Dark=1};
            var scene=new DirectGlassTests.GlassScene{Size=80,Version=2,HostWidth=80,HostHeight=80,PanelX=8,PanelY=12,PanelWidth=64,PanelHeight=56,
                BackdropWidth=80,BackdropHeight=80,Radius=26,Strength=1,Dispersion=1,InnerGain=1,OuterGain=1,Outline=1};
            Assert.Equal(0,DirectGlassTests.RenderGlass(p,ref material,ref scene));
        }
        static void Shadow(nint p)
        {
            var layers=DirectShadowTests.DefaultLayers();var pin=GCHandle.Alloc(layers,GCHandleType.Pinned);
            try{var scene=DirectShadowTests.Scene.Create(80,80,new(8,12,64,56),26,true,false,pin.AddrOfPinnedObject(),4);Assert.Equal(0,DirectShadowTests.Render(p,ref scene));}
            finally{pin.Free();}
        }
        static void Image(nint p)
        {
            var group=new DirectGroupTests.GroupInfo{Size=32,Version=1,Opacity=.7f,Bounded=1,Left=3,Top=2,Right=97,Bottom=99};
            Assert.Equal(0,DirectGroupTests.PushGroup(p,ref group));
            Assert.Equal(0,DirectImageTests.Submit(p,new DirectImageTests.ImageDraw{Key=1,X=15,Y=25,Width=35,Height=35,Opacity=.4f}));
            Assert.Equal(0,DirectVectorTests.Draw(p,new(){Key=1,X=35.2f,Y=20.2f,Width=80,Height=80,Opacity=.7f}));
            Assert.Equal(0,DirectTextTests.Draw(p,[new(){Key=1,X=12,Y=68,ScaleX=1,ScaleY=1,Opacity=.7f,R=1,G=1,B=1,A=1}]));
            Assert.Equal(0,DirectGroupTests.BeginMask(p));Assert.Equal(0,DirectPresenterTests.Clear(p,1,1,1,.8f));
            Assert.Equal(0,DirectGroupTests.PopGroup(p));
        }
        static byte[] Pixels(nint p){var pixels=new byte[100*100*4];Assert.Equal(0,DirectGlassTests.ReadPixels(p,pixels,(ulong)pixels.Length,400));return pixels;}
    });

    [Fact]
    public void SiblingSharesActualDeviceAndSurvivesParentReleaseAndResize() => NativeEngineTests.OnSta(() =>
    {
        var library=NativeLibrary.Load(Api.LibraryPath);
        try{Assert.True(NativeLibrary.TryGetExport(library,"GD_PresenterCreateSibling",out _),"Each native window still requires its own graphics device.");}
        finally{NativeLibrary.Free(library);}
        var first=DirectShadowTests.Host(264,180);var second=DirectShadowTests.Host(264,180);var independentHost=DirectShadowTests.Host(264,180);
        first.Show();second.Show();independentHost.Show();
        nint parent=0,child=0,third=0;
        try
        {
            var info=Info.Create(264,180,new System.Windows.Interop.WindowInteropHelper(first).Handle);info.Flags=1;
            Assert.Equal(0,DirectPresenterTests.Create(ref info,out parent));
            var initial=Snapshot(parent);Assert.Equal(1u,initial.Presenters);Assert.NotEqual(0ul,initial.Identity);
            info.Window=new System.Windows.Interop.WindowInteropHelper(second).Handle;
            Assert.Equal(0,CreateSibling(parent,ref info,out child));
            var shared=Snapshot(child);Assert.Equal(initial.Identity,shared.Identity);Assert.Equal(initial.AdapterLow,shared.AdapterLow);Assert.Equal(initial.AdapterHigh,shared.AdapterHigh);
            Assert.Equal(2u,shared.Presenters);Assert.Equal(initial.LiveDevices,shared.LiveDevices);
            info.Window=new System.Windows.Interop.WindowInteropHelper(independentHost).Handle;
            info.Flags=0;Assert.Equal(0,DirectPresenterTests.Create(ref info,out third));
            var independent=Snapshot(third);Assert.NotEqual(shared.Identity,independent.Identity);Assert.Equal(shared.LiveDevices+1,independent.LiveDevices);
            DirectPresenterTests.Destroy(third);third=0;Assert.Equal(shared.LiveDevices,Snapshot(child).LiveDevices);
            Assert.Equal(0,DirectPresenterTests.Clear(parent,1,0,0,1));Assert.Equal(0,DirectPresenterTests.Clear(child,0,0,1,.5f));
            Assert.Equal(0,DirectPresenterTests.ReadPixel(parent,1,1,out var red));Assert.Equal(0xffff0000u,red);
            Assert.Equal(0,DirectPresenterTests.ReadPixel(child,1,1,out var blue));Assert.InRange((int)(blue>>24),127,128);Assert.InRange((int)(blue&255),127,128);
            Assert.Equal(0,DirectPresenterTests.Resize(parent,300,200));
            Assert.Equal(0,DirectPresenterTests.ReadPixel(child,1,1,out var intact));Assert.Equal(blue,intact);
            var wrong=Task.Run(()=>{var s=new DeviceStats{Size=40,Version=1};return Statistics(child,ref s);}).GetAwaiter().GetResult();Assert.Equal(unchecked((int)0x8001010e),wrong);
            var retired=parent;parent=0;Task.Run(()=>DirectPresenterTests.Destroy(retired)).GetAwaiter().GetResult();
            Assert.Equal(1u,Snapshot(child).Presenters);Assert.Equal(initial.Identity,Snapshot(child).Identity);
            Assert.Equal(0,DirectPresenterTests.Resize(child,100,80));Assert.Equal(0,DirectPresenterTests.Clear(child,.2f,.4f,.8f,1));
            Assert.Equal(0,DirectPresenterTests.ReadPixel(child,20,20,out var survivor));Assert.Equal(0xff3366ccu,survivor);
            DirectPresenterTests.Destroy(child);child=0;
            info.Width=64;info.Height=32;Assert.Equal(0,DirectPresenterTests.Create(ref info,out third));
            Assert.NotEqual(initial.Identity,Snapshot(third).Identity);Assert.Equal(initial.LiveDevices,Snapshot(third).LiveDevices);
        }
        finally{if(parent!=0)DirectPresenterTests.Destroy(parent);if(child!=0)DirectPresenterTests.Destroy(child);if(third!=0)DirectPresenterTests.Destroy(third);first.Close();second.Close();independentHost.Close();}
    });

    [Fact]
    public void InvalidSiblingNeverPublishesAHandle() => NativeEngineTests.OnSta(() =>
    {
        var library=NativeLibrary.Load(Api.LibraryPath);
        try{Assert.True(NativeLibrary.TryGetExport(library,"GD_PresenterCreateSibling",out _));}finally{NativeLibrary.Free(library);}
        var host=DirectShadowTests.Host(64,32);host.Show();
        var info=Info.Create(64,32,new System.Windows.Interop.WindowInteropHelper(host).Handle);
        Assert.Equal(0,DirectPresenterTests.Create(ref info,out var parent));
        try
        {
            Assert.Equal(unchecked((int)0x80004003),CreateSibling(0,ref info,out var rejected));Assert.Equal(0,rejected);
            info.Version=99;Assert.Equal(unchecked((int)0x80004002),CreateSibling(parent,ref info,out rejected));Assert.Equal(0,rejected);info.Version=1;
            info.Window=0;Assert.Equal(unchecked((int)0x80070057),CreateSibling(parent,ref info,out rejected));Assert.Equal(0,rejected);
            info.Window=new System.Windows.Interop.WindowInteropHelper(host).Handle;
            var wrong=Task.Run(()=>{var copy=info;var result=CreateSibling(parent,ref copy,out var handle);return(result,handle);}).GetAwaiter().GetResult();
            Assert.Equal(unchecked((int)0x8001010e),wrong.result);Assert.Equal(0,wrong.handle);
            // DComp rejects a second target on the same HWND. Failure after
            // allocating a swapchain must also roll back session ownership.
            Assert.True(CreateSibling(parent,ref info,out rejected)<0);Assert.Equal(0,rejected);
            Assert.Equal(1u,Snapshot(parent).Presenters);
        }
        finally{DirectPresenterTests.Destroy(parent);host.Close();}
    });

    internal static DeviceStats Snapshot(nint p){var s=new DeviceStats{Size=40,Version=1};Assert.Equal(0,Statistics(p,ref s));return s;}
    [StructLayout(LayoutKind.Sequential)] internal struct DeviceStats
    {internal uint Size,Version;internal ulong Identity;internal uint AdapterLow;internal int AdapterHigh;internal uint OwnerThread,Presenters,LiveDevices,Reserved;}
    [DllImport("GlassDesk.Rendering.Native.dll",EntryPoint="GD_PresenterCreateSibling",CallingConvention=CallingConvention.Cdecl)] internal static extern int CreateSibling(nint parent,ref Info info,out nint p);
    [DllImport("GlassDesk.Rendering.Native.dll",EntryPoint="GD_PresenterGetDeviceStatistics",CallingConvention=CallingConvention.Cdecl)] internal static extern int Statistics(nint p,ref DeviceStats stats);
}
