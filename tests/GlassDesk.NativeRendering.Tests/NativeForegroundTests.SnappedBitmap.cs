using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GlassDesk.NativeRendering.Tests;

public sealed partial class NativeForegroundTests
{
    // Moving the texture with snapped coverage, or prefiltering at the snapped
    // size, changes this patterned image even when a solid icon looks correct.
    [Theory]
    [InlineData(32,1)]
    [InlineData(32,1.3)]
    [InlineData(128,1)]
    [InlineData(128,1.3)]
    [InlineData(128,1.3,false)]
    [InlineData(96,1.3)]
    [InlineData(256,1.3)]
    [InlineData(31,.37)]
    public void SnappedBitmapKeepsIndependentTextureSamplingAndCoverage(int sourceSize,double scale,bool snapped=true)=>NativeEngineTests.OnSta(()=>
    {
        var bitmap=PatternBitmap(sourceSize);
        var element=new SnappedBitmapReference(bitmap,snapped){RenderTransform=new MatrixTransform(scale,0,0,scale,13.35,7.27),Opacity=.67};
        RenderOptions.SetBitmapScalingMode(element,BitmapScalingMode.HighQuality);
        var host=Host(element,110,100);host.Show();host.UpdateLayout();
        try{CompareNative($"snapped-image-{sourceSize}-{scale}-{snapped}",host,element);}finally{host.Close();}
    });
    private static BitmapSource PatternBitmap(int sourceSize)
    {
        var bytes=new byte[sourceSize*sourceSize*4];
        for(var y=0;y<sourceSize;y++)for(var x=0;x<sourceSize;x++)
        {
            var at=(y*sourceSize+x)*4;var alpha=(byte)(64+(x*7+y*13)%192);
            bytes[at]=(byte)((x/3+y/5)%2*alpha);bytes[at+1]=(byte)(y*19%256*alpha/255);
            bytes[at+2]=(byte)(x*13%256*alpha/255);bytes[at+3]=alpha;
        }
        var bitmap=BitmapSource.Create(sourceSize,sourceSize,96,96,PixelFormats.Pbgra32,null,bytes,sourceSize*4);bitmap.Freeze();
        return bitmap;
    }
    private sealed class SnappedBitmapReference(BitmapSource bitmap,bool snapped):FrameworkElement
    {
        protected override void OnRender(DrawingContext dc)
        {
            if(snapped)dc.PushGuidelineSet(new GuidelineSet([4.35,47.95],[5.2,48.8]));
            dc.DrawImage(bitmap,new Rect(4.35,5.2,43.6,43.6));if(snapped)dc.Pop();
        }
    }
}
