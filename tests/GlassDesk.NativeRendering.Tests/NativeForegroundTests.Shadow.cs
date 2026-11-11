using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassDesk.Dock;
using GlassDesk.Rendering;
using System.Windows.Interop;
using System.Windows.Media.Effects;

namespace GlassDesk.NativeRendering.Tests;

public sealed partial class NativeForegroundTests
{
    [Fact]
    public void NativeLibraryExposesGroupShadowWithoutReadback()
    {
        var library=NativeLibrary.Load(Api.LibraryPath);
        try{Assert.True(NativeLibrary.TryGetExport(library,"GD_PresenterApplyGroupShadow",out _),"Missing native group shadow compositor.");}
        finally{NativeLibrary.Free(library);}
    }
    [Theory]
    [InlineData(false,20.35,1.12,.6)]
    [InlineData(true,20.35,1.12,.6)]
    [InlineData(true,-12.35,1.12,.6)]
    [InlineData(true,135.35,.95,.8)]
    public void ProductionGhostHasNativeShadowIncludingOffWindowSource(bool withImage,double left,double scale,double opacity)=>NativeEngineTests.OnSta(()=>
    {
        BitmapSource? bitmap=null;
        if(withImage)
        {
            var pixels=Enumerable.Range(0,32*32).SelectMany(i=>{var a=i%32<4?90:255;return new byte[]{(byte)(i%32*6*a/255),(byte)(100*a/255),(byte)(220*a/255),(byte)a};}).ToArray();
            bitmap=BitmapSource.Create(32,32,96,96,PixelFormats.Pbgra32,null,pixels,128);bitmap.Freeze();
        }
        var model=new DockTileModel{Glyph="\uE8A5",Icon=bitmap};
        var strip=new DockTileStrip();
        var ghost=(Border)typeof(DockTileStrip).GetMethod("CreateGhost",BindingFlags.Instance|BindingFlags.NonPublic)!
            .Invoke(strip,[new Border{DataContext=model}])!;
        Assert.Equal(14,((System.Windows.Media.Effects.DropShadowEffect)ghost.Effect).BlurRadius);
        ghost.Opacity=opacity;ghost.RenderTransform=new ScaleTransform(scale,scale);ghost.RenderTransformOrigin=new(.5,.5);
        var canvas=new Canvas{Background=Brushes.Transparent};canvas.Children.Add(ghost);Canvas.SetLeft(ghost,left);Canvas.SetTop(ghost,15.2);
        var host=Host(canvas,160,120);host.Show();host.UpdateLayout();
        try{CompareNative($"ghost-{withImage}-{left}",host,canvas,64);}finally{host.Close();}
    });
    [Fact]
    public void PaddedPresenterPreservesUnmodifiedForegroundPixelCoordinates()=>NativeEngineTests.OnSta(()=>
    {
        var root=new Border{Background=Brushes.Red,Margin=new Thickness(3.35,5.2,2.1,4.3)};
        var host=Host(root,80,80);host.Show();host.UpdateLayout();
        try{CompareNative("padded-coordinate",host,root,32);}finally{host.Close();}
    });

    [Theory]
    [InlineData(0,0,315,.7,false)]
    [InlineData(0,0,315,0,false)]
    [InlineData(14,5,135,.45,true)]
    [InlineData(31,9,225,.65,true)]
    [InlineData(5.9,4.3,30,.8,false)]
    public void ShadowRetainsTintClipMaskAndNestedEffectOrdering(double radius,double depth,double direction,double opacity,bool masked)=>NativeEngineTests.OnSta(()=>
    {
        var leaf=new Border{Width=34.2,Height=29.6,Background=new SolidColorBrush(Color.FromArgb(190,240,40,70)),CornerRadius=new(8.3),
            Effect=new DropShadowEffect{BlurRadius=3,ShadowDepth=3,Direction=45,Color=Colors.Cyan,Opacity=.65}};
        var group=new Canvas{Width=56,Height=54,Opacity=.63,RenderTransform=new ScaleTransform(1.13,.94),
            Clip=new EllipseGeometry(new Rect(-13,-8,72,73)),
            Effect=new DropShadowEffect{BlurRadius=radius,ShadowDepth=depth,Direction=direction,Color=Color.FromArgb(100,40,180,230),Opacity=opacity}};
        if(masked)group.OpacityMask=new LinearGradientBrush(Colors.White,Color.FromArgb(70,255,255,255),0);
        group.Children.Add(leaf);Canvas.SetLeft(leaf,4.35);Canvas.SetTop(leaf,5.2);
        var root=new Canvas{Background=Brushes.Transparent,Clip=new RectangleGeometry(new Rect(0,0,160,120))};root.Children.Add(group);
        Canvas.SetLeft(group,35.35);Canvas.SetTop(group,26.2);
        var host=Host(root,160,120);host.Show();host.UpdateLayout();
        try{CompareNative($"shadow-order-{radius}-{opacity}-{masked}",host,root,96);}finally{host.Close();}
    });

    [Fact]
    public void ShadowPreflightRejectsUncoveredOrQualityEffectsWithoutUpload()=>NativeEngineTests.OnSta(()=>
    {
        var root=new Border{Width=48,Height=48,Background=Brushes.Red,Effect=new DropShadowEffect{BlurRadius=14}};
        var host=Host(root,80,80);host.Show();host.UpdateLayout();
        try
        {
            using var renderer=CreateRenderer(new WindowInteropHelper(host).Handle,100,100);
            var before=renderer.VectorStatistics;
            Assert.Throws<NotSupportedException>(()=>renderer.Prepare(VisualDrawingSnapshot.Capture(root),new(80,80)));
            Assert.Equal(before,renderer.VectorStatistics);
            var other=Host(null,80,80);other.Show();
            try
            {
                using var padded=CreatePaddedRenderer(new WindowInteropHelper(other).Handle,100,100,64);
                ((DropShadowEffect)root.Effect).RenderingBias=RenderingBias.Quality;
                Assert.Throws<NotSupportedException>(()=>padded.Prepare(VisualDrawingSnapshot.Capture(root),new(80,80)));
                Assert.Equal(0ul,padded.GroupEffectStatistics.TextureBytes);
            }
            finally{other.Close();}
        }
        finally{host.Close();}
    });

    [Fact]
    public void PaddedSiblingSurvivesParentDisposalAndResizeWithOriginalPixelOrigin()=>NativeEngineTests.OnSta(()=>
    {
        var root=new Border{Background=Brushes.Red,Margin=new Thickness(3.35,5.2,2.1,4.3)};
        var source=Host(root,80,80);var first=Host(null,80,80);var target=Host(null,80,80);
        source.Show();first.Show();target.Show();
        try
        {
            var expected=Capture(source);
            using var parent=CreatePaddedRenderer(new WindowInteropHelper(first).Handle,expected.Width,expected.Height,32);
            Assert.Throws<ArgumentOutOfRangeException>(()=>{using var invalid=parent.CreateSibling(new WindowInteropHelper(target).Handle,0,100);});
            Assert.Throws<ArgumentOutOfRangeException>(()=>{using var invalid=parent.CreateSibling(new WindowInteropHelper(target).Handle,100,-1);});
            using var child=parent.CreateSibling(new WindowInteropHelper(target).Handle,expected.Width,expected.Height);
            Assert.Equal(parent.DeviceStatistics.Identity,child.DeviceStatistics.Identity);
            using var scene=child.Prepare(VisualDrawingSnapshot.Capture(root),new(80,80));parent.Dispose();
            Assert.Equal(1u,child.DeviceStatistics.Presenters);
            Await(child);child.Clear(Colors.Transparent);child.Draw(scene);Assert.True(child.Present());
            ManagedVectorTests.Compare("padded-sibling",expected,Capture(target));
            source.Width=96;source.Height=88;target.Width=96;target.Height=88;root.Background=Brushes.Blue;
            expected=Capture(source);target.UpdateLayout();Capture(target);
            child.Resize(expected.Width,expected.Height);Assert.Throws<InvalidOperationException>(()=>child.Draw(scene));scene.Dispose();
            using var resized=child.Prepare(VisualDrawingSnapshot.Capture(root),new(96,88));
            Await(child);child.Clear(Colors.Transparent);child.Draw(resized);Assert.True(child.Present());
            ManagedVectorTests.Compare("padded-sibling-resized",expected,Capture(target));
        }
        finally{source.Close();first.Close();target.Close();}
    });
}
