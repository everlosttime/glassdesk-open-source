using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassDesk.Rendering;
using System.Runtime.CompilerServices;
using System.Reflection;
using GlassDesk.Dock;

namespace GlassDesk.NativeRendering.Tests;

public sealed partial class NativeForegroundTests
{
    private static RetainedVisualDrawingSource CreateLiveSource(Visual root,VisualSnapshotOptions? options=null)=>new(root,options);
    [Fact]
    public void RetainedSourcePoseCaptureAvoidsRepeatedLargeDrawingAndBoundsQueries()=>NativeEngineTests.OnSta(()=>
    {
        var path=new PathGeometry();var figure=new PathFigure{StartPoint=new(2,2),IsClosed=true};
        for(var i=0;i<2000;i++)figure.Segments.Add(new LineSegment(new(2+i%61,2+i%59),true));path.Figures.Add(figure);path.Freeze();
        var root=new DrawingReplay(new GeometryDrawing(Brushes.Red,null,path));var host=Host(root,80,80);host.Show();
        try
        {
            Capture(host);var source=CreateLiveSource(root);using var lifetime=(IDisposable)source;
            source.Track(root,(Func<ulong>)(()=>1));source.Capture();
            root.RenderTransform=new TranslateTransform(3.35,2.2);root.Opacity=.6;Capture(host);
            var before=GC.GetAllocatedBytesForCurrentThread();VisualDrawingSnapshot snapshot=source.Capture();var allocated=GC.GetAllocatedBytesForCurrentThread()-before;
            Assert.Equal(.6,snapshot.Root.Opacity);Assert.Equal(3.35,snapshot.Root.Transform.OffsetX);
            Assert.Equal(1ul,(ulong)source.Statistics.DrawingCaptures);Assert.Equal(1ul,(ulong)source.Statistics.DrawingReuses);
            Assert.InRange(allocated,0,32000);
            System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Api.LibraryPath)!,"retained-source-pose-allocation.json"),
                System.Text.Json.JsonSerializer.Serialize(new{allocated,segments=2000,stats=(object)source.Statistics}));
        }
        finally{host.Close();}
    });
    [Fact]
    public void RawDrawingChangeTracksMutableBrushWithoutRequiringAVisualRenderRevision()=>NativeEngineTests.OnSta(()=>
    {
        var brush=new SolidColorBrush(Colors.Red);var root=new Border{Background=brush};var host=Host(root,80,80);host.Show();
        try
        {
            Capture(host);var raw=VisualTreeHelper.GetDrawing(root)!;var changed=0;raw.Changed+=(_,_)=>changed++;
            brush.Color=Colors.Blue;Capture(host);
            Assert.True(changed>0,"GetDrawing does not propagate live resource changes; retained source must use another invalidation signal.");
        }
        finally{host.Close();}
    });

    [Fact]
    public void RetainedSourceRefreshesLivePaintExplicitContentAndUnknownNodes()=>NativeEngineTests.OnSta(()=>
    {
        var brush=new SolidColorBrush(Colors.Red);var border=new Border{Width=30,Height=30,Background=brush};
        var paint=new ChangingPaint{Width=24,Height=24};var root=new Canvas();root.Children.Add(border);root.Children.Add(paint);Canvas.SetLeft(paint,40);
        var host=Host(root,80,80);host.Show();ulong revision=0;
        try
        {
            Capture(host);using var source=CreateLiveSource(root);source.Track(border,()=>revision);
            source.Capture();source.Capture();var reused=source.Statistics.DrawingReuses;
            brush.Color=Colors.Blue;paint.Change(Brushes.Cyan);Capture(host);
            CompareNative("retained-source-live-paint",host,(Func<VisualDrawingSnapshot>)source.Capture);
            Assert.True(source.Statistics.Invalidations>0);Assert.True(source.Statistics.DrawingReuses>=reused);
            border.Background=Brushes.Green;revision++;Capture(host);
            CompareNative("retained-source-versioned-content",host,(Func<VisualDrawingSnapshot>)source.Capture);
            root.Children.Remove(border);source.Capture();Assert.Equal(0,source.Statistics.CachedVisuals);
        }
        finally{host.Close();}
    });

    [Fact]
    public void RetainedSourceObservesActualStripPressGlowWithoutContentRevision()=>NativeEngineTests.OnSta(()=>
    {
        var foreground=new UpdatingForeground();var host=Host(foreground.Root,280,140);host.Show();
        try
        {
            Capture(host);using var source=CreateLiveSource(foreground.Root);
            source.Track(foreground.Strip,()=>foreground.Strip.ForegroundDrawingVersion);source.Capture();source.Capture();
            var revision=foreground.Strip.ForegroundDrawingVersion;
            var glows=(List<RadialGradientBrush>)typeof(DockTileStrip).GetField("_pressGlows",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(foreground.Strip)!;
            glows[0].Opacity=.4;Capture(host);Assert.Equal(revision,foreground.Strip.ForegroundDrawingVersion);
            CompareNative("retained-source-strip-press-glow",host,(Func<VisualDrawingSnapshot>)source.Capture);
            Assert.True(source.Statistics.Invalidations>0);
        }
        finally{host.Close();}
    });

    [Fact]
    public void ReusedDrawingStillCountsAgainstBudgetsAndReparentingDepth()=>NativeEngineTests.OnSta(()=>
    {
        var leaf=new DrawingReplay(new GeometryDrawing(Brushes.Red,null,new RectangleGeometry(new Rect(2,2,12,12)))){Width=24,Height=24};
        var root=new Canvas();root.Children.Add(leaf);var host=Host(root,80,80);host.Show();
        try
        {
            Capture(host);using var source=CreateLiveSource(root,new(){MaximumDrawings=3,MaximumDepth=4});source.Track(leaf,()=>1);
            var first=source.Capture();var cached=source.Capture();Assert.Equal(first.DrawingCount,cached.DrawingCount);
            var second=new DrawingReplay(new GeometryDrawing(Brushes.Blue,null,new RectangleGeometry(new Rect(2,2,12,12)))){Width=24,Height=24};
            root.Children.Add(second);Capture(host);Assert.Throws<NotSupportedException>(()=>source.Capture());Assert.Equal(0,source.Statistics.CachedVisuals);
            root.Children.Remove(second);root.Children.Remove(leaf);
            var outer=new Canvas();var inner=new Canvas();outer.Children.Add(inner);inner.Children.Add(leaf);root.Children.Add(outer);Capture(host);
            Assert.Throws<NotSupportedException>(()=>source.Capture());
            inner.Children.Remove(leaf);root.Children.Clear();root.Children.Add(leaf);Capture(host);source.Capture();
            Assert.Equal(1,source.Statistics.CachedVisuals);source.Invalidate();Assert.Equal(0,source.Statistics.CachedVisuals);
        }
        finally{host.Close();}
    });

    [Fact]
    public void ReusedImagesAndGlyphsKeepSharedByteAndGlyphBudgets()=>NativeEngineTests.OnSta(()=>
    {
        static BitmapSource Bitmap(byte value)
        {
            var bitmap=BitmapSource.Create(4,4,96,96,PixelFormats.Pbgra32,null,Enumerable.Repeat(value,64).ToArray(),16);
            bitmap.Freeze();return bitmap;
        }
        var bitmap=Bitmap(255);var root=new Canvas();
        root.Children.Add(new Image{Width=16,Height=16,Source=bitmap});root.Children.Add(new Image{Width=16,Height=16,Source=bitmap});
        root.Children.Add(new TextBlock{Text="AB",FontSize=14,Foreground=Brushes.White});
        var host=Host(root,80,80);host.Show();
        try
        {
            Capture(host);using var source=CreateLiveSource(root,new(){MaximumBitmapBytes=64,MaximumGlyphs=2});source.Track(root,()=>1);
            source.Capture();var cached=source.Capture();Assert.Equal(64,cached.BitmapSourceBytes);Assert.Equal(2,cached.GlyphCount);
            var image=new Image{Width=16,Height=16,Source=Bitmap(128)};root.Children.Add(image);Capture(host);
            Assert.Throws<NotSupportedException>(()=>source.Capture());root.Children.Remove(image);
            root.Children.Add(new TextBlock{Text="C",FontSize=14,Foreground=Brushes.White});Capture(host);
            Assert.Throws<NotSupportedException>(()=>source.Capture());Assert.True(source.Statistics.DrawingReuses>0);
        }
        finally{host.Close();}
    });

    [Fact]
    public void OwnerCloseReleasesLiveRootCallbacksAndCachedDrawings()=>NativeEngineTests.OnSta(()=>
    {
        var (source,window)=ClosedLiveSource();
        try
        {
            global::Program.Pump(TimeSpan.FromMilliseconds(80));
            for(var i=0;i<3;i++){GC.Collect();GC.WaitForPendingFinalizers();}
            Assert.False(window.IsAlive);Assert.Equal(0,source.Statistics.CachedVisuals);
            Assert.Throws<ObjectDisposedException>(()=>source.Capture());
        }
        finally{source.Dispose();}
    });
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (RetainedVisualDrawingSource,WeakReference) ClosedLiveSource()
    {
        var root=new Border{Background=Brushes.Red};var host=Host(root,80,80);host.Show();Capture(host);
        var source=CreateLiveSource(root,new(){Exclude=visual=>ReferenceEquals(visual,host)});
        source.Track(root,()=>host.IsVisible?1ul:0ul);source.Capture();var weak=new WeakReference(host);host.Close();return(source,weak);
    }
}
