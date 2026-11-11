using System.Runtime.InteropServices;
using GlassDesk.UI.CaptureCompatibility;

namespace GlassDesk.Dock.Tests;

public sealed class PrimaryWallpaperReadbackTests
{
    [Theory]
    [InlineData(0,-352,4160,2560,0,0,2560,1440,100,100,100,100,2560,1792)]
    [InlineData(-1600,0,4160,2560,0,0,2560,1440,100,100,100,100,4160,1440)]
    [InlineData(-1600,-400,4160,2960,0,0,2560,1440,100,100,100,100,4160,1840)]
    [InlineData(0,0,4000,2560,0,0,1440,2560,100,100,100,100,1440,2560)]
    [InlineData(0,0,2560,1440,0,0,2560,1440,100,100,100,100,2560,1440)]
    [InlineData(0,-352,4160,2560,0,0,2560,1440,2700,100,100,100,4160,2560)]
    [InlineData(0,-352,4160,2560,0,0,2560,1440,2500,100,100,100,4160,2560)]
    [InlineData(100,0,2460,1440,0,0,2560,1440,200,100,100,100,2460,1440)]
    public void CapacityIncludesEveryRequiredPixelWithoutTranslatingTheWindowOrigin(
        int sx,int sy,int sw,int sh,int px,int py,int pw,int ph,int x,int y,int w,int h,int expectedWidth,int expectedHeight)
    {
        var result=WallpaperReadbackExtent.LimitToPrimary(new(sx,sy,sw,sh),new(px,py,pw,ph),new(x,y,w,h));
        Assert.Equal(new ScreenRect(sx,sy,expectedWidth,expectedHeight),result);
    }

    [Fact]
    public void SmallerSharedDibKeepsTheExactPrimaryPixelsAndLiveUpdates()=>StaTest.Run(()=>
    {
        const int sw=640,sh=480,tw=193,th=57;
        var window=CreateWindowExW(0,"STATIC","Primary wallpaper capture fixture",0x8000000E,-10000,-10000,sw,sh,0,0,0,0);
        Assert.NotEqual(0,window);
        try
        {
            using var wallpaper=new NativeWallpaperSurface(window,sw,sh);
            var image=new byte[sw*sh*4];
            for(var y=0;y<sh;y++)for(var x=0;x<sw;x++)
            {var p=(y*sw+x)*4;image[p]=(byte)(20+x%190);image[p+1]=(byte)(10+y%180);image[p+2]=(byte)(30+(x+y)%170);image[p+3]=255;}
            wallpaper.Update(image);ShowWindow(window,4);
            var bounds=new ScreenRect(-80,-40,sw,sh);var primary=new ScreenRect(0,0,320,180);
            var region=new ScreenRect(77,41,147,89);var capacity=WallpaperReadbackExtent.LimitToPrimary(bounds,primary,region);
            var extents=new List<(int,int)>();
            bool Read(nint hwnd,nint dc)
            {
                GetObjectW(GetCurrentObject(dc,7),Marshal.SizeOf<Bitmap>(),out var b);extents.Add((b.Width,b.Height));
                return PrintWindow(hwnd,dc,2);
            }
            using var reference=new LiveWallpaperSurface(sw,sh,tw,th,Read,null);
            using var candidate=new LiveWallpaperSurface(capacity.Width,capacity.Height,tw,th,Read,null,Guid.NewGuid().ToString("N"),true);
            var expected=new byte[tw*th*4];var actual=new byte[expected.Length];
            bool Draw(LiveWallpaperSurface capture,byte[] output)=>capture.TryDraw(window,region.X,region.Y,region.Width,region.Height,
                bounds.X,bounds.Y,bounds.X+bounds.Width,bounds.Y+bounds.Height,output);
            Assert.True(Draw(reference,expected));extents.Clear();Assert.True(Draw(candidate,actual));
            Assert.Equal(expected,actual);Assert.All(extents,e=>Assert.Equal((400,220),e));
            Assert.Equal(image.AsSpan((81*sw+157)*4,4).ToArray(),actual[..4]);
            var previous=actual.ToArray();
            for(var p=0;p<image.Length;p+=4){image[p]=200;image[p+1]=90;image[p+2]=40;}
            wallpaper.Update(image);Assert.True(Draw(reference,expected));Assert.True(Draw(candidate,actual));
            Assert.Equal(expected,actual);Assert.NotEqual(previous,actual);
            Assert.Equal(new byte[]{200,90,40,255},actual[..4]);
        }
        finally{DestroyWindow(window);}
    });

    [StructLayout(LayoutKind.Sequential)] private struct Bitmap
    {public int Type,Width,Height,WidthBytes;public ushort Planes,BitsPixel;public nint Bits;}
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern nint CreateWindowExW(uint ex,string cls,string title,uint style,int x,int y,int w,int h,nint parent,nint menu,nint instance,nint data);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd,int command);
    [DllImport("user32.dll")] private static extern bool PrintWindow(nint hwnd,nint dc,uint flags);
    [DllImport("gdi32.dll")] private static extern nint GetCurrentObject(nint dc,uint type);
    [DllImport("gdi32.dll",CharSet=CharSet.Unicode)] private static extern int GetObjectW(nint value,int size,out Bitmap bitmap);
}
