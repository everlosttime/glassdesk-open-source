using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassDesk.UI.Controls;

namespace GlassDesk.NativeRendering.Tests;

public sealed class DirectGlassTests
{
    [Theory]
    [InlineData(0,84)]
    [InlineData(1,84)]
    [InlineData(15.999f,84)]
    [InlineData(26,30)]
    public void SmallRadiusOpticsRequireLegacyWithoutMutatingTheCandidate(float radius,float panelHeight) => NativeEngineTests.OnSta(() =>
    {
        var hwnd=CreateWindowEx(0x00200080,"STATIC","Optical fallback",unchecked((uint)0x80000000),0,0,160,100,0,0,0,0);
        Assert.NotEqual(0,hwnd);var info=Info.Create(160,100,hwnd);info.Flags=1;
        Assert.Equal(0,DirectPresenterTests.Create(ref info,out var presenter));
        try
        {
            Assert.Equal(0,DirectPresenterTests.Clear(presenter,.8f,.4f,.2f,.5f));
            var before=new byte[160*100*4];Assert.Equal(0,ReadPixels(presenter,before,(ulong)before.Length,160*4));
            var material=new StaticMaterialTests.Material{Size=48,Version=1,DipWidth=216,DipHeight=156,Strength=1,Clarity=.5f,TintOpacity=.16f};
            var scene=new GlassScene{Size=80,Version=2,Outline=1,HostWidth=160,HostHeight=100,PanelX=8,PanelY=8,PanelWidth=144,PanelHeight=panelHeight,
                BackdropX=-28,BackdropY=-28,BackdropWidth=216,BackdropHeight=156,Radius=radius,Strength=1,Dispersion=1,InnerGain=1,OuterGain=1};
            Assert.Equal(unchecked((int)0x80004001),RenderGlass(presenter,ref material,ref scene));
            var after=new byte[before.Length];Assert.Equal(0,ReadPixels(presenter,after,(ulong)after.Length,160*4));Assert.True(before.AsSpan().SequenceEqual(after));
            var stats=new MaterialStats{Size=40,Version=1};Assert.Equal(0,MaterialStatistics(presenter,ref stats));
            Assert.Equal(0ul,stats.FilterRuns);Assert.Equal(0ul,stats.SourceBytes);Assert.Equal(0ul,stats.GlassDraws);
        }
        finally{DirectPresenterTests.Destroy(presenter);DestroyWindow(hwnd);}
    });
    [Fact]
    public void InvalidOpticalCoordinateDomainsAreRejectedBeforeGpuWork()
    {
        var material=new StaticMaterialTests.Material{Size=48,Version=1,DipWidth=100,DipHeight=100,Strength=1,Clarity=.5f,TintOpacity=.16f};
        var scene=new GlassScene{Size=80,Version=1,HostWidth=float.Epsilon,HostHeight=100,PanelWidth=100,PanelHeight=100,
            BackdropWidth=100,BackdropHeight=100,Strength=1,Dispersion=1,InnerGain=1,OuterGain=1};
        Assert.Equal(unchecked((int)0x80070057),RenderGlass(0,ref material,ref scene));
        scene.HostWidth=100;scene.PanelWidth=float.MaxValue;
        Assert.Equal(unchecked((int)0x80070057),RenderGlass(0,ref material,ref scene));
        scene.PanelWidth=100;scene.Dispersion=float.MaxValue;
        Assert.Equal(unchecked((int)0x80070057),RenderGlass(0,ref material,ref scene));
        scene.Dispersion=1;scene.Outline=1;
        Assert.Equal(unchecked((int)0x80070057),RenderGlass(0,ref material,ref scene));
        scene.Version=2;scene.Outline=2;
        Assert.Equal(unchecked((int)0x80070057),RenderGlass(0,ref material,ref scene));
        scene.Outline=1;
        Assert.Equal(unchecked((int)0x80004003),RenderGlass(0,ref material,ref scene));
    }
    [Theory]
    [InlineData(16,false,false,false)]
    [InlineData(16,false,true,false)]
    [InlineData(16,true,true,false)]
    [InlineData(26,false,false,false)]
    [InlineData(40,false,false,false)]
    [InlineData(26,true,false,false)]
    [InlineData(26,false,true,false)]
    [InlineData(40,false,true,false)]
    [InlineData(26,false,true,true)]
    [InlineData(26,false,false,true)]
    public void NativeLensMatchesExistingProductionShaderAndPreservesTransparentCorners(float radius,bool launchpad,bool compact,bool fractional) => NativeEngineTests.OnSta(() =>
    {
        const int w=160,h=100,sw=216,sh=156;
        var native=CreateWindowEx(0x00200080,"STATIC","Direct glass",unchecked((uint)0x80000000),0,0,w,h,0,0,0,0);
        Assert.NotEqual(0,native);
        var info=Info.Create(w,h,native);info.Flags=1;
        Assert.Equal(0,DirectPresenterTests.Create(ref info,out var p));
        try
        {
            var library=NativeLibrary.Load(Api.LibraryPath);
            try
            {
                Assert.True(NativeLibrary.TryGetExport(library,"GD_PresenterRenderGlass",out _),"Production lens is not connected to native direct output.");
                Assert.True(NativeLibrary.TryGetExport(library,"GD_PresenterReadPixels",out _),"Whole-frame alpha comparison is missing.");
                Assert.True(NativeLibrary.TryGetExport(library,"GD_PresenterGetMaterialStatistics",out _),"Static material reuse has no verification boundary.");
            }
            finally{NativeLibrary.Free(library);}
            var source=new byte[sw*sh*4];
            for(var y=0;y<sh;y++)for(var x=0;x<sw;x++)
            {var i=(y*sw+x)*4;source[i]=(byte)((x*7)%256);source[i+1]=(byte)((y*9)%256);source[i+2]=(byte)((x/7+y/7)%2*255);source[i+3]=255;}
            var pin=GCHandle.Alloc(source,GCHandleType.Pinned);
            try{var upload=new StaticMaterialTests.Upload{Size=32,Version=1,Width=sw,Height=sh,Stride=sw*4,Pixels=pin.AddrOfPinnedObject()};Assert.Equal(0,DirectPresenterTests.UploadBitmap(p,ref upload));}
            finally{pin.Free();}
            var material=new StaticMaterialTests.Material{Size=48,Version=1,DipWidth=sw,DipHeight=sh,Strength=1,Clarity=.5f,TintOpacity=.16f,Dark=1};
            var scene=new GlassScene{Size=80,Version=compact?2u:1u,Outline=compact?1u:0u,HostWidth=w,HostHeight=h,PanelX=fractional?8.35f:8,PanelY=fractional?8.2f:8,PanelWidth=144,PanelHeight=84,
                BackdropX=-28,BackdropY=-28,BackdropWidth=sw,BackdropHeight=sh,Radius=radius,Strength=1,Dispersion=1,InnerGain=1,OuterGain=1,Launchpad=launchpad?1u:0u};
            Assert.Equal(0,RenderGlass(p,ref material,ref scene));
            Assert.Equal(0,DirectPresenterTests.ReadPixel(p,0,0,out var transparent));Assert.Equal(0u,transparent);

            var filtered=new byte[source.Length];var scratch=new byte[source.Length];
            AndroidLiquidGlassRenderer.RenderTransitionBackdropInto(source,filtered,scratch,sw,sh,new(sw,sh),true,1,backgroundClarity:.5);
            var bitmap=BitmapSource.Create(sw,sh,96,96,PixelFormats.Bgra32,null,filtered,sw*4);bitmap.Freeze();
            var layer=new TransitionGlassLayer{Width=sw,Height=sh,Opacity=1};layer.SetBackdrop(bitmap);
            var panel=new Rect(scene.PanelX,scene.PanelY,144,84);var back=new Rect(-28,-28,sw,sh);
            layer.SetPanel(panel,back,radius,1,launchpad);
            var root=new Canvas{Width=w,Height=h,Clip=compact?SquircleGlassGeometry.CreateCompact(panel,radius):SquircleGlassGeometry.Create(panel,radius)};
            root.Children.Add(layer);Canvas.SetLeft(layer,-28);Canvas.SetTop(layer,-28);
            root.Measure(new(w,h));root.Arrange(new(0,0,w,h));root.UpdateLayout();
            var host=new Window{Content=root,Width=w,Height=h,WindowStyle=WindowStyle.None,AllowsTransparency=true,Background=Brushes.Transparent,
                ShowActivated=false,ShowInTaskbar=false,Left=0,Top=0};
            CapturePixels reference;
            try
            {
                host.Show();host.UpdateLayout();
                reference=WgcWindowCapture.Capture(new System.Windows.Interop.WindowInteropHelper(host).Handle,TimeSpan.FromSeconds(3))
                    ?? throw new InvalidOperationException("The owned hardware reference window did not produce a frame.");
            }
            finally{host.Close();}
            var expected=reference.Pixels;
            var outputWidth=reference.Width;var outputHeight=reference.Height;
            if(outputWidth!=w || outputHeight!=h)
            {
                Assert.Equal(0,DirectPresenterTests.Resize(p,(uint)outputWidth,(uint)outputHeight));
                pin=GCHandle.Alloc(source,GCHandleType.Pinned);
                try{var upload=new StaticMaterialTests.Upload{Size=32,Version=1,Width=sw,Height=sh,Stride=sw*4,Pixels=pin.AddrOfPinnedObject()};Assert.Equal(0,DirectPresenterTests.UploadBitmap(p,ref upload));}
                finally{pin.Free();}
                Assert.Equal(0,RenderGlass(p,ref material,ref scene));
            }
            var observations=new List<object>();
            foreach(var point in new[]{(80u,50u),(40u,40u),(28u,50u),(12u,50u),(80u,12u)})
            {
                var sx=(uint)(point.Item1*outputWidth/w);var sy=(uint)(point.Item2*outputHeight/h);
                DirectPresenterTests.ReadPixel(p,sx,sy,out var sample);
                var offset=(int)((sy*outputWidth+sx)*4);
                observations.Add(new{x=sx,y=sy,width=outputWidth,height=outputHeight,native=BitConverter.GetBytes(sample),wpf=expected.AsSpan(offset,4).ToArray(),unrefracted=filtered.AsSpan((((int)point.Item2+28)*sw+(int)point.Item1+28)*4,4).ToArray()});
            }
            System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Api.LibraryPath)!,"lens-samples.json"),System.Text.Json.JsonSerializer.Serialize(observations));
            Assert.True(expected[(outputHeight/2*outputWidth+outputWidth/2)*4+3]>240,"WPF reference failed to render the production material.");
            double error=0;var channels=0;var maximum=0;
            for(uint y=12;y<outputHeight-12;y+=4)for(uint x=12;x<outputWidth-12;x+=4)
            {
                if(GlassSignedDistance.Squircle(panel,radius,new((x+.5)*w/outputWidth,(y+.5)*h/outputHeight))>-2)continue;
                Assert.Equal(0,DirectPresenterTests.ReadPixel(p,x,y,out var color));var actual=BitConverter.GetBytes(color);
                for(var c=0;c<4;c++){var d=Math.Abs(actual[c]-expected[(y*outputWidth+x)*4+c]);error+=d;maximum=Math.Max(maximum,d);channels++;}
            }
            var nativePixels=new byte[outputWidth*outputHeight*4];
            Assert.Equal(0,ReadPixels(p,nativePixels,(ulong)nativePixels.Length,(uint)outputWidth*4));
            var alphaMaximum=0;var alphaTotal=0;
            for(var i=3;i<nativePixels.Length;i+=4){var d=Math.Abs(nativePixels[i]-expected[i]);alphaMaximum=Math.Max(alphaMaximum,d);alphaTotal+=d;}
            var fullMaximum=0;long fullTotal=0;
            for(var i=0;i<nativePixels.Length;i++){var d=Math.Abs(nativePixels[i]-expected[i]);fullMaximum=Math.Max(fullMaximum,d);fullTotal+=d;}
            var evidence=System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Api.LibraryPath)!,"direct-presentation-tests",$"lens-r{radius}-launchpad-{launchpad}"+(compact?$"-compact-fractional-{fractional}":fractional?"-fractional-True":""));System.IO.Directory.CreateDirectory(evidence);
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(evidence,"lens-wpf.bgra"),expected);
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(evidence,"lens-native.bgra"),nativePixels);
            System.IO.File.WriteAllText(System.IO.Path.Combine(evidence,"lens-comparison.json"),System.Text.Json.JsonSerializer.Serialize(new{outputWidth,outputHeight,interiorMean=error/channels,interiorMaximum=maximum,alphaMaximum,alphaMean=(double)alphaTotal/(outputWidth*outputHeight),fullMaximum,fullMean=(double)fullTotal/nativePixels.Length}));
            Assert.True(error/channels<2,$"Mean channel difference {error/channels:F3}; max {maximum}.");
            Assert.InRange(maximum,0,8);
            Assert.InRange(alphaMaximum,0,8);
            Assert.InRange(fullMaximum,0,8);Assert.True((double)fullTotal/nativePixels.Length<1.5);
            if(compact && radius==26 && !fractional)
            {
                var rejected=0;
                foreach(var hostWidth in new[]{32f,16f,8f,4f,1f})
                {
                    var oversized=scene;oversized.HostWidth=hostWidth;oversized.HostHeight=hostWidth*outputHeight/outputWidth;
                    oversized.PanelWidth=oversized.PanelHeight=32768;oversized.Radius=16384;
                    var result=RenderGlass(p,ref material,ref oversized);
                    Assert.True(result==0 || result==unchecked((int)0x80070057),$"Unexpected bounded-contour result {result:X8}.");
                    if(result!=0)rejected++;
                    Assert.Equal(0,RenderGlass(p,ref material,ref scene));
                    var recovered=new byte[nativePixels.Length];
                    Assert.Equal(0,ReadPixels(p,recovered,(ulong)recovered.Length,(uint)outputWidth*4));
                    Assert.True(nativePixels.AsSpan().SequenceEqual(recovered),$"Contour capacity rejection corrupted the cached scene at host width {hostWidth}.");
                }
                Assert.True(rejected>0,"Extreme geometry did not exercise the bounded contour allocation.");
            }
            scene.PanelX+=1;
            Assert.Equal(0,RenderGlass(p,ref material,ref scene));
            var stats=new MaterialStats{Size=40,Version=1};Assert.Equal(0,MaterialStatistics(p,ref stats));
            Assert.Equal(1ul,stats.FilterRuns);
            material.Clarity=1;
            Assert.Equal(0,RenderGlass(p,ref material,ref scene));Assert.Equal(0,MaterialStatistics(p,ref stats));
            Assert.Equal(2ul,stats.FilterRuns);
            source[0]^=0xff;
            pin=GCHandle.Alloc(source,GCHandleType.Pinned);
            try{var upload=new StaticMaterialTests.Upload{Size=32,Version=1,Width=sw,Height=sh,Stride=sw*4,Pixels=pin.AddrOfPinnedObject()};Assert.Equal(0,DirectPresenterTests.UploadBitmap(p,ref upload));}
            finally{pin.Free();}
            Assert.Equal(0,RenderGlass(p,ref material,ref scene));Assert.Equal(0,MaterialStatistics(p,ref stats));
            Assert.Equal(3ul,stats.FilterRuns);
        }
        finally{DirectPresenterTests.Destroy(p);DestroyWindow(native);}
    });

    [StructLayout(LayoutKind.Sequential)] internal struct GlassScene
    {
        internal uint Size,Version;
        internal float HostWidth,HostHeight,PanelX,PanelY,PanelWidth,PanelHeight;
        internal float BackdropX,BackdropY,BackdropWidth,BackdropHeight,Radius,Strength,Dispersion,InnerGain,OuterGain;
        internal uint Launchpad,Outline,Reserved2;
    }
    [DllImport("GlassDesk.Rendering.Native.dll",EntryPoint="GD_PresenterRenderGlass",CallingConvention=CallingConvention.Cdecl)]
    internal static extern int RenderGlass(nint presenter,ref StaticMaterialTests.Material material,ref GlassScene scene);
    [DllImport("GlassDesk.Rendering.Native.dll",EntryPoint="GD_PresenterReadPixels",CallingConvention=CallingConvention.Cdecl)]
    internal static extern int ReadPixels(nint presenter,[Out] byte[] pixels,ulong capacity,uint stride);
    [StructLayout(LayoutKind.Sequential)] internal struct MaterialStats
    { internal uint Size,Version;internal ulong FilterRuns,SourceBytes,ScratchBytes,GlassDraws; }
    [DllImport("GlassDesk.Rendering.Native.dll",EntryPoint="GD_PresenterGetMaterialStatistics",CallingConvention=CallingConvention.Cdecl)]
    internal static extern int MaterialStatistics(nint presenter,ref MaterialStats stats);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern nint CreateWindowEx(uint ex,string cls,string name,uint style,int x,int y,int w,int h,nint parent,nint menu,nint instance,nint arg);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(nint hwnd);
}
