using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassDesk.Rendering;

namespace GlassDesk.NativeRendering.Tests;

public sealed partial class DirectGroupTests
{
    [Theory]
    [InlineData(false,false,1f,false)]
    [InlineData(false,false,.7f,false)]
    [InlineData(false,false,.7f,true)]
    [InlineData(true,false,.7f,false)]
    [InlineData(false,true,.7f,false)]
    [InlineData(true,true,.7f,false)]
    public void GroupContainsOrderedVectorImageAndRealGlyphRunBeforeClipping(bool withImage,bool withText,float opacity,bool fullBounds)=>NativeEngineTests.OnSta(()=>
    {
        var pixels=Enumerable.Range(0,16*16).SelectMany(_=>new byte[]{32,64,96,128}).ToArray();
        var bitmap=BitmapSource.Create(16,16,96,96,PixelFormats.Pbgra32,null,pixels,64);bitmap.Freeze();
        var group=new Canvas{Opacity=opacity,Clip=Clip,Background=fullBounds?Brushes.Transparent:null};
        group.Children.Add(new System.Windows.Shapes.Path{Data=Red,Fill=new SolidColorBrush(RedColor)});
        var image=new Image{Source=bitmap,Width=32,Height=28,Stretch=Stretch.Fill};RenderOptions.SetBitmapScalingMode(image,BitmapScalingMode.HighQuality);
        Canvas.SetLeft(image,25.35);Canvas.SetTop(image,20.2);group.Children.Add(image);
        var ink=Color.FromArgb(180,220,240,255);var block=new TextBlock{Text="12:34",FontSize=18,Foreground=new SolidColorBrush(ink),Opacity=.4};
        Canvas.SetLeft(block,8.35);Canvas.SetTop(block,45.2);group.Children.Add(block);
        image.Visibility=withImage?Visibility.Visible:Visibility.Collapsed;block.Visibility=withText?Visibility.Visible:Visibility.Collapsed;
        var root=new Grid{Background=new SolidColorBrush(Background)};root.Children.Add(group);
        var first=DirectShadowTests.Host(80,80);first.Content=root;first.Show();first.UpdateLayout();var second=DirectShadowTests.Host(80,80);second.Show();
        try
        {
            var expected=Capture(first);var runs=withText?DirectTextTests.Glyphs(VisualTreeHelper.GetDrawing(block)).ToArray():Array.Empty<GlyphRun>();if(withText)Assert.NotEmpty(runs);
            var effectiveClip=VisualTreeHelper.GetClip(group);
            using var p=new NativeDirectPresenter(new WindowInteropHelper(second).Handle,expected.Width,expected.Height);
            var device=p.DeviceStatistics;
            p.SetVector(1,Red,new(80,80),RedColor);p.SetVector(3,effectiveClip!,new(80,80),Colors.White);p.SetImage(1,bitmap);
            for(var i=0;i<runs.Length;i++)p.SetGlyphRun((uint)i+1,runs[i]);
            var bounds=VisualTreeHelper.GetDescendantBounds(group);bounds.Intersect(effectiveClip!.Bounds);
            var scaleX=expected.Width/80d;var scaleY=expected.Height/80d;
            bounds=new(bounds.X*scaleX,bounds.Y*scaleY,bounds.Width*scaleX,bounds.Height*scaleY);bounds.Inflate(1,1);
            p.Clear(Background);p.PushGroup(opacity,bounds);Draw(p,1);if(withImage)p.DrawImages(new(80,80),[new(1,new(25.35,20.2,32,28))]);
            p.DrawGlyphRuns(new(80,80),runs.Select((r,i)=>new DirectGlyphDraw((uint)i+1,new(8.35+r.BaselineOrigin.X,45.2+r.BaselineOrigin.Y),ink,.4f)).ToArray());
            p.BeginGroupMask();Draw(p,3);p.PopGroup();Assert.True(p.Present());
            var actual=Capture(second);var name=$"groups-mixed-{withImage}-{withText}-{opacity}-{fullBounds}";Save(name,expected,actual);ManagedVectorTests.Compare(name,expected,actual);
            Assert.Equal(device.Identity,p.DeviceStatistics.Identity);Assert.Equal(device.LiveDevices,p.DeviceStatistics.LiveDevices);
        }
        finally{first.Close();second.Close();}
    });
}
