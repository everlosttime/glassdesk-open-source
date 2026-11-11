using System.Runtime.InteropServices;

namespace GlassDesk.NativeRendering.Tests;

public sealed class DirectPresenterTests
{
    [Fact]
    public void WgcObservesPresentedNativeAlphaOnTransparentWpfHost() => NativeEngineTests.OnSta(() =>
    {
        var host=new System.Windows.Window{Width=64,Height=32,Left=0,Top=0,WindowStyle=System.Windows.WindowStyle.None,
            AllowsTransparency=true,Background=System.Windows.Media.Brushes.Transparent,ShowActivated=false,ShowInTaskbar=false};
        host.Show();host.UpdateLayout();
        var hwnd=new System.Windows.Interop.WindowInteropHelper(host).Handle;
        Assert.NotEqual(0,hwnd);
        var info=Info.Create(64,32,hwnd);
        Assert.Equal(0,Create(ref info,out var presenter));
        try
        {
            var observations=new List<object>();
            foreach(var alpha in new[]{.5f,0f})
            {
                Assert.Equal(0,Clear(presenter,.8f,.4f,.2f,alpha));
                Assert.Equal(0,Present(presenter));
                var capture=WgcWindowCapture.Capture(hwnd,TimeSpan.FromSeconds(3))
                    ?? throw new InvalidOperationException("Owned native composition HWND produced no WGC frame.");
                var expected=new[]{(int)Math.Round(51*alpha),(int)Math.Round(102*alpha),(int)Math.Round(204*alpha),(int)Math.Round(255*alpha)};
                observations.Add(new{alpha,capture.Width,capture.Height,capture.SourceQpcTicks,center=capture.Center});
                for(var c=0;c<4;c++)Assert.InRange((int)capture.Center[c],Math.Max(0,expected[c]-1),Math.Min(255,expected[c]+1));
            }
            var evidence=System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Api.LibraryPath)!,"direct-presentation-tests","native-composition-alpha.json");
            System.IO.File.WriteAllText(evidence,System.Text.Json.JsonSerializer.Serialize(observations));
            Assert.Equal(unchecked((int)0x80070005),ReadPixel(presenter,32,16,out _));
        }
        finally{Destroy(presenter);host.Close();}
    });
    [Theory]
    [InlineData(false, .5f)]
    [InlineData(false, 1f)]
    [InlineData(true, .5f)]
    public void DirectMaterialMatchesTheProductionCpuPrefilter(bool frosted,float clarity) => NativeEngineTests.OnSta(() =>
    {
        const int w=16,h=12;
        var hwnd=CreateWindowEx(0x00200080,"STATIC","Direct production filter",unchecked((uint)0x80000000),0,0,w,h,0,0,0,0);
        Assert.NotEqual(0,hwnd);
        var info=Info.Create(w,h,hwnd); info.Flags=1;
        Assert.Equal(0,Create(ref info,out var p));
        try
        {
            var library=NativeLibrary.Load(Api.LibraryPath);
            try { Assert.True(NativeLibrary.TryGetExport(library,"GD_PresenterUploadBitmap",out _),"Production material is not connected to direct presentation."); }
            finally { NativeLibrary.Free(library); }
            var pixels=new byte[w*h*4];
            for(var y=0;y<h;y++) for(var x=0;x<w;x++)
            { var i=(y*w+x)*4; pixels[i]=(byte)(x*13);pixels[i+1]=(byte)(y*19);pixels[i+2]=(byte)((x+y)%2*255);pixels[i+3]=255; }
            var material=new StaticMaterialTests.Material {Size=48,Version=1,DipWidth=32,DipHeight=24,Strength=1,Clarity=clarity,TintOpacity=.16f,Dark=1,Kind=frosted?1u:0u};
            Assert.Equal(unchecked((int)0x80070015),RenderMaterial(p,ref material));
            var pin=GCHandle.Alloc(pixels,GCHandleType.Pinned);
            try
            {
                var upload=new StaticMaterialTests.Upload {Size=32,Version=1,Width=w,Height=h,Stride=w*4,Pixels=pin.AddrOfPinnedObject()};
                Assert.Equal(0,UploadBitmap(p,ref upload));
            }
            finally { pin.Free(); }
            Assert.Equal(0,RenderMaterial(p,ref material));
            var expected=new byte[pixels.Length]; var scratch=new byte[pixels.Length];
            if(frosted) GlassDesk.UI.Controls.AndroidLiquidGlassRenderer.RenderFrostedBackdropInto(pixels,expected,scratch,w,h,new(32,24),true,1,clarity);
            else GlassDesk.UI.Controls.AndroidLiquidGlassRenderer.RenderTransitionBackdropInto(pixels,expected,scratch,w,h,new(32,24),true,1,backgroundClarity:clarity);
            for(uint y=0;y<h;y++) for(uint x=0;x<w;x++)
            {
                Assert.Equal(0,ReadPixel(p,x,y,out var color));
                var actual=BitConverter.GetBytes(color);
                for(var c=0;c<4;c++) Assert.InRange((int)actual[c],Math.Max(0,expected[(y*w+x)*4+c]-2),Math.Min(255,expected[(y*w+x)*4+c]+2));
            }
            Assert.Equal(0,Resize(p,32,24));
            Assert.Equal(unchecked((int)0x80070015),RenderMaterial(p,ref material));
        }
        finally { Destroy(p);DestroyWindow(hwnd); }
    });
    [Fact]
    public void PresenterRejectsBadAbiAndInvalidWindowBeforeAllocating()
    {
        var info=Info.Create(64,32,0); info.Version=99;
        Assert.Equal(unchecked((int)0x80004002),Create(ref info,out var presenter)); Assert.Equal(0,presenter);
        info.Version=1;
        Assert.Equal(unchecked((int)0x80070057),Create(ref info,out presenter)); Assert.Equal(0,presenter);
    }

    [Fact]
    public void DirectOutputIsPremultipliedAndReadbackRequiresDiagnosticPermission() => NativeEngineTests.OnSta(() =>
    {
        var window=CreateWindowEx(0x00200080,"STATIC","GlassDesk alpha test",unchecked((uint)0x80000000),0,0,64,32,0,0,0,0);
        Assert.NotEqual(0,window);
        try
        {
            var library=NativeLibrary.Load(Api.LibraryPath);
            try { Assert.True(NativeLibrary.TryGetExport(library,"GD_PresenterReadPixel",out _),"Direct alpha verification missing."); }
            finally { NativeLibrary.Free(library); }
            foreach (var diagnostic in new[]{false,true})
            {
                var info=Info.Create(64,32,window); info.Flags=diagnostic?1u:0u;
                Assert.Equal(0,Create(ref info,out var presenter));
                try
                {
                    Assert.Equal(0,Clear(presenter,.8f,.4f,.2f,.5f));
                    var result=ReadPixel(presenter,32,16,out var color);
                    if (!diagnostic)
                    {
                        Assert.Equal(unchecked((int)0x80070005),result);
                        Assert.Equal(unchecked((int)0x80070005),DirectGlassTests.ReadPixels(presenter,new byte[64*32*4],64*32*4,64*4));
                        continue;
                    }
                    Assert.Equal(0,result);
                    var actual=BitConverter.GetBytes(color); var expected=new[]{26,51,102,128};
                    for (var c=0;c<4;c++) Assert.InRange((int)actual[c],expected[c]-1,expected[c]+1);
                    Assert.Equal(0,Clear(presenter,1,1,1,0));
                    Assert.Equal(0,ReadPixel(presenter,32,16,out color)); Assert.Equal(0u,color);
                    Assert.Equal(unchecked((int)0x80070057),DirectGlassTests.ReadPixels(presenter,new byte[4],4,64*4));
                }
                finally { Destroy(presenter); }
            }
        }
        finally { DestroyWindow(window); }
    });
    [Fact]
    public void DirectPresenterUsesTwoBuffersWithoutChangingWindowOwnerOrStyle() => NativeEngineTests.OnSta(() =>
    {
        var window = CreateWindowEx(0x00200080,"STATIC","GlassDesk owned presenter test",unchecked((uint)0x80000000),0,0,64,32,0,0,0,0);
        Assert.NotEqual(0,window);
        var library = NativeLibrary.Load(Api.LibraryPath);
        try
        {
            Assert.True(NativeLibrary.TryGetExport(library,"GD_PresenterCreate",out _),"Independent native presentation is missing.");
            var style = GetWindowLongPtr(window,-16); var extended = GetWindowLongPtr(window,-20); var owner=GetWindow(window,4);
            var info=Info.Create(64,32,window);
            Assert.Equal(0,Create(ref info,out var presenter));
            var available=WaitHandle(presenter);
            try
            {
                Assert.NotEqual(0,WaitHandle(presenter));
                Assert.Equal(0,Clear(presenter,.2f,.4f,.8f,.5f));
                Assert.InRange(Present(presenter),0,1);
                var stats=PresenterStats.Create(); Assert.Equal(0,Statistics(presenter,ref stats));
                Assert.Equal(2u,stats.BufferCount); Assert.Equal(0u,stats.D3D9ResourceCount);
                Assert.Equal(64ul*32*4*2,stats.BufferBytes);
                Assert.Equal(unchecked((int)0x80070057),Resize(presenter,0,32));
                Assert.Equal(0,Resize(presenter,96,48));
                Assert.Equal(0,Statistics(presenter,ref stats)); Assert.Equal(96u,stats.Width); Assert.Equal(48u,stats.Height);
                Assert.Equal(style,GetWindowLongPtr(window,-16)); Assert.Equal(extended,GetWindowLongPtr(window,-20)); Assert.Equal(owner,GetWindow(window,4));
                Assert.Equal(unchecked((int)0x8001010E),Task.Run(()=>Clear(presenter,0,0,0,0)).GetAwaiter().GetResult());
            }
            finally { Destroy(presenter); }
            Assert.False(GetHandleInformation(available,out _));
        }
        finally { NativeLibrary.Free(library); DestroyWindow(window); }
    });

    [StructLayout(LayoutKind.Sequential)] internal struct PresenterStats
    {
        internal uint Size,Version;
        internal ulong Submitted,WouldBlock;
        internal uint Width,Height,BufferCount,D3D9ResourceCount;
        internal ulong BufferBytes;
        internal static PresenterStats Create()=>new(){Size=48,Version=1};
    }
    private const string Dll="GlassDesk.Rendering.Native.dll";
    [DllImport(Dll,EntryPoint="GD_PresenterCreate",CallingConvention=CallingConvention.Cdecl)] internal static extern int Create(ref Info info,out nint presenter);
    [DllImport(Dll,EntryPoint="GD_PresenterDestroy",CallingConvention=CallingConvention.Cdecl)] internal static extern void Destroy(nint presenter);
    [DllImport(Dll,EntryPoint="GD_PresenterClear",CallingConvention=CallingConvention.Cdecl)] internal static extern int Clear(nint presenter,float r,float g,float b,float a);
    [DllImport(Dll,EntryPoint="GD_PresenterPresent",CallingConvention=CallingConvention.Cdecl)] internal static extern int Present(nint presenter);
    [DllImport(Dll,EntryPoint="GD_PresenterResize",CallingConvention=CallingConvention.Cdecl)] internal static extern int Resize(nint presenter,uint width,uint height);
    [DllImport(Dll,EntryPoint="GD_PresenterGetStatistics",CallingConvention=CallingConvention.Cdecl)] internal static extern int Statistics(nint presenter,ref PresenterStats stats);
    [DllImport(Dll,EntryPoint="GD_PresenterGetWaitHandle",CallingConvention=CallingConvention.Cdecl)] internal static extern nint WaitHandle(nint presenter);
    [DllImport(Dll,EntryPoint="GD_PresenterReadPixel",CallingConvention=CallingConvention.Cdecl)] internal static extern int ReadPixel(nint presenter,uint x,uint y,out uint bgra);
    [DllImport(Dll,EntryPoint="GD_PresenterUploadBitmap",CallingConvention=CallingConvention.Cdecl)] internal static extern int UploadBitmap(nint presenter,ref StaticMaterialTests.Upload upload);
    [DllImport(Dll,EntryPoint="GD_PresenterRenderMaterial",CallingConvention=CallingConvention.Cdecl)] internal static extern int RenderMaterial(nint presenter,ref StaticMaterialTests.Material material);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern nint CreateWindowEx(uint ex,string cls,string name,uint style,int x,int y,int w,int h,nint parent,nint menu,nint instance,nint arg);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd,uint command);
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd,int index);
    [DllImport("kernel32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetHandleInformation(nint handle,out uint flags);
}
