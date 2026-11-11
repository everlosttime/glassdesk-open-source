using System.Runtime.InteropServices;

namespace GlassDesk.NativeRendering.Tests;

public sealed class DirectMappedImageTests
{
    [Fact]
    public void MappedImageAbiRejectsInvalidMapsAndMissingAssetsBeforeDrawing()=>NativeEngineTests.OnSta(()=>
    {
        Assert.Equal(48,Marshal.SizeOf<MappedDraw>());Assert.Equal(32,Marshal.SizeOf<Batch>());
        var hwnd=CreateWindowEx(0x00200080,"STATIC","Mapped image validation",unchecked((uint)0x80000000),0,0,80,80,0,0,0,0);
        Assert.NotEqual(0,hwnd);var info=Info.Create(80,80,hwnd);info.Flags=1;
        Assert.Equal(0,DirectPresenterTests.Create(ref info,out var presenter));
        try
        {
            var source=Enumerable.Repeat((byte)255,16*16*4).ToArray();DirectImageTests.Upload(presenter,1,16,16,source);
            var draw=new MappedDraw{Geometry=new(){Key=1,X=4,Y=5,Width=44,Height=44,Opacity=1},MapX=4.35f,MapY=5.2f,MapWidth=43.6f,MapHeight=43.6f};
            Assert.Equal(0,Submit(presenter,draw));var stats=DirectImageTests.Stats(presenter);
            Assert.Equal(1u,stats.ImageCount);Assert.Equal(1024ul,stats.SourceBytes);
            var before=new byte[80*80*4];Assert.Equal(0,DirectGlassTests.ReadPixels(presenter,before,(ulong)before.Length,320));
            var bad=draw;bad.MapWidth=0;Assert.Equal(unchecked((int)0x80070057),Submit(presenter,draw,bad));
            bad=draw;bad.MapY=float.NaN;Assert.Equal(unchecked((int)0x80070057),Submit(presenter,draw,bad));
            bad=draw;bad.Geometry.Reserved=1;Assert.Equal(unchecked((int)0x80070057),Submit(presenter,draw,bad));
            bad=draw;bad.Geometry.Key=99;Assert.Equal(unchecked((int)0x80070490),Submit(presenter,draw,bad));
            Assert.Equal(unchecked((int)0x80070057),Submit(presenter,Enumerable.Repeat(draw,257).ToArray()));
            var after=new byte[before.Length];Assert.Equal(0,DirectGlassTests.ReadPixels(presenter,after,(ulong)after.Length,320));Assert.Equal(before,after);
            var batch=new Batch{Size=32,Version=2,HostWidth=80,HostHeight=80};Assert.Equal(unchecked((int)0x80004002),Draw(presenter,ref batch));
            batch.Version=1;batch.Size=31;Assert.Equal(unchecked((int)0x80070057),Draw(presenter,ref batch));
            batch.Size=32;batch.Count=1;Assert.Equal(unchecked((int)0x80070057),Draw(presenter,ref batch));
            batch.Count=0;Assert.Equal(0,Draw(presenter,ref batch));Assert.Equal(stats,DirectImageTests.Stats(presenter));
        }
        finally{DirectPresenterTests.Destroy(presenter);DestroyWindow(hwnd);}
    });
    private static int Submit(nint presenter,params MappedDraw[] draws)
    {
        var pin=GCHandle.Alloc(draws,GCHandleType.Pinned);
        try{var batch=new Batch{Size=32,Version=1,Count=(uint)draws.Length,HostWidth=80,HostHeight=80,Items=pin.AddrOfPinnedObject()};return Draw(presenter,ref batch);}
        finally{pin.Free();}
    }
    [StructLayout(LayoutKind.Sequential)]private struct MappedDraw
    {internal DirectImageTests.ImageDraw Geometry;internal float MapX,MapY,MapWidth,MapHeight;}
    [StructLayout(LayoutKind.Sequential)]private struct Batch
    {internal uint Size,Version,Count,Reserved;internal float HostWidth,HostHeight;internal nint Items;}
    [DllImport("GlassDesk.Rendering.Native.dll",EntryPoint="GD_PresenterDrawMappedImages",CallingConvention=CallingConvention.Cdecl)]private static extern int Draw(nint presenter,ref Batch batch);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern nint CreateWindowEx(uint ex,string cls,string name,uint style,int x,int y,int w,int h,nint parent,nint menu,nint instance,nint arg);
    [DllImport("user32.dll")]private static extern bool DestroyWindow(nint hwnd);
}
