using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GlassDesk.NativeRendering.Tests;

public sealed class DirectImageTests
{
    [Fact]
    public void ImageResourcesAreBoundedReusableAndExplicitlyReleased() => NativeEngineTests.OnSta(() =>
    {
        var hwnd=CreateWindowEx(0x00200080,"STATIC","Native image resources",unchecked((uint)0x80000000),0,0,80,80,0,0,0,0);
        Assert.NotEqual(0,hwnd);var info=Info.Create(80,80,hwnd);info.Flags=1;
        Assert.Equal(0,DirectPresenterTests.Create(ref info,out var p));
        try
        {
            var source=new byte[256*256*4];Array.Fill(source,(byte)255);
            Upload(p,1,256,256,source);
            var stats=Stats(p);Assert.Equal(1u,stats.ImageCount);Assert.Equal((ulong)source.Length,stats.SourceBytes);Assert.Equal(0ul,stats.CacheBytes);
            Assert.Equal(0,Submit(p,new ImageDraw{Key=1,X=4,Y=5,Width=43.6f,Height=43.6f,Opacity=0}));
            Assert.Equal(0,Submit(p,new ImageDraw{Key=1,X=800,Y=800,Width=20,Height=20,Opacity=1}));
            Assert.Equal(0ul,Stats(p).PrefilterRuns);
            var draws=new[]{new ImageDraw{Key=1,X=4,Y=5,Width=43.6f,Height=43.6f,Opacity=1},new ImageDraw{Key=1,X=40,Y=39,Width=20,Height=20,Opacity=.4f}};
            for(var i=0;i<8;i++){draws[0].X=4+i*.1f;Assert.Equal(0,Submit(p,draws));}
            stats=Stats(p);Assert.Equal(2ul,stats.PrefilterRuns);Assert.True(stats.CacheBytes>0);
            var before=new byte[80*80*4];Assert.Equal(0,DirectGlassTests.ReadPixels(p,before,(ulong)before.Length,80*4));
            Assert.Equal(unchecked((int)0x80070490),Submit(p,draws[0],new(){Key=99,X=0,Y=0,Width=40,Height=40,Opacity=1}));
            var after=new byte[before.Length];Assert.Equal(0,DirectGlassTests.ReadPixels(p,after,(ulong)after.Length,80*4));Assert.Equal(before,after);
            // A valid 32 MiB bitmap plus the existing asset exceeds the image
            // budget. Rejection must preserve the existing source and caches.
            var large=new byte[4096*2048*4];var pin=GCHandle.Alloc(large,GCHandleType.Pinned);
            try{var b=new StaticMaterialTests.Upload{Size=32,Version=1,Width=4096,Height=2048,Stride=4096*4,Pixels=pin.AddrOfPinnedObject()};Assert.Equal(unchecked((int)0x8007000E),SetImage(p,2,ref b));}
            finally{pin.Free();}
            Assert.Equal(stats,Stats(p));
            Assert.Equal(0,DirectPresenterTests.Resize(p,160,160));Assert.Equal(stats,Stats(p));
            Assert.Equal(0,Submit(p,draws));var resized=Stats(p);Assert.Equal(stats.SourceBytes,resized.SourceBytes);Assert.True(resized.PrefilterRuns>stats.PrefilterRuns);
            Assert.InRange(resized.CacheBytes,1ul,4*resized.SourceBytes);
            Upload(p,1,256,256,source);Assert.Equal(0ul,Stats(p).CacheBytes);Assert.Equal(1u,Stats(p).ImageCount);
            Assert.Equal(0,RemoveImage(p,1));Assert.Equal(1,RemoveImage(p,1));stats=Stats(p);
            Assert.Equal(0u,stats.ImageCount);Assert.Equal(0ul,stats.SourceBytes);Assert.Equal(0ul,stats.CacheBytes);
        }
        finally{DirectPresenterTests.Destroy(p);DestroyWindow(hwnd);}
    });

    [Theory]
    [InlineData(256,43.6f,1f)]
    [InlineData(256,50.14f,.4f)]
    [InlineData(24,48f,1f)]
    [InlineData(256,43.6f,1f,true)]
    public void NativeImagesPreserveProductionHighQualityScalingAndAlpha(int sourceSize,float size,float opacity,bool duplicate=false) => NativeEngineTests.OnSta(() =>
    {
        var library=NativeLibrary.Load(Api.LibraryPath);
        try{Assert.True(NativeLibrary.TryGetExport(library,"GD_PresenterSetImage",out _),"Native foreground images are missing.");}
        finally{NativeLibrary.Free(library);}
        const int width=80,height=80;
        var source=new byte[sourceSize*sourceSize*4];
        for(var y=0;y<sourceSize;y++)for(var x=0;x<sourceSize;x++)
        {
            var i=(y*sourceSize+x)*4;
            var dx=(x+.5)/sourceSize-.5;var dy=(y+.5)/sourceSize-.5;
            var alpha=dx*dx+dy*dy<.435*.435?(byte)255:(byte)0;
            source[i]=(byte)((x/2+y/2)%2*alpha);source[i+1]=(byte)(y*19%256*alpha/255);
            source[i+2]=(byte)(x*13%256*alpha/255);source[i+3]=alpha;
        }
        var bitmap=BitmapSource.Create(sourceSize,sourceSize,96,96,PixelFormats.Pbgra32,null,source,sourceSize*4);bitmap.Freeze();
        var image=new Image{Source=bitmap,Width=size,Height=size,Opacity=opacity,Stretch=Stretch.Uniform};
        RenderOptions.SetBitmapScalingMode(image,BitmapScalingMode.HighQuality);
        var canvas=new Canvas{Width=width,Height=height};canvas.Children.Add(image);Canvas.SetLeft(image,4.35);Canvas.SetTop(image,5.2);
        if(duplicate)
        {
            var other=new Image{Source=bitmap,Width=20,Height=20,Opacity=.4,Stretch=Stretch.Uniform};
            RenderOptions.SetBitmapScalingMode(other,BitmapScalingMode.HighQuality);canvas.Children.Add(other);Canvas.SetLeft(other,40.35);Canvas.SetTop(other,39.2);
        }
        var host=new Window{Width=width,Height=height,Content=canvas,WindowStyle=WindowStyle.None,AllowsTransparency=true,
            Background=Brushes.Transparent,ShowActivated=false,ShowInTaskbar=false,Left=0,Top=0};
        try
        {
            host.Show();host.UpdateLayout();var hwnd=new System.Windows.Interop.WindowInteropHelper(host).Handle;
            var reference=WgcWindowCapture.Capture(hwnd,TimeSpan.FromSeconds(3))??throw new InvalidOperationException("Owned hardware image reference did not render.");
            var info=Info.Create((uint)reference.Width,(uint)reference.Height,hwnd);info.Flags=1;
            Assert.Equal(0,DirectPresenterTests.Create(ref info,out var p));
            try
            {
                var pin=GCHandle.Alloc(source,GCHandleType.Pinned);
                try{var upload=new StaticMaterialTests.Upload{Size=32,Version=1,Width=(uint)sourceSize,Height=(uint)sourceSize,Stride=(uint)sourceSize*4,Pixels=pin.AddrOfPinnedObject()};Assert.Equal(0,SetImage(p,1,ref upload));}
                finally{pin.Free();}
                Assert.Equal(0,DirectPresenterTests.Clear(p,0,0,0,0));
                var items=new List<ImageDraw>{new(){Key=1,X=4.35f,Y=5.2f,Width=size,Height=size,Opacity=opacity}};
                if(duplicate)items.Add(new(){Key=1,X=40.35f,Y=39.2f,Width=20,Height=20,Opacity=.4f});
                pin=GCHandle.Alloc(items.ToArray(),GCHandleType.Pinned);
                try{var batch=new ImageBatch{Size=32,Version=1,Count=(uint)items.Count,HostWidth=width,HostHeight=height,Items=pin.AddrOfPinnedObject()};Assert.Equal(0,DrawImages(p,ref batch));}
                finally{pin.Free();}
                var actual=new byte[reference.Pixels.Length];Assert.Equal(0,DirectGlassTests.ReadPixels(p,actual,(ulong)actual.Length,(uint)reference.Width*4));
                double total=0;var maximum=0;var alphaMaximum=0;
                for(var i=0;i<actual.Length;i++){var d=Math.Abs(actual[i]-reference.Pixels[i]);total+=d;maximum=Math.Max(maximum,d);if(i%4==3)alphaMaximum=Math.Max(alphaMaximum,d);}
                var evidence=System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Api.LibraryPath)!,"direct-presentation-tests",$"image-{sourceSize}-{size}-{opacity}"+(duplicate?"-duplicate":""));System.IO.Directory.CreateDirectory(evidence);
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(evidence,"wpf.bgra"),reference.Pixels);System.IO.File.WriteAllBytes(System.IO.Path.Combine(evidence,"native.bgra"),actual);
                System.IO.File.WriteAllText(System.IO.Path.Combine(evidence,"comparison.json"),System.Text.Json.JsonSerializer.Serialize(new{reference.Width,reference.Height,mean=total/actual.Length,maximum,alphaMaximum}));
                Assert.True(total/actual.Length<1.5,$"Mean channel difference {total/actual.Length:F3}; max {maximum}.");Assert.InRange(maximum,0,8);
            }
            finally{DirectPresenterTests.Destroy(p);}
        }
        finally{host.Close();}
    });

    [StructLayout(LayoutKind.Sequential)] internal struct ImageDraw
    { internal uint Key,Reserved;internal float X,Y,Width,Height,Opacity;internal uint Reserved2; }
    [StructLayout(LayoutKind.Sequential)] internal struct ImageBatch
    { internal uint Size,Version,Count,Reserved;internal float HostWidth,HostHeight;internal nint Items; }
    [StructLayout(LayoutKind.Sequential)] internal struct ImageStats
    { internal uint Size,Version,ImageCount,Reserved;internal ulong SourceBytes,CacheBytes,PrefilterRuns; }
    internal static ImageStats Stats(nint p){var s=new ImageStats{Size=40,Version=1};Assert.Equal(0,GetStatistics(p,ref s));return s;}
    internal static void Upload(nint p,uint key,uint width,uint height,byte[] pixels)
    {
        var pin=GCHandle.Alloc(pixels,GCHandleType.Pinned);
        try{var b=new StaticMaterialTests.Upload{Size=32,Version=1,Width=width,Height=height,Stride=width*4,Pixels=pin.AddrOfPinnedObject()};Assert.Equal(0,SetImage(p,key,ref b));}
        finally{pin.Free();}
    }
    internal static int Submit(nint p,params ImageDraw[] items)
    {
        var pin=GCHandle.Alloc(items,GCHandleType.Pinned);
        try{var b=new ImageBatch{Size=32,Version=1,Count=(uint)items.Length,HostWidth=80,HostHeight=80,Items=pin.AddrOfPinnedObject()};return DrawImages(p,ref b);}
        finally{pin.Free();}
    }
    [DllImport("GlassDesk.Rendering.Native.dll",EntryPoint="GD_PresenterSetImage",CallingConvention=CallingConvention.Cdecl)]
    internal static extern int SetImage(nint p,uint key,ref StaticMaterialTests.Upload bitmap);
    [DllImport("GlassDesk.Rendering.Native.dll",EntryPoint="GD_PresenterDrawImages",CallingConvention=CallingConvention.Cdecl)]
    internal static extern int DrawImages(nint p,ref ImageBatch batch);
    [DllImport("GlassDesk.Rendering.Native.dll",EntryPoint="GD_PresenterRemoveImage",CallingConvention=CallingConvention.Cdecl)]
    internal static extern int RemoveImage(nint p,uint key);
    [DllImport("GlassDesk.Rendering.Native.dll",EntryPoint="GD_PresenterGetImageStatistics",CallingConvention=CallingConvention.Cdecl)]
    internal static extern int GetStatistics(nint p,ref ImageStats stats);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern nint CreateWindowEx(uint ex,string cls,string name,uint style,int x,int y,int w,int h,nint parent,nint menu,nint instance,nint arg);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(nint hwnd);
}
