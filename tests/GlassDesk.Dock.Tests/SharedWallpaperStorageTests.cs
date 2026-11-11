using System.IO;
using System.IO.MemoryMappedFiles;
using System.Reflection;
using System.Runtime.InteropServices;
using GlassDesk.UI.CaptureCompatibility;

namespace GlassDesk.Dock.Tests;

public sealed class SharedWallpaperStorageTests
{
    [Fact]
    public void RealPrintWindowSourceStaysIdenticalInTheMappedDib()=>StaTest.Run(()=>
    {
        const int sw=640,sh=480,tw=193,th=57;
        var window=CreateWindowExW(0,"STATIC","GlassDesk shared wallpaper fixture",0x8000000E,-10000,-10000,sw,sh,0,0,0,0);
        Assert.NotEqual(0,window);
        try
        {
            using var wallpaper=new NativeWallpaperSurface(window,sw,sh);var data=new byte[sw*sh*4];
            for(var y=0;y<sh;y++)for(var x=0;x<sw;x++){var p=(y*sw+x)*4;data[p]=(byte)(20+x%190);data[p+1]=(byte)(10+y%180);data[p+2]=(byte)(30+(x+y)%170);data[p+3]=255;}
            wallpaper.Update(data);ShowWindow(window,4);
            bool Read(nint hwnd,nint dc)=>PrintWindow(hwnd,dc,2);
            using var reference=new LiveWallpaperSurface(sw,sh,tw,th,Read,null);
            var ctor=Constructor();
            using var shared=(LiveWallpaperSurface)ctor.Invoke([sw,sh,tw,th,(Func<nint,nint,bool>)Read,null,Guid.NewGuid().ToString("N")]);
            var expected=new byte[tw*th*4];var actual=new byte[expected.Length];
            Assert.True(reference.TryDraw(window,137,221,247,119,0,0,sw,sh,expected));
            Assert.True(shared.TryDraw(window,137,221,247,119,0,0,sw,sh,actual));
            Assert.Equal(expected,actual);Assert.True(expected.Where((_,i)=>i%4!=3).Distinct().Count()>100);
        }
        finally{DestroyWindow(window);}
    });

    [Fact]
    public void AnAbandonedWriterPoisonsTheBackingUntilEveryOldOwnerHasRetired()
    {
        var reads=0;bool Draw(nint _,nint dc){reads++;Fill(dc,0x000000ff);return true;}
        var scope=Guid.NewGuid().ToString("N");
        using var surface=Create(Draw,scope);var pixels=new byte[64*64*4];
        Assert.True(surface.TryDraw(77,0,0,64,64,0,0,64,64,pixels));
        var owner=typeof(LiveWallpaperSurface).GetField("_sharedSource",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(surface)!;
        var name=(string)owner.GetType().GetProperty("Name",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(owner)!;
        var abandoned=new Thread(()=>{using var mutex=Mutex.OpenExisting(name+"-gate");Assert.True(mutex.WaitOne(1000));});
        abandoned.Start();Assert.True(abandoned.Join(2000));
        Array.Clear(pixels);Assert.ThrowsAny<InvalidOperationException>(()=>surface.TryDraw(77,0,0,64,64,0,0,64,64,pixels));
        using var peer=Create(Draw,scope);
        Assert.ThrowsAny<InvalidOperationException>(()=>peer.TryDraw(77,0,0,64,64,0,0,64,64,pixels));
        Assert.Equal(1,reads);
    }

    [Fact]
    public async Task ConcurrentReadersShareOneBackingStoreWithoutMixingTheirFreshFrames()
    {
        var scope=Guid.NewGuid().ToString("N");using var painted=new ManualResetEventSlim();
        bool Red(nint _,nint dc){Fill(dc,0x000000ff);painted.Set();Thread.Sleep(50);return true;}
        bool Blue(nint _,nint dc){Fill(dc,0x00ff0000);return true;}
        using var a=Create(Red,scope);using var b=Create(Blue,scope);
        var red=new byte[64*64*4];var blue=new byte[red.Length];
        var first=Task.Run(()=>a.TryDraw(77,0,0,64,64,0,0,64,64,red));
        Assert.True(painted.Wait(TimeSpan.FromSeconds(3)));
        var second=Task.Run(()=>b.TryDraw(77,0,0,64,64,0,0,64,64,blue));
        Assert.All(await Task.WhenAll(first,second),ok=>Assert.True(ok));
        Assert.Equal(new byte[]{0,0,255,255},red[..4]);Assert.Equal(new byte[]{255,0,0,255},blue[..4]);
        // Both native views must observe the second write, proving one backing store.
        Assert.Equal(new byte[]{255,0,0},SourceRgb(a));Assert.Equal(SourceRgb(a),SourceRgb(b));
    }

    [Fact]
    public void ScopeIsolationPeerDisposalAndLastOwnerCleanupAreRealKernelLifetimes()
    {
        var scope=Guid.NewGuid().ToString("N");
        bool Red(nint _,nint dc){Fill(dc,0x000000ff);return true;}
        bool Blue(nint _,nint dc){Fill(dc,0x00ff0000);return true;}
        using var a=Create(Red,scope);using var b=Create(Blue,scope);using var other=Create(Red,Guid.NewGuid().ToString("N"));
        var pixels=new byte[64*64*4];
        Assert.True(a.TryDraw(77,0,0,64,64,0,0,64,64,pixels));
        Assert.True(b.TryDraw(77,0,0,64,64,0,0,64,64,pixels));
        Assert.True(other.TryDraw(77,0,0,64,64,0,0,64,64,pixels));
        Assert.Equal(new byte[]{255,0,0},SourceRgb(a));Assert.Equal(new byte[]{0,0,255},SourceRgb(other));
        var owner=typeof(LiveWallpaperSurface).GetField("_sharedSource",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(b)!;
        var name=(string)owner.GetType().GetProperty("Name",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(owner)!;
        a.Dispose();Assert.True(b.TryDraw(77,0,0,64,64,0,0,64,64,pixels));
        using(var view=MemoryMappedFile.OpenExisting(name)){Assert.NotNull(view);}
        b.Dispose();Assert.Throws<FileNotFoundException>(()=>MemoryMappedFile.OpenExisting(name));
    }

    private static LiveWallpaperSurface Create(Func<nint,nint,bool> draw,string scope)
    {
        return (LiveWallpaperSurface)Constructor().Invoke([64,64,64,64,draw,null,scope]);
    }
    private static ConstructorInfo Constructor()
    {var ctor=typeof(LiveWallpaperSurface).GetConstructor(BindingFlags.Instance|BindingFlags.NonPublic,null,
        [typeof(int),typeof(int),typeof(int),typeof(int),typeof(Func<nint,nint,bool>),typeof(Action<CaptureTrace>),typeof(string)],null);Assert.NotNull(ctor);return ctor;}
    private static byte[] SourceRgb(LiveWallpaperSurface surface)
    {
        var pointer=(nint)typeof(LiveWallpaperSurface).GetField("_sourceBits",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(surface)!;
        var data=new byte[3];Marshal.Copy(pointer,data,0,3);return data;
    }
    private static void Fill(nint dc,uint color)
    {var brush=CreateSolidBrush(color);var rect=new Rect{Right=64,Bottom=64};try{Assert.NotEqual(0,FillRect(dc,ref rect,brush));}finally{DeleteObject(brush);}}
    [StructLayout(LayoutKind.Sequential)]private struct Rect{public int Left,Top,Right,Bottom;}
    [DllImport("gdi32.dll")]private static extern nint CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")]private static extern bool DeleteObject(nint value);
    [DllImport("user32.dll")]private static extern int FillRect(nint dc,ref Rect rect,nint brush);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern nint CreateWindowExW(uint ex,string cls,string title,uint style,int x,int y,int w,int h,nint parent,nint menu,nint instance,nint parameter);
    [DllImport("user32.dll")]private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")]private static extern bool ShowWindow(nint window,int command);
    [DllImport("user32.dll")]private static extern bool PrintWindow(nint window,nint dc,uint flags);
}
