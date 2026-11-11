using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using GlassDesk.UI.CaptureCompatibility;

internal static class WallpaperClipReport
{
    internal static int Run(string path)
    {
        if(File.Exists(path))throw new IOException("Use a fresh clipping report.");
        const int sw=640,sh=480;
        var window=CreateWindowExW(0,"STATIC","GlassDesk clipping diagnostic",0x8000000E,-10000,-10000,sw,sh,0,0,0,0);
        if(window==0)throw new InvalidOperationException("Cannot create clipping source.");
        try
        {
            using var wallpaper=new NativeWallpaperSurface(window,sw,sh);var pixels=new byte[sw*sh*4];
            for(var y=0;y<sh;y++)for(var x=0;x<sw;x++){var p=(y*sw+x)*4;pixels[p]=(byte)(20+x%190);pixels[p+1]=(byte)(10+y%180);pixels[p+2]=(byte)(30+(x+y)%170);pixels[p+3]=255;}
            wallpaper.Update(pixels);ShowWindow(window,4);
            var clipResult=0;
            bool Read(nint source,nint dc)
            {
                var saved=SaveDC(dc);var brush=CreateSolidBrush(0x00494949);var rect=new Rect{Right=sw,Bottom=sh};
                try{FillRect(dc,ref rect,brush);clipResult=IntersectClipRect(dc,137,221,384,340);return PrintWindow(source,dc,2);}
                finally{DeleteObject(brush);RestoreDC(dc,saved);}
            }
            using var capture=new LiveWallpaperSurface(sw,sh,193,57,Read,null);var target=new byte[193*57*4];
            var readSucceeded=capture.TryDraw(window,137,221,247,119,0,0,sw,sh,target);
            var bits=(nint)typeof(LiveWallpaperSurface).GetField("_sourceBits",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(capture)!;
            var outside=new byte[4];Marshal.Copy(bits,outside,0,4);
            var inside=new byte[4];Marshal.Copy(bits+(221*sw+137)*4,inside,0,4);
            var preserved=outside.Take(3).SequenceEqual(new byte[]{73,73,73});
            File.WriteAllText(path,JsonSerializer.Serialize(new{readSucceeded,clipResult,outside,inside,sourceClipPreserved=preserved,
                note="Owned patterned STATIC source, actual PrintWindow(PW_RENDERFULLCONTENT), full 32bpp DIB seeded outside the requested clip. No installed windows changed."},new JsonSerializerOptions{WriteIndented=true}));
            return readSucceeded?0:1;
        }
        finally{DestroyWindow(window);}
    }
    [StructLayout(LayoutKind.Sequential)]private struct Rect{public int Left,Top,Right,Bottom;}
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern nint CreateWindowExW(uint ex,string cls,string title,uint style,int x,int y,int w,int h,nint parent,nint menu,nint instance,nint parameter);
    [DllImport("user32.dll")]private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")]private static extern bool ShowWindow(nint window,int command);
    [DllImport("user32.dll")]private static extern bool PrintWindow(nint window,nint dc,uint flags);
    [DllImport("user32.dll")]private static extern int FillRect(nint dc,ref Rect rect,nint brush);
    [DllImport("gdi32.dll")]private static extern nint CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")]private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")]private static extern int SaveDC(nint dc);
    [DllImport("gdi32.dll")]private static extern bool RestoreDC(nint dc,int state);
    [DllImport("gdi32.dll")]private static extern int IntersectClipRect(nint dc,int l,int t,int r,int b);
}
