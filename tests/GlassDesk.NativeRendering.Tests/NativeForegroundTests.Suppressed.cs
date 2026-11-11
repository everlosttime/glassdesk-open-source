using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassDesk.Dock;
using GlassDesk.Rendering;
using GlassDesk.UI.Controls;

namespace GlassDesk.NativeRendering.Tests;

public sealed partial class NativeForegroundTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ContinuouslySuppressedSourceUpdatesRealStripGlyphTextAndDrawings(bool retained)=>NativeEngineTests.OnSta(()=>
    {
        var source=new UpdatingForeground();var reference=new UpdatingForeground();
        var carrier=new Grid{Opacity=0};carrier.Children.Add(source.Root);
        var host=Host(carrier,280,140);var expectedHost=Host(reference.Root,280,140);
        host.Show();expectedHost.Show();host.UpdateLayout();expectedHost.UpdateLayout();
        using var live=retained?new RetainedVisualDrawingSource(source.Root):null;
        live?.Track(source.Strip,()=>source.Strip.ForegroundDrawingVersion);
        var prefix=retained?"retained-source-":"suppressed-source-";
        var frames=new List<object>();NativeForegroundScene? previous=null;
        try
        {
            var empty=Capture(host);Assert.All(empty.Pixels.Where((_,i)=>i%4==3),alpha=>Assert.Equal(0,alpha));
            using var renderer=CreateRenderer(new WindowInteropHelper(host).Handle,empty.Width,empty.Height);
            var renderCount=source.Paint.Renders;
            for(var frame=0;frame<4;frame++)
            {
                source.Update(frame);reference.Update(frame);
                var expected=Capture(expectedHost); // Pumps both dispatchers; the source carrier stays at zero.
                Assert.Equal(0,carrier.Opacity);Assert.True(source.Paint.Renders>renderCount,"Suppressed source did not execute its invalidated OnRender.");
                renderCount=source.Paint.Renders;
                var allocated=GC.GetAllocatedBytesForCurrentThread();var timer=Stopwatch.StartNew();
                var snapshot=live?.Capture()??VisualDrawingSnapshot.Capture(source.Root);var captureMs=timer.Elapsed.TotalMilliseconds;
                timer.Restart();var scene=renderer.Prepare(snapshot,new(280,140));var prepareMs=timer.Elapsed.TotalMilliseconds;
                var compileAllocated=GC.GetAllocatedBytesForCurrentThread()-allocated;
                Inventory(prefix+frame,snapshot); // Keep diagnostic JSON/string allocation outside the measured segment.
                if(live is not null)
                {
                    var oracle=VisualDrawingSnapshot.Capture(source.Root);
                    Assert.Equal(oracle.VisualCount,snapshot.VisualCount);Assert.Equal(oracle.DrawingCount,snapshot.DrawingCount);
                    Assert.Equal(oracle.GlyphCount,snapshot.GlyphCount);Assert.Equal(oracle.BitmapSourceBytes,snapshot.BitmapSourceBytes);
                }
                try
                {
                    Await(renderer);renderer.Clear(Colors.Transparent);renderer.Draw(scene);Assert.True(renderer.Present());
                    var actual=Capture(host);Save(prefix+frame,expected,actual);
                    ManagedVectorTests.Compare(prefix+frame,expected,actual);
                    previous?.Dispose();previous=scene;
                    frames.Add(new{frame,captureMs,prepareMs,captureAndPrepareAllocatedBytes=compileAllocated,sourceRenders=renderCount,snapshot.VisualCount,snapshot.DrawingCount,
                        sourceCarrierOpacity=carrier.Opacity,renderer.VectorStatistics,renderer.ImageStatistics,renderer.GlyphStatistics,renderer.ResourceStatistics,renderer.CompileStatistics,sourceStatistics=live?.Statistics});
                }
                catch{scene.Dispose();throw;}
            }
            Assert.Equal(0,carrier.Opacity);
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(Api.LibraryPath)!,prefix+"updates.json"),
                System.Text.Json.JsonSerializer.Serialize(new{frames,inventoryExcludedFromAllocationSegment=true,sourceCapturedBelowExperimentalCarrier=true,
                    productionBackendSelected=false,clockDrivenFramesMeasured=false,wpfGpuSubmissionMeasured=false,displayedFpsMeasured=false,wholeProductMemoryMeasured=false}));
            previous?.Dispose();previous=null;
        }
        finally{previous?.Dispose();host.Close();expectedHost.Close();}
    });

    [Fact]
    public void SmallLaunchpadStrokeRetainsCompactWpfWidening()=>NativeEngineTests.OnSta(()=>
    {
        var glyph=new LaunchpadGlyph{Width=32,Height=32,OutlineBrush=Brushes.White};var root=new Canvas();root.Children.Add(glyph);
        Canvas.SetLeft(glyph,235);Canvas.SetTop(glyph,12);var host=Host(root,280,140);host.Show();
        try
        {
            var original=Capture(host);var drawing=VisualTreeHelper.GetDrawing(glyph)!;
            var replay=new DrawingReplay(drawing);var replayRoot=new Canvas();replayRoot.Children.Add(replay);Canvas.SetLeft(replay,235);Canvas.SetTop(replay,12);
            var replayHost=Host(replayRoot,280,140);replayHost.Show();
            try{var actual=Capture(replayHost);Save("small-launchpad-wpf-replay",original,actual);ManagedVectorTests.Compare("small-launchpad-wpf-replay",original,actual);}finally{replayHost.Close();}
            var observations=new List<object>();
            foreach(var (deviceSpace,tolerance,smooth) in new[]{(true,.25,false),(false,.2,false),(false,.25,false),(false,.01,false),(true,.25,true)})
            {
                var widened=new DrawingGroup();
                foreach(var geometry in drawing.Children.Cast<GeometryDrawing>())
                {
                    var shape=geometry.Geometry.CloneCurrentValue();var pen=geometry.Pen.CloneCurrentValue();
                    if(deviceSpace){shape.Transform=new MatrixTransform(1.25,0,0,1.25,235*1.25,12*1.25);pen.Thickness*=1.25;}
                    if(smooth){var path=PathGeometry.CreateFromGeometry(shape);foreach(var segment in path.Figures.SelectMany(f=>f.Segments))segment.IsSmoothJoin=true;shape=path;}
                    var outline=shape.GetWidenedPathGeometry(pen,tolerance,ToleranceType.Absolute);
                    outline.Transform=deviceSpace?new ScaleTransform(.8,.8):new TranslateTransform(235,12);
                    widened.Children.Add(new GeometryDrawing(pen.Brush,null,outline));
                }
                var widenedHost=Host(new DrawingReplay(widened),280,140);widenedHost.Show();
                try
                {
                    var actual=Capture(widenedHost);var difference=original.Pixels.Zip(actual.Pixels,(a,b)=>Math.Abs(a-b)).ToArray();
                    var maximum=difference.Max();Save($"small-launchpad-wpf-widened-{deviceSpace}-{tolerance}-{smooth}",original,actual);
                    observations.Add(new{deviceSpace,tolerance,smooth,maximum,mean=difference.Average()});
                }
                finally{widenedHost.Close();}
            }
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(Api.LibraryPath)!,"small-launchpad-widen-diagnosis.json"),System.Text.Json.JsonSerializer.Serialize(observations));
            CompareNative("small-launchpad-native",host,root);
        }
        finally{host.Close();}
    });
    private sealed class DrawingReplay(Drawing drawing):FrameworkElement
    {protected override void OnRender(DrawingContext dc)=>dc.DrawDrawing(drawing);}

    [Theory]
    [InlineData(1.2,2.333333333333333,PenLineJoin.Round,false)]
    [InlineData(1.2,2.333333333333333,PenLineJoin.Bevel,false)]
    [InlineData(100,2.333333333333333,PenLineJoin.Miter,false)]
    [InlineData(1.2,10,PenLineJoin.Round,false)]
    [InlineData(1.2,2.333333333333333,PenLineJoin.Round,true)]
    public void CompactRoundedRectangleStrokeKeepsRadiusClampDegenerateHoleAndDashFallback(double radius,double thickness,PenLineJoin join,bool dashed)=>NativeEngineTests.OnSta(()=>
    {
        var pen=new Pen(Brushes.White,thickness){LineJoin=join};if(dashed)pen.DashStyle=new DashStyle([1.2,.7],.35);
        var drawing=new GeometryDrawing(null,pen,new RectangleGeometry(new Rect(5.75,4.35,5.666666666666666,5.666666666666666),radius,radius));
        var root=new DrawingReplay(drawing);var host=Host(root,40,40);host.Show();host.UpdateLayout();
        try{CompareNative($"compact-stroke-{radius}-{thickness}-{join}-{dashed}",host,root);}finally{host.Close();}
    });

    private sealed class UpdatingForeground
    {
        internal readonly Canvas Root=new(){Background=Brushes.Transparent};
        internal readonly ChangingPaint Paint=new(){Width=24,Height=24};
        private readonly DockTileStrip _strip=new(){Width=220,Height=110};
        internal DockTileStrip Strip=>_strip;
        private readonly LaunchpadGlyph _glyph=new(){Width=32,Height=32,OutlineBrush=Brushes.White};
        private readonly TextBlock _text=new(){FontSize=13,Foreground=Brushes.White};
        private DockTileModel[] _models=[];
        internal UpdatingForeground()
        {
            Root.Children.Add(_strip);Root.Children.Add(_glyph);Root.Children.Add(Paint);Root.Children.Add(_text);
            Canvas.SetLeft(_glyph,235);Canvas.SetTop(_glyph,12);Canvas.SetLeft(Paint,240);Canvas.SetTop(Paint,70);
            Canvas.SetLeft(_text,230);Canvas.SetTop(_text,110);Tiles(false);_strip.SetMetrics(48,8,1.36,12,220);
        }
        private void Tiles(bool changed)
        {
            var bytes=Enumerable.Range(0,32*32).SelectMany(_=>changed?new byte[]{180,80,30,255}:new byte[]{40,100,220,255}).ToArray();
            var bitmap=BitmapSource.Create(32,32,96,96,PixelFormats.Pbgra32,null,bytes,128);bitmap.Freeze();
            _models=Enumerable.Range(0,7).Select(i=>new DockTileModel{EntryId="source-"+i,Glyph="\uE8A5",Icon=i%2==0?bitmap:null,
                DotBrush=Brushes.White,ActiveBrush=Brushes.Cyan,RunningVisibility=Visibility.Visible}).ToArray();_strip.SetTiles(_models);
        }
        internal void Update(int frame)
        {
            if(frame==2)Tiles(true);
            foreach(var model in _models){model.IsActive=model.EntryId=="source-"+frame;model.RunningVisibility=frame==3?Visibility.Collapsed:Visibility.Visible;}
            _strip.PaintFrame(30+60*frame);_strip.ScrollToHorizontalOffset(frame%2==0?0:76);
            _glyph.IsFilled=frame%2!=0;_text.Text=(1234+frame).ToString();Paint.Change(frame%2==0?Brushes.Red:Brushes.Cyan);
        }
    }
    private sealed class ChangingPaint:FrameworkElement
    {
        private Brush _brush=Brushes.White;
        internal int Renders{get;private set;}
        internal void Change(Brush brush){_brush=brush;InvalidateVisual();}
        protected override void OnRender(DrawingContext dc){Renders++;dc.DrawRoundedRectangle(_brush,null,new(0,0,24,24),4,4);}
    }
}
