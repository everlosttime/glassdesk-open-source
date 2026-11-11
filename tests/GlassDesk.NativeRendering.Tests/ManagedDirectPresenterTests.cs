using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls;
using GlassDesk.Rendering;
using GlassDesk.UI.Controls;

namespace GlassDesk.NativeRendering.Tests;

public sealed class ManagedDirectPresenterTests
{
    [Fact]
    public void ReplacingLargeAssetsDoesNotCloneTheirPixelsOntoTheManagedHeap() => NativeEngineTests.OnSta(() =>
    {
        var host=Host(80,80);
        try
        {
            host.Show();host.UpdateLayout();
            using var output=new NativeDirectPresenter(new WindowInteropHelper(host).Handle,80,80);
            var small=BitmapSource.Create(1,1,96,96,PixelFormats.Pbgra32,null,new byte[]{0,0,0,255},4);
            output.SetImage(1,small); // Warm the upload/JIT path before measuring.
            const int side=1024;var bytes=new byte[side*side*4];Array.Fill(bytes,(byte)255);
            var bitmap=BitmapSource.Create(side,side,96,96,PixelFormats.Pbgra32,null,bytes,side*4);bitmap.Freeze();
            var start=GC.GetAllocatedBytesForCurrentThread();
            for(var i=0;i<3;i++)output.SetImage(1,bitmap);
            var allocated=GC.GetAllocatedBytesForCurrentThread()-start;
            var evidence=System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Api.LibraryPath)!,"direct-presentation-tests","managed-image-upload-allocation.json");
            System.IO.File.WriteAllText(evidence,System.Text.Json.JsonSerializer.Serialize(new{sourceBytes=bytes.Length,replacements=3,managedThreadAllocatedBytes=allocated}));
            Assert.True(allocated<1024*1024,$"Image upload allocated {allocated} managed bytes for three 4 MiB replacements.");
            Assert.Equal(1u,output.ImageStatistics.ImageCount);Assert.Equal((ulong)bytes.Length,output.ImageStatistics.SourceBytes);
        }
        finally{host.Close();}
    });
    [Fact]
    public void ManagedGlassAndProductionImageMatchTheComposedHardwareReference() => NativeEngineTests.OnSta(() =>
    {
        const int width=160,height=100,sw=216,sh=156;
        var source=new byte[sw*sh*4];
        for(var y=0;y<sh;y++)for(var x=0;x<sw;x++)
        {var i=(y*sw+x)*4;source[i]=(byte)(x*7%256);source[i+1]=(byte)(y*9%256);source[i+2]=(byte)((x/7+y/7)%2*255);source[i+3]=255;}
        var filtered=new byte[source.Length];var scratch=new byte[source.Length];
        AndroidLiquidGlassRenderer.RenderTransitionBackdropInto(source,filtered,scratch,sw,sh,new(sw,sh),true,1,backgroundClarity:.5);
        var bitmap=BitmapSource.Create(sw,sh,96,96,PixelFormats.Bgra32,null,filtered,sw*4);bitmap.Freeze();
        var asset=new BitmapImage();asset.BeginInit();asset.CacheOption=BitmapCacheOption.OnLoad;
        asset.UriSource=new Uri(System.IO.Path.Combine(Api.FindRoot(),"src","GlassDesk.App","Assets","GlassDeskBrand.png"));asset.EndInit();asset.Freeze();
        var panel=new Rect(8,8,144,84);var back=new Rect(-28,-28,sw,sh);
        var root=new Canvas{Width=width,Height=height,Clip=SquircleGlassGeometry.CreateCompact(panel,26)};
        var lens=new TransitionGlassLayer{Width=sw,Height=sh,Opacity=1};lens.SetBackdrop(bitmap);lens.SetPanel(panel,back,26,1,false);
        root.Children.Add(lens);Canvas.SetLeft(lens,-28);Canvas.SetTop(lens,-28);
        var image=new Image{Source=asset,Width=40,Height=40,Stretch=Stretch.Uniform};RenderOptions.SetBitmapScalingMode(image,BitmapScalingMode.HighQuality);
        root.Children.Add(image);Canvas.SetLeft(image,24.35);Canvas.SetTop(image,28.2);
        CapturePixels reference;
        var referenceHost=Host(width,height);referenceHost.Content=root;
        try{referenceHost.Show();referenceHost.UpdateLayout();reference=WgcWindowCapture.Capture(new WindowInteropHelper(referenceHost).Handle,TimeSpan.FromSeconds(3))??throw new InvalidOperationException("Hardware material reference was not captured.");}
        finally{referenceHost.Close();}
        var host=Host(width,height);
        try
        {
            host.Show();host.UpdateLayout();var hwnd=new WindowInteropHelper(host).Handle;
            using var output=new NativeDirectPresenter(hwnd,reference.Width,reference.Height);
            output.UploadWallpaper(source,sw,sh);output.RenderGlass(new(sw,sh,1,.5f,.16f,false,true),new(new(width,height),panel,back,26,1));
            output.SetImage(1,asset);
            // The layout supplies the content rectangle for Uniform fitting.
            var aspect=asset.Width/asset.Height;var imageWidth=40*Math.Min(1,aspect);var imageHeight=40/Math.Max(1,aspect);
            output.DrawImages(new(width,height),[new(1,new(24.35+(40-imageWidth)/2,28.2+(40-imageHeight)/2,imageWidth,imageHeight))]);
            Assert.True(output.Present());
            var actual=WgcWindowCapture.Capture(hwnd,TimeSpan.FromSeconds(3))??throw new InvalidOperationException("Native composed material was not captured.");
            Assert.Equal(reference.Width,actual.Width);Assert.Equal(reference.Height,actual.Height);
            double total=0;var maximum=0;var alphaMaximum=0;
            for(var i=0;i<actual.Pixels.Length;i++){var d=Math.Abs(actual.Pixels[i]-reference.Pixels[i]);total+=d;maximum=Math.Max(maximum,d);if(i%4==3)alphaMaximum=Math.Max(alphaMaximum,d);}
            var evidence=System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Api.LibraryPath)!,"direct-presentation-tests","managed-glass-production-image");System.IO.Directory.CreateDirectory(evidence);
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(evidence,"wpf.bgra"),reference.Pixels);System.IO.File.WriteAllBytes(System.IO.Path.Combine(evidence,"native-wgc.bgra"),actual.Pixels);
            System.IO.File.WriteAllText(System.IO.Path.Combine(evidence,"comparison.json"),System.Text.Json.JsonSerializer.Serialize(new{actual.Width,actual.Height,mean=total/actual.Pixels.Length,maximum,alphaMaximum,source="WGC of owned reference and native HWNDs"}));
            Assert.True(total/actual.Pixels.Length<1.5,$"Composed mean difference {total/actual.Pixels.Length:F3}; max {maximum}.");Assert.InRange(maximum,0,8);
        }
        finally{host.Close();}
    });
    private static Window Host(int width,int height)=>new(){Width=width,Height=height,Left=0,Top=0,WindowStyle=WindowStyle.None,
        AllowsTransparency=true,Background=Brushes.Transparent,ShowActivated=false,ShowInTaskbar=false};

    [Fact]
    public void ManagedDirectPresenterConvertsAssetsPresentsAlphaAndReleasesResources() => NativeEngineTests.OnSta(() =>
    {
        var host=new Window{Width=80,Height=80,Left=0,Top=0,WindowStyle=WindowStyle.None,AllowsTransparency=true,
            Background=Brushes.Transparent,ShowActivated=false,ShowInTaskbar=false};
        try
        {
            host.Show();host.UpdateLayout();var hwnd=new WindowInteropHelper(host).Handle;
            var initial=WgcWindowCapture.Capture(hwnd,TimeSpan.FromSeconds(3))??throw new InvalidOperationException("Owned reference HWND was not captured.");
            using var output=new NativeDirectPresenter(hwnd,initial.Width,initial.Height);
            var bitmap=BitmapSource.Create(2,2,96,96,PixelFormats.Bgr24,null,new byte[]{50,100,200,50,100,200,50,100,200,50,100,200},6);
            output.SetImage(1,bitmap);Assert.Equal(1u,output.ImageStatistics.ImageCount);Assert.Equal(16ul,output.ImageStatistics.SourceBytes);
            output.Clear(Colors.Transparent);
            output.DrawImages(new(80,80),[new(1,new Rect(8,8,40,40),.4f)]);
            var unsupportedScene=new DirectGlassScene(new(80,80),new(8,8,64,64),new(0,0,80,80),15,1);
            var material=new StaticGlassMaterial(80,80,1,.5f,.16f,false,true);
            Assert.False(output.TryRenderGlass(material,unsupportedScene));
            Assert.Throws<NotSupportedException>(()=>output.RenderGlass(material,unsupportedScene));
            Assert.False(output.TryRenderGlass(material with{Frosted=true},unsupportedScene with{Radius=26}));
            Assert.True(output.Present());
            var capture=WgcWindowCapture.Capture(hwnd,TimeSpan.FromSeconds(3))??throw new InvalidOperationException("Managed direct output was not captured.");
            var point=((capture.Height/4)*capture.Width+capture.Width/4)*4;
            var expected=new[]{20,40,80,102};for(var c=0;c<4;c++)Assert.InRange((int)capture.Pixels[point+c],expected[c]-1,expected[c]+1);
            Assert.Equal(2u,output.Statistics.BufferCount);Assert.Equal(0u,output.Statistics.D3D9ResourceCount);Assert.NotEqual(0,output.FrameLatencyHandle);
            Assert.IsAssignableFrom<InvalidOperationException>(Task.Run(()=>Record.Exception(()=>{output.Clear(Colors.Transparent);})).GetAwaiter().GetResult());
            output.Resize(initial.Width+10,initial.Height+10);Assert.Equal(1u,output.ImageStatistics.ImageCount);
            Assert.True(output.RemoveImage(1));Assert.False(output.RemoveImage(1));Assert.Equal(0ul,output.ImageStatistics.SourceBytes);
            output.Dispose();Assert.Throws<ObjectDisposedException>(()=>{output.Clear(Colors.Transparent);});
        }
        finally{host.Close();}
    });
}
