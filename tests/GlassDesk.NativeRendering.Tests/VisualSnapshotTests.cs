using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GlassDesk.Core.Models;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Dock;
using GlassDesk.Rendering;
using GlassDesk.UI.Controls;
using GlassDesk.UI.Theming;

namespace GlassDesk.NativeRendering.Tests;

public sealed partial class VisualSnapshotTests
{
    [Theory]
    [InlineData(TextFormattingMode.Ideal)]
    [InlineData(TextFormattingMode.Display)]
    public void SnapshotPreservesVisualTransformsClipsGroupOpacityAndPaintOrder(TextFormattingMode mode)=>NativeEngineTests.OnSta(()=>
    {
        var canvas=new Canvas{Width=240,Height=140};
        TextOptions.SetTextFormattingMode(canvas,mode);
        var group=new Canvas{Width=160,Height=100,Opacity=.45,Clip=new EllipseGeometry(new(80,50),72,43),
            OpacityMask=new LinearGradientBrush(Colors.White,Color.FromArgb(70,255,255,255),0),
            RenderTransform=new TransformGroup{Children={new ScaleTransform(1.12,1.12),new TranslateTransform(3.35,2.2)}}};
        Canvas.SetLeft(group,14.4);Canvas.SetTop(group,10.35);canvas.Children.Add(group);
        var red=new Border{Width=90,Height=70,Background=Brushes.Red};
        var blue=new Border{Width=90,Height=70,Background=Brushes.Blue};
        group.Children.Add(red);group.Children.Add(blue);Canvas.SetLeft(blue,48);Canvas.SetTop(blue,18);Panel.SetZIndex(red,4);
        var text=new TextBlock{Text="12:34 星期四",FontSize=18,Foreground=Brushes.White,Margin=new Thickness(20,30,0,0)};
        group.Children.Add(text);
        var host=Host(canvas,240,140);
        try
        {
            host.Show();host.UpdateLayout();var expected=Capture(host);
            var snapshot=VisualDrawingSnapshot.Capture(canvas);
            Record("visual-"+mode,snapshot);
            Assert.True(snapshot.Root.Drawing.IsFrozen);Assert.True(snapshot.VisualCount>3);Assert.True(snapshot.DrawingCount>3);
            Compare("visual-groups-"+mode,expected,Render(snapshot,240,140));
            red.Background=Brushes.Lime;group.Opacity=.8;Panel.SetZIndex(blue,8);text.Text="新排版 09:58";host.UpdateLayout();
            Compare("visual-detached-"+mode,expected,Render(snapshot,240,140));
            var current=Capture(host);var changed=VisualDrawingSnapshot.Capture(canvas);Compare("visual-updated-"+mode,current,Render(changed,240,140));
        }
        finally{host.Close();}
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualDockForegroundIncludesSlideClipAndIndependentDragOverlay(bool sliding)=>NativeEngineTests.OnSta(()=>
    {
        using var theme=ThemeService.Initialize(Dispatcher.CurrentDispatcher);
        theme.Update(new AppearanceSettings{PinBarsToDesktop=false,GlassMaterial=GlassMaterialMode.PlainGlass});
        var dock=new DockWindow();
        try
        {
            var glass=(LiquidGlassSurface)dock.FindName("GlassSurface");glass.IsBackdropActive=()=>false;glass.Visibility=Visibility.Collapsed;
            var strip=(DockTileStrip)typeof(DockWindow).GetField("_tileStrip",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(dock)!;
            strip.SetTiles([new DockTileModel{EntryId="ghost-probe",DisplayName="probe",Glyph="\uE8A5"}]);
            dock.Show();dock.UpdateLayout();SetWindowDisplayAffinity(new WindowInteropHelper(dock).Handle,0);
            var root=(Grid)dock.FindName("DockVisualRoot");
            if(sliding){root.RenderTransform=new TranslateTransform(0,13.35);root.Opacity=.67;}
            var overlay=(Canvas)dock.FindName("DockDragOverlay");
            var tile=Descendants(strip).OfType<Border>().Single(b=>b.DataContext is DockTileModel && b.Background is RadialGradientBrush);
            var ghost=(Border)typeof(DockTileStrip).GetMethod("CreateGhost",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(strip,[tile])!;
            Assert.IsType<DropShadowEffect>(ghost.Effect);
            ghost.Opacity=.6;ghost.RenderTransform=new ScaleTransform(1.12,1.12);ghost.RenderTransformOrigin=new(.5,.5);
            Canvas.SetLeft(ghost,3.35);Canvas.SetTop(ghost,9.2);overlay.Children.Add(ghost);
            dock.UpdateLayout();global::Program.Pump(TimeSpan.FromMilliseconds(100));
            // Direct WGC of the unmodified production HWND returns E_INVALIDARG
            // here. Use a live VisualBrush reference of its real foreground for
            // drawing parity; this deliberately makes no HWND visibility claim.
            var expected=MirrorForeground(dock);var snapshot=dock.CaptureForegroundSnapshot();
            Record("dock-"+sliding,snapshot);
            Assert.True(snapshot.VisualCount>15);Assert.True(snapshot.DrawingCount>10);
            Compare("dock-foreground-"+sliding,expected,Render(snapshot,dock.ActualWidth,dock.ActualHeight));
            ((DropShadowEffect)ghost.Effect).Opacity=0;
            Compare("dock-retained-shadow-"+sliding,expected,Render(snapshot,dock.ActualWidth,dock.ActualHeight));
            var current=MirrorForeground(dock);var changed=dock.CaptureForegroundSnapshot();
            Compare("dock-changed-shadow-"+sliding,current,Render(changed,dock.ActualWidth,dock.ActualHeight));
        }
        finally{dock.Close();}
    });

    [Theory]
    [InlineData(0)]
    [InlineData(76)]
    public void ActualTileStripCapturesMagnificationScrollingFadesAndLiveIndicators(double scroll)=>NativeEngineTests.OnSta(()=>
    {
        var pixels=Enumerable.Range(0,32*32).SelectMany(_=>new byte[]{40,100,220,255}).ToArray();
        var bitmap=BitmapSource.Create(32,32,96,96,PixelFormats.Pbgra32,null,pixels,128);bitmap.Freeze();
        var models=Enumerable.Range(0,7).Select(i=>new DockTileModel{EntryId="probe-"+i,DisplayName="probe",Glyph="\uE8A5",Icon=i%2==0?bitmap:null,
            DotBrush=Brushes.White,ActiveBrush=Brushes.Cyan,RunningVisibility=Visibility.Visible,IsActive=i==2}).ToArray();
        var strip=new DockTileStrip();strip.SetTiles(models);strip.SetMetrics(48,8,1.36,12,220);
        var host=Host(strip,240,110);
        try
        {
            host.Show();host.UpdateLayout();strip.PaintFrame(100);strip.ScrollToHorizontalOffset(scroll);host.UpdateLayout();
            foreach(var border in Descendants(strip).OfType<Border>())if(border.Background is RadialGradientBrush glow)glow.Opacity=.46;
            var expected=Capture(host);var snapshot=VisualDrawingSnapshot.Capture(strip);
            Record("strip-"+scroll,snapshot);
            Assert.True(snapshot.VisualCount>25);Compare("strip-"+scroll,expected,Render(snapshot,240,110));
            models[2].IsActive=false;models[0].RunningVisibility=Visibility.Collapsed;strip.PaintFrame(145);host.UpdateLayout();
            Compare("strip-retained-"+scroll,expected,Render(snapshot,240,110));
            Compare("strip-changed-"+scroll,Capture(host),Render(VisualDrawingSnapshot.Capture(strip),240,110));
        }
        finally{host.Close();}
    });

    [Fact]
    public void UnsupportedLiveEffectsAndBoundsFailBeforeMutatingSource()=>NativeEngineTests.OnSta(()=>
    {
        var root=new Border{Width=80,Height=60,Background=Brushes.Red};var host=Host(root,80,60);
        try
        {
            host.Show();host.UpdateLayout();root.Effect=new BlurEffect{Radius=3};
            Assert.Throws<NotSupportedException>(()=>VisualDrawingSnapshot.Capture(root));Assert.NotNull(root.Effect);
            root.Effect=null;root.CacheMode=new BitmapCache();Assert.Throws<NotSupportedException>(()=>VisualDrawingSnapshot.Capture(root));
            root.CacheMode=null;root.Background=new VisualBrush(new Border{Width=10,Height=10,Background=Brushes.White});Capture(host);
            Assert.Throws<NotSupportedException>(()=>VisualDrawingSnapshot.Capture(root));
            root.Background=Brushes.Red;host.UpdateLayout();Capture(host);
            Assert.Throws<NotSupportedException>(()=>VisualDrawingSnapshot.Capture(root,new(){MaximumDrawings=1}));
            Assert.Same(Brushes.Red,root.Background);
        }
        finally{host.Close();}
    });

    [Fact]
    public void SnapshotDoesNotRetainItsSourceWindow()=>NativeEngineTests.OnSta(()=>
    {
        var (snapshot,window)=CreateDetached();
        global::Program.Pump(TimeSpan.FromMilliseconds(100));
        for(var i=0;i<4 && window.IsAlive;i++){GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();}
        Assert.False(window.IsAlive);Assert.True(snapshot.Root.Drawing.IsFrozen);Assert.True(snapshot.DrawingCount>0);
        GC.KeepAlive(snapshot); // GC here tests references only; never a product-memory measurement.
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (VisualDrawingSnapshot,WeakReference) CreateDetached()
    {
        var root=new StackPanel{Width=80,Height=60};TextOptions.SetTextFormattingMode(root,TextFormattingMode.Display);
        var text=new TextBlock{Text="123 词",FontSize=12};
        text.SetBinding(TextBlock.ForegroundProperty,new System.Windows.Data.Binding("Foreground")
            {RelativeSource=new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor,typeof(Window),1)});
        root.Children.Add(text);
        root.Children.Add(new Border{Width=20,Height=20,Background=Brushes.Red,Effect=new DropShadowEffect{BlurRadius=4}});
        var bitmap=BitmapSource.Create(4,4,96,96,PixelFormats.Pbgra32,null,Enumerable.Repeat((byte)255,64).ToArray(),16);bitmap.Freeze();
        root.Children.Add(new Image{Width=16,Height=16,Source=bitmap});
        var host=Host(root,80,60);host.Foreground=new SolidColorBrush(Colors.White);host.Show();host.UpdateLayout();Capture(host);
        var snapshot=VisualDrawingSnapshot.Capture(root,new(){Exclude=visual=>ReferenceEquals(visual,host)});
        Assert.True(snapshot.GlyphCount>0);Assert.Equal(64,snapshot.BitmapSourceBytes);
        host.Close();return(snapshot,new WeakReference(host));
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {yield return root;for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)foreach(var child in Descendants(VisualTreeHelper.GetChild(root,i)))yield return child;}
    private static Window Host(FrameworkElement content,double width,double height)=>new(){Content=content,Width=width,Height=height,
        Left=20,Top=20,WindowStyle=WindowStyle.None,AllowsTransparency=true,Background=Brushes.Transparent,ShowActivated=false,ShowInTaskbar=false};
    private static CapturePixels Capture(Window host)
    {
        host.UpdateLayout();global::Program.Pump(TimeSpan.FromMilliseconds(80));
        var hwnd=new WindowInteropHelper(host).Handle;
        try
        {
            _=WgcWindowCapture.Capture(hwnd,TimeSpan.FromSeconds(3));
            return WgcWindowCapture.Capture(hwnd,TimeSpan.FromSeconds(3))??throw new InvalidOperationException("No snapshot reference frame.");
        }
        catch(COMException error)
        {
            GetWindowDisplayAffinity(hwnd,out var affinity);
            throw new InvalidOperationException($"{host.GetType().Name}: visible={host.IsVisible}/{IsWindowVisible(hwnd)}, size={host.ActualWidth}x{host.ActualHeight}, affinity={affinity}, style={GetWindowLongPtr(hwnd,-16):X}, ex={GetWindowLongPtr(hwnd,-20):X}, owner={GetWindow(hwnd,4):X}.",error);
        }
    }
    private static CapturePixels Render(VisualDrawingSnapshot snapshot,double width,double height)
    {var host=Host(new SnapshotElement(snapshot.Root),width,height);try{host.Show();host.UpdateLayout();return Capture(host);}finally{host.Close();}}
    private sealed class SnapshotElement:FrameworkElement
    {
        private readonly DrawingVisual _root;
        internal SnapshotElement(VisualDrawingNode node){_root=Rebuild(node);AddVisualChild(_root);}
        protected override int VisualChildrenCount=>1;
        protected override Visual GetVisualChild(int index)=>index==0?_root:throw new ArgumentOutOfRangeException(nameof(index));
        private static DrawingVisual Rebuild(VisualDrawingNode node)
        {
            var visual=new DrawingVisual{Transform=new MatrixTransform(node.Transform),Offset=node.Offset,Opacity=node.Opacity,
                Clip=node.Clip,OpacityMask=node.OpacityMask,Effect=node.DropShadow,
                XSnappingGuidelines=node.XSnappingGuidelines,YSnappingGuidelines=node.YSnappingGuidelines};
            RenderOptions.SetBitmapScalingMode(visual,node.BitmapScalingMode);RenderOptions.SetEdgeMode(visual,node.EdgeMode);
            RenderOptions.SetClearTypeHint(visual,node.ClearTypeHint);TextOptions.SetTextFormattingMode(visual,node.TextFormattingMode);
            TextOptions.SetTextRenderingMode(visual,node.TextRenderingMode);TextOptions.SetTextHintingMode(visual,node.TextHintingMode);
            using(var dc=visual.RenderOpen())dc.DrawDrawing(node.Drawing);
            foreach(var child in node.Children)visual.Children.Add(Rebuild(child));return visual;
        }
    }
    private static CapturePixels MirrorForeground(DockWindow dock)
    {
        var visual=(Visual)dock.Content;var size=new Rect(0,0,dock.ActualWidth,dock.ActualHeight);
        var brush=new VisualBrush(visual){Stretch=Stretch.None,ViewboxUnits=BrushMappingMode.Absolute,Viewbox=size,
            ViewportUnits=BrushMappingMode.Absolute,Viewport=size,AlignmentX=AlignmentX.Left,AlignmentY=AlignmentY.Top};
        var element=new LiveReference(brush,dock.Clip,dock.Opacity,size);var host=Host(element,size.Width,size.Height);
        try{host.Show();host.UpdateLayout();return Capture(host);}finally{host.Close();}
    }
    private sealed class LiveReference(Brush brush,Geometry clip,double opacity,Rect bounds):FrameworkElement
    {
        protected override void OnRender(DrawingContext dc)
        {dc.PushClip(clip);dc.PushOpacity(opacity);dc.DrawRectangle(brush,null,bounds);dc.Pop();dc.Pop();}
    }
    private static void Compare(string name,CapturePixels reference,CapturePixels actual)
    {
        Assert.True(reference.Pixels.Where((_,i)=>i%4==3).Count(a=>a>24)>30,"Empty reference is not evidence.");
        var output=Path.Combine(Path.GetDirectoryName(Api.LibraryPath)!,"visual-snapshot-parity");Directory.CreateDirectory(output);
        File.WriteAllBytes(Path.Combine(output,name+"-reference.bgra"),reference.Pixels);File.WriteAllBytes(Path.Combine(output,name+"-snapshot.bgra"),actual.Pixels);
        ManagedVectorTests.Compare("snapshot-"+name,reference,actual);
    }
    private static void Record(string name,VisualDrawingSnapshot snapshot)
    {
        var output=Path.Combine(Path.GetDirectoryName(Api.LibraryPath)!,"visual-snapshot-parity");Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output,name+"-scene.json"),System.Text.Json.JsonSerializer.Serialize(new
        {snapshot.VisualCount,snapshot.DrawingCount,snapshot.GlyphCount,snapshot.BitmapSourceBytes,
            frozen=snapshot.Root.Drawing.IsFrozen,wholeProductAcceptance=false,nativeBackendSelected=false,displayedFpsMeasured=false}));
    }
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(nint window,uint affinity);
    [DllImport("user32.dll")] private static extern bool GetWindowDisplayAffinity(nint window,out uint affinity);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtr(nint window,int index);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window,uint command);
}
