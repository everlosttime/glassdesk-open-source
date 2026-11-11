using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Reflection;
using GlassDesk.Rendering;

namespace GlassDesk.NativeRendering.Tests;

public sealed partial class NativeForegroundTests
{
    [Fact]
    public void MovingLargeRetainedFillReusesCompiledShapeAndKeepsBothScenes()=>NativeEngineTests.OnSta(()=>
    {
        var path=new PathGeometry();var figure=new PathFigure{StartPoint=new(2,2),IsClosed=true};
        for(var i=0;i<2000;i++)figure.Segments.Add(new LineSegment(new(2+i%61,2+i%59),true));path.Figures.Add(figure);path.Freeze();
        var root=new DrawingReplay(new GeometryDrawing(Brushes.Red,null,path));var host=Host(root,80,80);var target=Host(null,80,80);host.Show();target.Show();
        try
        {
            var expected=Capture(host);using var source=new RetainedVisualDrawingSource(root);source.Track(root,()=>1);
            using var renderer=CreateRenderer(new WindowInteropHelper(target).Handle,expected.Width,expected.Height);
            using var old=renderer.Prepare(source.Capture(),new(80,80));var builds=renderer.VectorStatistics.GeometryBuilds;
            root.RenderTransform=new MatrixTransform(1.07,0,0,1.09,3.35,2.2);root.Opacity=.6;var moved=Capture(host);var snapshot=source.Capture();
            var before=GC.GetAllocatedBytesForCurrentThread();using var next=renderer.Prepare(snapshot,new(80,80));var allocated=GC.GetAllocatedBytesForCurrentThread()-before;
            var buildsAfter=renderer.VectorStatistics.GeometryBuilds;
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(Api.LibraryPath)!,"compiled-shape-pose-allocation.json"),
                System.Text.Json.JsonSerializer.Serialize(new{allocated,segments=2000,builds,buildsAfter}));
            Assert.Equal(builds,buildsAfter);Assert.InRange(allocated,0,64000);
            void Draw(NativeForegroundScene scene,CapturePixels reference,string name)
            {Await(renderer);renderer.Clear(Colors.Transparent);renderer.Draw(scene);Assert.True(renderer.Present());Save(name,reference,Capture(target));ManagedVectorTests.Compare(name,reference,Capture(target));}
            Draw(next,moved,"compiled-shape-moved");Draw(old,expected,"compiled-shape-old");
            old.Dispose();Draw(next,moved,"compiled-shape-survivor");next.Dispose();Assert.Equal(0,renderer.ResourceStatistics.Resources);
            Assert.Equal(0,renderer.CompileStatistics.Contexts);Assert.Equal(0,renderer.CompileStatistics.PayloadBytes);
        }
        finally{host.Close();target.Close();}
    });

    [Fact]
    public void PlacedVectorRejectsOversizedDeviceCoordinatesBeforeAcquiringResources()=>NativeEngineTests.OnSta(()=>
    {
        var root=new DrawingReplay(new GeometryDrawing(Brushes.Red,null,new RectangleGeometry(new Rect(2,2,12,12))));
        var host=Host(root,80,80);var target=Host(null,80,80);host.Show();target.Show();
        try
        {
            Capture(host);using var source=new RetainedVisualDrawingSource(root);source.Track(root,()=>1);
            using var renderer=CreateRenderer(new WindowInteropHelper(target).Handle,100,100);
            using var old=renderer.Prepare(source.Capture(),new(80,80));var resources=renderer.ResourceStatistics;
            root.RenderTransform=new ScaleTransform(20000,20000);Capture(host);
            Assert.Throws<ArgumentException>(()=>renderer.Prepare(source.Capture(),new(80,80)));
            Assert.Equal(resources,renderer.ResourceStatistics);
        }
        finally{host.Close();target.Close();}
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompiledContextTracksChangedGuidesStrokeTransformAndPaint(bool stroke)=>NativeEngineTests.OnSta(()=>
    {
        var shape=new RectangleGeometry(new Rect(5.2,7.3,36.4,30.5),3.2,3.2);shape.Freeze();
        var ink=new SolidColorBrush(Colors.Cyan);var pen=new Pen(Brushes.White,2.3);pen.Freeze();
        var group=new DrawingGroup{GuidelineSet=new GuidelineSet([5.2,41.6],[7.3,37.8])};
        group.Children.Add(new GeometryDrawing(stroke?null:ink,stroke?pen:null,shape));
        var root=new DrawingReplay(group);var host=Host(root,80,80);var target=Host(null,80,80);host.Show();target.Show();
        NativeForegroundScene? previous=null;CapturePixels? prior=null;
        try
        {
            Capture(host);using var source=new RetainedVisualDrawingSource(root);source.Track(root,()=>1);
            using var renderer=CreateRenderer(new WindowInteropHelper(target).Handle,100,100);
            void Draw(NativeForegroundScene scene,CapturePixels reference,string name)
            {Await(renderer);renderer.Clear(Colors.Transparent);renderer.Draw(scene);Assert.True(renderer.Present());var actual=Capture(target);Save(name,reference,actual);ManagedVectorTests.Compare(name,reference,actual);}
            for(var frame=0;frame<4;frame++)
            {
                if(frame==1)root.RenderTransform=new MatrixTransform(1.1,0,0,1.1,3.35,2.2);
                if(frame==2){group.GuidelineSet.GuidelinesX[0]=5.65;root.Clip=new RectangleGeometry(new Rect(0,0,34.2,60));}
                if(frame==3){ink.Color=Colors.Orange;root.Opacity=.6;}
                var expected=Capture(host);var snapshot=source.Capture();var next=renderer.Prepare(snapshot,new(80,80));
                if(previous is not null)Draw(previous,prior!,"compiled-context-retained-"+stroke+"-"+frame);
                Draw(next,expected,"compiled-context-"+stroke+"-"+frame);previous?.Dispose();previous=next;prior=expected;
                var hits=renderer.CompileStatistics.Hits;using(var same=renderer.Prepare(snapshot,new(80,80)))Assert.True(renderer.CompileStatistics.Hits>hits);
            }
            previous!.Dispose();previous=null;Assert.Equal(0,renderer.CompileStatistics.Payloads);Assert.Equal(0,renderer.CompileStatistics.Contexts);
        }
        finally{previous?.Dispose();host.Close();target.Close();}
    });

    [Fact]
    public void CompiledAliasesStayBoundedAndNumericBudgetCanFallBackWithoutLosingScenes()=>NativeEngineTests.OnSta(()=>
    {
        var drawing=new GeometryDrawing(Brushes.Red,null,new RectangleGeometry(new Rect(2,2,12,12)));
        var root=new DrawingReplay(drawing);var host=Host(root,80,80);var target=Host(null,80,80);host.Show();target.Show();
        try
        {
            Capture(host);using var source=new RetainedVisualDrawingSource(root);source.Track(root,()=>1);
            using var renderer=CreateRenderer(new WindowInteropHelper(target).Handle,100,100);
            var current=renderer.Prepare(source.Capture(),new(80,80));
            try
            {
                for(var i=0;i<10;i++)
                {
                    drawing.Geometry=new RectangleGeometry(new Rect(2,2,12,12));Capture(host);
                    var next=renderer.Prepare(source.Capture(),new(80,80));current.Dispose();current=next;
                    Assert.InRange(renderer.CompileStatistics.Contexts,1,4);Assert.Equal(1,renderer.CompileStatistics.Payloads);
                    Assert.Equal(1u,renderer.VectorStatistics.NativeVectorCount);
                }
                var stats=renderer.CompileStatistics;
                var cache=typeof(NativeForegroundPresenter).GetField("_compileCache",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(renderer)!;
                var bytes=cache.GetType().GetField("_bytes",BindingFlags.Instance|BindingFlags.NonPublic)!;bytes.SetValue(cache,8L*1024*1024);
                try
                {
                    drawing.Geometry=new RectangleGeometry(new Rect(2,2,16,12));var expected=Capture(host);
                    using var next=renderer.Prepare(source.Capture(),new(80,80));Assert.Equal(stats.Payloads,renderer.CompileStatistics.Payloads);
                    Await(renderer);renderer.Clear(Colors.Transparent);renderer.Draw(next);Assert.True(renderer.Present());
                    var actual=Capture(target);Save("compiled-budget-fallback",expected,actual);ManagedVectorTests.Compare("compiled-budget-fallback",expected,actual);
                }
                finally{bytes.SetValue(cache,stats.PayloadBytes);}
            }
            finally{current.Dispose();}
            Assert.Equal(0,renderer.CompileStatistics.Contexts);Assert.Equal(0,renderer.CompileStatistics.Payloads);Assert.Equal(0,renderer.CompileStatistics.PayloadBytes);
        }
        finally{host.Close();target.Close();}
    });
}
