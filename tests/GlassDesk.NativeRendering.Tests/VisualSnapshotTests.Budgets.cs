using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassDesk.Rendering;

namespace GlassDesk.NativeRendering.Tests;

public sealed partial class VisualSnapshotTests
{
    [Fact]
    public void ImmutableBitmapSourcesShareTheirBudgetAndLiveSourcesRequireFallback()=>NativeEngineTests.OnSta(()=>
    {
        var bitmap=BitmapSource.Create(4,4,96,96,PixelFormats.Pbgra32,null,Enumerable.Repeat((byte)255,64).ToArray(),16);bitmap.Freeze();
        var root=new Canvas();var first=new Image{Width=20,Height=20,Source=bitmap};var second=new Image{Width=20,Height=20,Source=bitmap};
        root.Children.Add(first);root.Children.Add(second);Canvas.SetLeft(second,24);var host=Host(root,80,60);
        try
        {
            host.Show();host.UpdateLayout();Capture(host);
            var snapshot=VisualDrawingSnapshot.Capture(root,new(){MaximumBitmapBytes=64});
            Assert.Equal(64,snapshot.BitmapSourceBytes);
            Assert.Throws<NotSupportedException>(()=>VisualDrawingSnapshot.Capture(root,new(){MaximumBitmapBytes=63}));
            Assert.Throws<NotSupportedException>(()=>VisualDrawingSnapshot.Capture(root,new(){MaximumVisuals=1}));
            Assert.Throws<NotSupportedException>(()=>VisualDrawingSnapshot.Capture(root,new(){MaximumDepth=1}));
            var live=new WriteableBitmap(bitmap);first.Source=live;Capture(host);
            Assert.Throws<NotSupportedException>(()=>VisualDrawingSnapshot.Capture(root));
            Assert.Same(live,first.Source);Assert.False(live.IsFrozen);
        }
        finally{host.Close();}
    });
}
