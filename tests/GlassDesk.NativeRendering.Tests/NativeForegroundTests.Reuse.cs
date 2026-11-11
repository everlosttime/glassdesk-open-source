using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassDesk.Rendering;

namespace GlassDesk.NativeRendering.Tests;

public sealed partial class NativeForegroundTests
{
    [Fact]
    public void FrozenPathSnapshotDoesNotAddAnotherLargeGraphCopyToWpfBoundsCost()=>NativeEngineTests.OnSta(()=>
    {
        var path=new PathGeometry();var figure=new PathFigure{StartPoint=new(2,2),IsClosed=true};
        for(var i=0;i<2000;i++)figure.Segments.Add(new LineSegment(new(2+i%61,2+i%59),true));path.Figures.Add(figure);path.Freeze();
        var root=new DrawingReplay(new GeometryDrawing(Brushes.Red,null,path));var host=Host(root,80,80);host.Show();
        try
        {
            Capture(host);VisualDrawingSnapshot.Capture(root);
            var intrinsicBefore=GC.GetAllocatedBytesForCurrentThread();
            _=VisualTreeHelper.GetDrawing(root);_=VisualTreeHelper.GetContentBounds(root);_=VisualTreeHelper.GetDescendantBounds(root);
            var publicWpfAllocated=GC.GetAllocatedBytesForCurrentThread()-intrinsicBefore;
            var before=GC.GetAllocatedBytesForCurrentThread();var snapshot=VisualDrawingSnapshot.Capture(root);
            var allocated=GC.GetAllocatedBytesForCurrentThread()-before;
            System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Api.LibraryPath)!,"frozen-path-snapshot-allocation.json"),
                System.Text.Json.JsonSerializer.Serialize(new{segments=2000,allocated,publicWpfAllocated,extra=allocated-publicWpfAllocated,snapshot.DrawingCount,snapshot.VisualCount,forcedGc=false}));
            Assert.InRange(allocated-publicWpfAllocated,0,64000);
        }
        finally{host.Close();}
    });
    [Fact]
    public void UnchangedScenesReuseSourcesAndSurviveEitherLeaseBeingReleased()=>NativeEngineTests.OnSta(()=>
    {
        var bitmap=BitmapSource.Create(16,16,96,96,PixelFormats.Pbgra32,null,Enumerable.Repeat((byte)255,1024).ToArray(),64);bitmap.Freeze();
        var root=new Canvas{Background=Brushes.Transparent};
        root.Children.Add(new Border{Width=20,Height=20,Background=Brushes.Red});
        var text=new TextBlock{Text="12:34",FontSize=18,Foreground=Brushes.White};Canvas.SetLeft(text,8);Canvas.SetTop(text,30);root.Children.Add(text);
        var image=new Image{Source=bitmap,Width=16,Height=16};Canvas.SetLeft(image,50);root.Children.Add(image);
        var source=Host(root,80,80);var target=Host(null,80,80);source.Show();target.Show();
        try
        {
            var expected=Capture(source);using var p=CreateRenderer(new WindowInteropHelper(target).Handle,expected.Width,expected.Height);
            var old=p.Prepare(VisualDrawingSnapshot.Capture(root),new(80,80));
            Await(p);p.Clear(Colors.Transparent);p.Draw(old);Assert.True(p.Present());ManagedVectorTests.Compare("reuse-old",expected,Capture(target));
            var vectors=p.VectorStatistics;var glyphs=p.GlyphStatistics;var images=p.ImageStatistics;
            using var same=p.Prepare(VisualDrawingSnapshot.Capture(root),new(80,80));
            Assert.Equal(vectors.NativeVectorCount,p.VectorStatistics.NativeVectorCount);Assert.Equal(vectors.GeometryBuilds,p.VectorStatistics.GeometryBuilds);
            Assert.Equal(glyphs.RunCount,p.GlyphStatistics.RunCount);Assert.Equal(images.ImageCount,p.ImageStatistics.ImageCount);
            old.Dispose();Await(p);p.Clear(Colors.Transparent);p.Draw(same);Assert.True(p.Present());
            ManagedVectorTests.Compare("reuse-same",expected,Capture(target));Assert.Equal(glyphs.Rasterizations,p.GlyphStatistics.Rasterizations);
            root.Opacity=.6;expected=Capture(source);
            using var faded=p.Prepare(VisualDrawingSnapshot.Capture(root),new(80,80));
            Assert.Equal(vectors.GeometryBuilds,p.VectorStatistics.GeometryBuilds);Assert.Equal(images.ImageCount,p.ImageStatistics.ImageCount);
            same.Dispose();Await(p);p.Clear(Colors.Transparent);p.Draw(faded);Assert.True(p.Present());ManagedVectorTests.Compare("reuse-faded",expected,Capture(target));
            Assert.Equal(glyphs.Rasterizations,p.GlyphStatistics.Rasterizations);faded.Dispose();
            Assert.Equal(0u,p.VectorStatistics.NativeVectorCount);Assert.Equal(0u,p.GlyphStatistics.RunCount);Assert.Equal(0u,p.ImageStatistics.ImageCount);
            Assert.Equal(0,p.ResourceStatistics.Resources);Assert.Equal(0,p.ResourceStatistics.DescriptorBytes);
        }
        finally{source.Close();target.Close();}
    });

    [Fact]
    public void GlyphScaleVariantsDoNotOverwriteRetainedSceneMasks()=>NativeEngineTests.OnSta(()=>
    {
        var text=new TextBlock{Text="12:34",FontSize=18,Foreground=Brushes.White};var root=new Canvas();root.Children.Add(text);Canvas.SetLeft(text,8);Canvas.SetTop(text,12);
        var source=Host(root,100,80);var target=Host(null,100,80);source.Show();target.Show();
        try
        {
            var expected=Capture(source);using var p=CreateRenderer(new WindowInteropHelper(target).Handle,expected.Width,expected.Height);
            using var old=p.Prepare(VisualDrawingSnapshot.Capture(root),new(100,80));
            text.RenderTransform=new ScaleTransform(1.2,1.2);var enlarged=Capture(source);
            using var next=p.Prepare(VisualDrawingSnapshot.Capture(root),new(100,80));Assert.Equal(2u,p.GlyphStatistics.RunCount);
            void Draw(NativeForegroundScene scene,CapturePixels reference,string name)
            {Await(p);p.Clear(Colors.Transparent);p.Draw(scene);Assert.True(p.Present());ManagedVectorTests.Compare(name,reference,Capture(target));}
            Draw(old,expected,"reuse-glyph-old");Draw(next,enlarged,"reuse-glyph-new");var rasters=p.GlyphStatistics.Rasterizations;
            Draw(old,expected,"reuse-glyph-retained");Draw(next,enlarged,"reuse-glyph-repeat");Assert.Equal(rasters,p.GlyphStatistics.Rasterizations);
        }
        finally{source.Close();target.Close();}
    });

    [Fact]
    public void DescriptorBudgetRejectsMissButAllowsHitsWithoutLosingLeases()=>NativeEngineTests.OnSta(()=>
    {
        var root=new Border{Background=Brushes.Red};var source=Host(root,80,80);var target=Host(null,80,80);source.Show();target.Show();
        try
        {
            Capture(source);using var p=CreateRenderer(new WindowInteropHelper(target).Handle,100,100);
            using var old=p.Prepare(VisualDrawingSnapshot.Capture(root),new(80,80));var stats=p.ResourceStatistics;
            var field=typeof(NativeForegroundPresenter).GetField("_descriptorBytes",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
            field.SetValue(p,8L*1024*1024);
            try
            {
                p.Prepare(VisualDrawingSnapshot.Capture(root),new(80,80)).Dispose();
                root.Background=Brushes.Blue;Capture(source);var before=p.VectorStatistics;
                Assert.Throws<NotSupportedException>(()=>p.Prepare(VisualDrawingSnapshot.Capture(root),new(80,80)));
                Assert.Equal(before,p.VectorStatistics);Assert.Equal(stats.References,p.ResourceStatistics.References);
            }
            finally{field.SetValue(p,stats.DescriptorBytes);}
            old.Dispose();Assert.Equal(0,p.ResourceStatistics.Resources);Assert.Equal(0,p.ResourceStatistics.DescriptorBytes);
        }
        finally{source.Close();target.Close();}
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChangedBitmapOrGradientKeepsBothScenesIndependent(bool gradient)=>NativeEngineTests.OnSta(()=>
    {
        BitmapSource Bitmap(byte blue,byte red)
        {var pixels=Enumerable.Range(0,16*16).SelectMany(_=>new byte[]{blue,20,red,255}).ToArray();var b=BitmapSource.Create(16,16,96,96,PixelFormats.Pbgra32,null,pixels,64);b.Freeze();return b;}
        var image=new Image{Source=Bitmap(0,220),Width=48,Height=48};
        var border=new Border{Width=48,Height=48,Background=new LinearGradientBrush(Colors.Red,Colors.Blue,0){Opacity=.45}};
        var root=new Canvas();root.Children.Add(gradient?border:image);var source=Host(root,80,80);var target=Host(null,80,80);source.Show();target.Show();
        try
        {
            var expected=Capture(source);using var p=CreateRenderer(new WindowInteropHelper(target).Handle,expected.Width,expected.Height);
            using var old=p.Prepare(VisualDrawingSnapshot.Capture(root),new(80,80));
            if(gradient)border.Background=new LinearGradientBrush(Colors.Cyan,Colors.Green,45){Opacity=.8};else image.Source=Bitmap(220,0);
            var changed=Capture(source);using var next=p.Prepare(VisualDrawingSnapshot.Capture(root),new(80,80));
            Assert.Equal(2,gradient?(int)p.VectorStatistics.NativeVectorCount:(int)p.ImageStatistics.ImageCount);
            Await(p);p.Clear(Colors.Transparent);p.Draw(next);Assert.True(p.Present());ManagedVectorTests.Compare("reuse-changed-"+gradient,changed,Capture(target));
            next.Dispose();Await(p);p.Clear(Colors.Transparent);p.Draw(old);Assert.True(p.Present());ManagedVectorTests.Compare("reuse-retained-"+gradient,expected,Capture(target));
            old.Dispose();Assert.Equal(0,p.ResourceStatistics.Resources);Assert.Equal(0,p.ResourceStatistics.DescriptorBytes);
        }
        finally{source.Close();target.Close();}
    });

    [Fact]
    public void ResizeRetainsGlyphSourceLeaseAndRebuildsOnlyItsTargetMask()=>NativeEngineTests.OnSta(()=>
    {
        var text=new TextBlock{Text="12:34",FontSize=18,Foreground=Brushes.White};var root=new Canvas();root.Children.Add(text);Canvas.SetLeft(text,8);Canvas.SetTop(text,12);
        var source=Host(root,80,80);var target=Host(null,80,80);source.Show();target.Show();
        try
        {
            var expected=Capture(source);using var p=CreateRenderer(new WindowInteropHelper(target).Handle,expected.Width,expected.Height);
            using var old=p.Prepare(VisualDrawingSnapshot.Capture(root),new(80,80));Await(p);p.Clear(Colors.Transparent);p.Draw(old);Assert.True(p.Present());
            var uploads=p.ResourceStatistics.Uploads;
            source.Width=96;target.Width=96;expected=Capture(source);Capture(target);p.Resize(expected.Width,expected.Height);
            using var next=p.Prepare(VisualDrawingSnapshot.Capture(root),new(96,80));
            Assert.Equal(uploads,p.ResourceStatistics.Uploads);Assert.Equal(1u,p.GlyphStatistics.RunCount);
            Assert.Throws<InvalidOperationException>(()=>p.Draw(old));old.Dispose();Await(p);p.Clear(Colors.Transparent);p.Draw(next);Assert.True(p.Present());
            ManagedVectorTests.Compare("reuse-resize-glyph",expected,Capture(target));next.Dispose();Assert.Equal(0,p.ResourceStatistics.Resources);
        }
        finally{source.Close();target.Close();}
    });
}
