using GlassDesk.Rendering;
using GlassDesk.UI.Controls;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace GlassDesk.NativeRendering.Tests;

public sealed class ManagedVectorTests
{
    [Theory]
    [InlineData(48f,1f)]
    [InlineData(55.2f,.4f)]
    public void NormalPcStrokeMatchesProductionThroughTheManagedPath(float size,float opacity)=>NativeEngineTests.OnSta(()=>
    {
        var root=new Canvas();var path=new Path{Data=Geometry.Parse(DirectVectorTests.PcPath),Fill=Brushes.Transparent,Stroke=Brushes.White,StrokeThickness=3.5,
            StrokeLineJoin=PenLineJoin.Round,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round,Opacity=opacity,RenderTransform=new ScaleTransform(size/48,size/48)};
        root.Children.Add(path);Canvas.SetLeft(path,12.35);Canvas.SetTop(path,10.35);var reference=Capture(root);
        var host=DirectShadowTests.Host(80,80);host.Show();
        try
        {
            var hwnd=new WindowInteropHelper(host).Handle;using var p=new NativeDirectPresenter(hwnd,reference.Width,reference.Height);
            p.SetStrokeVector(1,Geometry.Parse(DirectVectorTests.PcPath),new(48,48),Colors.White,3.5f);
            p.Clear(Colors.Transparent);p.DrawVectors(new(80,80),[new(1,new(12.35,10.35,size,size),opacity)]);Assert.True(p.Present());
            Compare($"managed-pc-{size}-{opacity}",reference,WgcWindowCapture.Capture(hwnd,TimeSpan.FromSeconds(3))??throw new InvalidOperationException("PC output missing."));
        }
        finally{host.Close();}
    });

    [Theory]
    [InlineData(false,48f,1f)]
    [InlineData(false,55.2f,.4f)]
    [InlineData(true,48f,1f)]
    [InlineData(true,55.2f,.4f)]
    public void NormalLaunchpadVectorsMatchTheActualProductionGlyph(bool filled,float size,float opacity)=>NativeEngineTests.OnSta(()=>
    {
        var referenceRoot=new Canvas();var glyph=new LaunchpadGlyph{Width=size,Height=size,OutlineBrush=Brushes.White,IsFilled=filled,Opacity=opacity};
        referenceRoot.Children.Add(glyph);Canvas.SetLeft(glyph,12.25);Canvas.SetTop(glyph,10.25);
        var reference=Capture(referenceRoot);var host=DirectShadowTests.Host(80,80);host.Show();
        try
        {
            var hwnd=new WindowInteropHelper(host).Handle;
            using var p=new NativeDirectPresenter(hwnd,reference.Width,reference.Height);
            var inset=filled?4:5.75;var box=filled?12:8.5;var radius=filled?3.5:1.8;
            var geometry=new RectangleGeometry(new(inset,inset,box,box),radius,radius);
            if(filled)p.SetVector(1,geometry,new(48,48),Colors.White);else p.SetStrokeVector(1,geometry,new(48,48),Colors.White,3.5f);
            var items=Enumerable.Range(0,9).Select(i=>new DirectVectorDraw(1,new(12.25+i%3*14*size/48,10.25+i/3*14*size/48,size,size),opacity)).ToArray();
            p.Clear(Colors.Transparent);p.DrawVectors(new(80,80),items);Assert.True(p.Present());
            var actual=WgcWindowCapture.Capture(hwnd,TimeSpan.FromSeconds(3))??throw new InvalidOperationException("Normal vector output missing.");
            Compare($"launchpad-{filled}-{size}-{opacity}",reference,actual);
            var stats=p.VectorStatistics;Assert.Equal(1u,stats.AssetCount);Assert.Equal(9u,stats.NativeVectorCount);
            p.Clear(Colors.Transparent);p.DrawVectors(new(80,80),items);Assert.Equal(stats.GeometryBuilds,p.VectorStatistics.GeometryBuilds);
            Assert.True(p.RemoveVector(1));Assert.False(p.RemoveVector(1));Assert.Equal(0u,p.VectorStatistics.NativeVectorCount);
        }
        finally{host.Close();}
    });

    [Theory]
    [InlineData(5f,1f)]
    [InlineData(10.5f,.4f)]
    public void RunningMarkerMatchesTheProductionRoundedBorder(float width,float opacity)=>NativeEngineTests.OnSta(()=>
    {
        var root=new Canvas();var marker=new Border{Width=width,Height=5,CornerRadius=new(2.5),Background=Brushes.DeepSkyBlue,Opacity=opacity};
        root.Children.Add(marker);Canvas.SetLeft(marker,30.2);Canvas.SetTop(marker,55.35);
        var reference=Capture(root);var host=DirectShadowTests.Host(80,80);host.Show();
        try
        {
            var hwnd=new WindowInteropHelper(host).Handle;using var p=new NativeDirectPresenter(hwnd,reference.Width,reference.Height);
            p.SetVector(1,new RectangleGeometry(new(0,0,width,5),2.5,2.5),new(width,5),Colors.DeepSkyBlue);
            p.Clear(Colors.Transparent);p.DrawVectors(new(80,80),[new(1,new(30.2,55.35,width,5),opacity)]);Assert.True(p.Present());
            Compare($"marker-{width}-{opacity}",reference,WgcWindowCapture.Capture(hwnd,TimeSpan.FromSeconds(3))??throw new InvalidOperationException("Marker output missing."));
            Assert.IsAssignableFrom<InvalidOperationException>(Task.Run(()=>Record.Exception(()=>p.RemoveVector(1))).GetAwaiter().GetResult());
            Assert.Throws<KeyNotFoundException>(()=>p.DrawVectors(new(80,80),[new(99,new(0,0,5,5))]));
            p.Dispose();Assert.Throws<ObjectDisposedException>(()=>p.RemoveVector(1));
        }
        finally{host.Close();}
    });

    [Fact]
    public void StrokeCacheTracksDeviceTransformAndPreservesSourceSnapshot()=>NativeEngineTests.OnSta(()=>
    {
        var host=DirectShadowTests.Host(80,80);host.Show();
        try
        {
            using var p=new NativeDirectPresenter(new WindowInteropHelper(host).Handle,100,100);
            var source=Geometry.Parse(DirectVectorTests.PcPath).CloneCurrentValue();
            p.SetStrokeVector(1,source,new(48,48),Colors.White,3.5f);source.Transform=new TranslateTransform(500,500);
            DirectVectorDraw[] items=[new(1,new(12,10,48,48))];
            p.Clear(Colors.Transparent);p.DrawVectors(new(80,80),items);var builds=p.VectorStatistics.GeometryBuilds;
            items[0]=items[0] with{Opacity=.4f};p.DrawVectors(new(80,80),items);Assert.Equal(builds,p.VectorStatistics.GeometryBuilds);
            items[0]=items[0] with{Bounds=new(12.2,10.2,55.2,55.2)};p.DrawVectors(new(80,80),items);Assert.Equal(builds+1,p.VectorStatistics.GeometryBuilds);
            p.Resize(120,120);p.DrawVectors(new(80,80),items);Assert.Equal(builds+2,p.VectorStatistics.GeometryBuilds);
            Assert.Throws<NotSupportedException>(()=>p.DrawVectors(new(80,80),[new(1,new(12,10,48,40))]));
            Assert.Throws<NotSupportedException>(()=>p.SetVector(2,Geometry.Parse("M0,0 A10,10 0 0 0 20,20"),new(48,48),Colors.White));
        }
        finally{host.Close();}
    });

    internal static CapturePixels Capture(FrameworkElement content)
    {
        var host=DirectShadowTests.Host(80,80);host.Content=content;
        try{host.Show();host.UpdateLayout();return WgcWindowCapture.Capture(new WindowInteropHelper(host).Handle,TimeSpan.FromSeconds(3))??throw new InvalidOperationException("Production vector reference missing.");}
        finally{host.Close();}
    }
    internal static void Compare(string name,CapturePixels reference,CapturePixels actual)
    {
        Assert.Equal(reference.Width,actual.Width);Assert.Equal(reference.Height,actual.Height);
        var maximum=0;double sum=0;for(var i=0;i<reference.Pixels.Length;i++){var d=Math.Abs(reference.Pixels[i]-actual.Pixels[i]);maximum=Math.Max(maximum,d);sum+=d;}
        var directory=System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Api.LibraryPath)!,"vector-parity");System.IO.Directory.CreateDirectory(directory);
        System.IO.File.WriteAllText(System.IO.Path.Combine(directory,name+".json"),System.Text.Json.JsonSerializer.Serialize(new{reference.Width,reference.Height,maximum,mean=sum/reference.Pixels.Length}));
        Assert.True(sum/reference.Pixels.Length<1.5,$"Mean {sum/reference.Pixels.Length:F4}, max {maximum}.");Assert.InRange(maximum,0,8);
    }
}
