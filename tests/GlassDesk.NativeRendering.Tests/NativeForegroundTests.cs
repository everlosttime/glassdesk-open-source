using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using GlassDesk.Rendering;
using GlassDesk.Dock;

namespace GlassDesk.NativeRendering.Tests;

public sealed partial class NativeForegroundTests
{
    [Theory]
    [InlineData(129.792020835909,30.228357207812405,49.69595832818199)]
    [InlineData(4.35,5.2,43.6)]
    [InlineData(5.13,7.27,32.14)]
    public void BitmapEdgesKeepWpfCoverageAtActualFractionalMagnification(double x,double y,double size)=>NativeEngineTests.OnSta(()=>
    {
        var bytes=Enumerable.Range(0,32*32).SelectMany(_=>new byte[]{40,100,220,255}).ToArray();
        var bitmap=System.Windows.Media.Imaging.BitmapSource.Create(32,32,96,96,PixelFormats.Pbgra32,null,bytes,128);bitmap.Freeze();
        var element=new BitmapReference(bitmap,new(x,y,size,size));RenderOptions.SetBitmapScalingMode(element,BitmapScalingMode.HighQuality);
        var host=Host(element,240,110);host.Show();host.UpdateLayout();
        try{CompareNative($"fractional-image-{x}",host,element);}finally{host.Close();}
    });
    private sealed class BitmapReference(System.Windows.Media.Imaging.BitmapSource bitmap,Rect rect):FrameworkElement
    {protected override void OnRender(DrawingContext dc)=>dc.DrawImage(bitmap,rect);}
    [Theory]
    [InlineData(false,TextFormattingMode.Ideal)]
    [InlineData(true,TextFormattingMode.Ideal)]
    [InlineData(true,TextFormattingMode.Display)]
    public void CompiledSceneKeepsOrderedOverlapsClipOpacityAndRealGlyphs(bool mask,TextFormattingMode mode)=>NativeEngineTests.OnSta(()=>
    {
        var root=new Canvas{Background=new SolidColorBrush(Color.FromRgb(11,20,30))};
        var group=new Canvas{Opacity=.7,Clip=Geometry.Parse("M15,6 C8,6 6,15 6,24 L6,54 C6,65 15,72 27,72 L55,72 C68,72 75,62 75,50 L75,23 C75,12 65,6 53,6 Z")};
        root.Children.Add(group);
        TextOptions.SetTextFormattingMode(root,mode);
        if(mask)group.OpacityMask=new LinearGradientBrush(Colors.White,Color.FromArgb(70,255,255,255),0);
        group.Children.Add(new System.Windows.Shapes.Path{Data=Geometry.Parse("M2.3,3.2 H59.3 V53.2 H2.3 Z"),Fill=new SolidColorBrush(Color.FromArgb(220,240,30,20))});
        var block=new TextBlock{Text="12:34",FontSize=18,Foreground=new SolidColorBrush(Color.FromArgb(180,220,240,255)),Opacity=.4};
        Canvas.SetLeft(block,8.35);Canvas.SetTop(block,45.2);group.Children.Add(block);
        var host=Host(root,80,80);host.Show();host.UpdateLayout();
        try{CompareNative($"mixed-{mask}-{mode}",host,root);}finally{host.Close();}
    });

    [Theory]
    [InlineData(0)]
    [InlineData(76)]
    public void ActualStripCompilesPaintedMagnificationScrollingFadesAndIndicators(double scroll)=>NativeEngineTests.OnSta(()=>
    {
        var pixels=Enumerable.Range(0,32*32).SelectMany(_=>new byte[]{40,100,220,255}).ToArray();
        var bitmap=System.Windows.Media.Imaging.BitmapSource.Create(32,32,96,96,PixelFormats.Pbgra32,null,pixels,128);bitmap.Freeze();
        var models=Enumerable.Range(0,7).Select(i=>new DockTileModel{EntryId="native-"+i,DisplayName="probe",Glyph="\uE8A5",Icon=i%2==0?bitmap:null,
            DotBrush=Brushes.White,ActiveBrush=Brushes.Cyan,RunningVisibility=Visibility.Visible,IsActive=i==2}).ToArray();
        var strip=new DockTileStrip();strip.SetTiles(models);strip.SetMetrics(48,8,1.36,12,220);
        var host=Host(strip,240,110);host.Show();host.UpdateLayout();strip.PaintFrame(100);strip.ScrollToHorizontalOffset(scroll);host.UpdateLayout();
        foreach(var border in Descendants(strip).OfType<Border>())if(border.Background is RadialGradientBrush glow)glow.Opacity=.46;
        try{CompareNative("strip-"+scroll,host,strip);}finally{host.Close();}
    });

    private static void CompareNative(string name,Window source,object sourceRoot,int padding=0)
    {
        var expected=Capture(source);var snapshot=sourceRoot is Func<VisualDrawingSnapshot> capture?capture():VisualDrawingSnapshot.Capture((Visual)sourceRoot);Inventory(name,snapshot);
        var metadata=Path.Combine(Path.GetDirectoryName(Api.LibraryPath)!,"foreground-parity",name+"-coordinates.json");
        File.WriteAllText(metadata,System.Text.Json.JsonSerializer.Serialize(new{source.ActualWidth,source.ActualHeight,expected.Width,expected.Height,
            dpiX=snapshot.Root.Dpi.DpiScaleX,dpiY=snapshot.Root.Dpi.DpiScaleY,offset=snapshot.Root.Offset.ToString(),bounds=snapshot.Root.DescendantBounds.ToString()}));
        var host=Host(null,source.ActualWidth,source.ActualHeight);host.Show();host.UpdateLayout();
        try
        {
            dynamic renderer=padding==0?CreateRenderer(new WindowInteropHelper(host).Handle,expected.Width,expected.Height):CreatePaddedRenderer(new WindowInteropHelper(host).Handle,expected.Width,expected.Height,padding);
            using var rendererLifetime=(IDisposable)renderer;
            dynamic scene=renderer.Prepare(snapshot,new Size(source.ActualWidth,source.ActualHeight));using var sceneLifetime=(IDisposable)scene;
            object boxedScene=scene;var plan=boxedScene.GetType().GetField("Plan",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(boxedScene)!;
            var commands=(System.Collections.IEnumerable)plan.GetType().GetField("Commands",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(plan)!;
            File.WriteAllLines(Path.ChangeExtension(metadata,"commands.txt"),commands.Cast<object>().Select(c=>c.ToString()!));
            Await(renderer);renderer.Clear(Colors.Transparent);renderer.Draw(scene);Assert.True((bool)renderer.Present());
            var actual=Capture(host);Save(name,expected,actual);ManagedVectorTests.Compare("foreground-"+name,expected,actual);
            var vectors=renderer.VectorStatistics;var glyphs=renderer.GlyphStatistics;var groups=renderer.GroupStatistics;var effects=renderer.GroupEffectStatistics;
            Await(renderer);renderer.Clear(Colors.Transparent);renderer.Draw(scene);Assert.True((bool)renderer.Present());
            ManagedVectorTests.Compare("foreground-repeat-"+name,expected,Capture(host));
            Assert.Equal((ulong)vectors.GeometryBuilds,(ulong)renderer.VectorStatistics.GeometryBuilds);
            Assert.Equal((ulong)vectors.CoverageBuilds,(ulong)renderer.VectorStatistics.CoverageBuilds);
            Assert.Equal((ulong)glyphs.Rasterizations,(ulong)renderer.GlyphStatistics.Rasterizations);
            Assert.Equal((ulong)groups.Allocations,(ulong)renderer.GroupStatistics.Allocations);
            Assert.Equal((ulong)effects.Allocations,(ulong)renderer.GroupEffectStatistics.Allocations);
        }
        finally{host.Close();}
    }
    private static NativeForegroundPresenter CreateRenderer(nint hwnd,int width,int height)=>new(hwnd,width,height);
    private static NativeForegroundPresenter CreatePaddedRenderer(nint hwnd,int width,int height,int padding)
    {
        var ctor=typeof(NativeForegroundPresenter).GetConstructor([typeof(nint),typeof(int),typeof(int),typeof(int)]);
        Assert.NotNull(ctor);return (NativeForegroundPresenter)ctor.Invoke([hwnd,width,height,padding]);
    }
    private static void Await(dynamic renderer)
    {
        var watch=System.Diagnostics.Stopwatch.StartNew();uint result;
        while((result=WaitForSingleObject((nint)renderer.FrameLatencyHandle,0))==258 && watch.Elapsed<TimeSpan.FromSeconds(3))global::Program.Pump(TimeSpan.FromMilliseconds(2));
        Assert.Equal(0u,result);
    }
    private static Window Host(FrameworkElement? content,double width,double height)=>new(){Content=content,Width=width,Height=height,Left=20,Top=20,
        WindowStyle=WindowStyle.None,AllowsTransparency=true,Background=Brushes.Transparent,ShowActivated=false,ShowInTaskbar=false};
    private static CapturePixels Capture(Window host)
    {host.UpdateLayout();global::Program.Pump(TimeSpan.FromMilliseconds(80));_=WgcWindowCapture.Capture(new WindowInteropHelper(host).Handle,TimeSpan.FromSeconds(3));return WgcWindowCapture.Capture(new WindowInteropHelper(host).Handle,TimeSpan.FromSeconds(3))!;}
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {yield return root;for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)foreach(var item in Descendants(VisualTreeHelper.GetChild(root,i)))yield return item;}
    private static void Save(string name,CapturePixels expected,CapturePixels actual)
    {var dir=Path.Combine(Path.GetDirectoryName(Api.LibraryPath)!,"foreground-parity");Directory.CreateDirectory(dir);File.WriteAllBytes(Path.Combine(dir,name+"-reference.bgra"),expected.Pixels);File.WriteAllBytes(Path.Combine(dir,name+"-native.bgra"),actual.Pixels);}
    private static void Inventory(string name,VisualDrawingSnapshot snapshot)
    {
        var paints=new List<string>();var guidelines=new List<string>();
        void Drawing(Drawing d){paints.Add(d.GetType().Name);if(d is DrawingGroup g){if(g.GuidelineSet is {} s)guidelines.Add($"drawing x={string.Join(',',s.GuidelinesX)} y={string.Join(',',s.GuidelinesY)} dynamic={typeof(GuidelineSet).GetProperty("IsDynamic",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)?.GetValue(s)}");foreach(var c in g.Children)Drawing(c);}else if(d is GeometryDrawing a)paints.Add($"{a.Brush?.GetType().Name}; pen={a.Pen?.Brush?.GetType().Name}/{a.Pen?.Thickness}");}
        void Node(VisualDrawingNode n){paints.Add($"Visual opacity={n.Opacity}; clip={n.Clip?.GetType().Name}; mask={n.OpacityMask?.GetType().Name}; effect={n.DropShadow?.GetType().Name}; text={n.TextFormattingMode}/{n.TextRenderingMode}/{n.TextHintingMode}");guidelines.Add($"{n.XSnappingGuidelines};{n.YSnappingGuidelines}");Drawing(n.Drawing);foreach(var c in n.Children)Node(c);}
        Node(snapshot.Root);var dir=Path.Combine(Path.GetDirectoryName(Api.LibraryPath)!,"foreground-parity");Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir,name+"-inventory.json"),System.Text.Json.JsonSerializer.Serialize(new{snapshot.VisualCount,snapshot.DrawingCount,paints,guidelines}));
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]private static extern uint WaitForSingleObject(nint handle,uint milliseconds);
}
