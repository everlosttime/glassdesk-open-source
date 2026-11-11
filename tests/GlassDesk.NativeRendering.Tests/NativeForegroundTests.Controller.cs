using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Models;
using GlassDesk.Dock;
using GlassDesk.Platform.Displays;
using GlassDesk.NativeRenderingProbe;
using GlassDesk.UI.Controls;
using GlassDesk.UI.Theming;

namespace GlassDesk.NativeRendering.Tests;

public sealed partial class NativeForegroundTests
{
    private static bool Controller(DockWindow dock,string method)
    {
        var operation=typeof(DockWindow).GetMethod(method);Assert.NotNull(operation);
        return operation.Invoke(dock,null) is true;
    }
    private static CapturePixels DockScreen(DockWindow dock)
    {
        dock.UpdateLayout();global::Program.Pump(TimeSpan.FromMilliseconds(100));
        var dpi=VisualTreeHelper.GetDpi(dock);
        return OwnedWindowMonitorCrop.Capture(new WindowInteropHelper(dock).Handle,(int)Math.Round(dock.ActualWidth*dpi.DpiScaleX),(int)Math.Round(dock.ActualHeight*dpi.DpiScaleY));
    }
    private sealed record ControllerWindowContract(long Window,long Owner,long Parent,long Style,long ExtendedStyle)
    {
        internal static ControllerWindowContract Read(DockWindow dock)
        {var hwnd=new WindowInteropHelper(dock).Handle;return new((long)hwnd,(long)ControllerGetWindow(hwnd,4),(long)ControllerGetParent(hwnd),(long)ControllerGetWindowLongPtr(hwnd,-16),(long)ControllerGetWindowLongPtr(hwnd,-20));}
    }
    [ControllerPixelTheory]
    [InlineData(1,false,false)]
    [InlineData(.67,false,false)]
    [InlineData(1,true,false)]
    [InlineData(1,false,true)]
    [InlineData(.67,false,true)]
    public void ActualDockControllerKeepsSameHwndPixelsApplicationOpacityAndLegacyRecovery(double opacity,bool sliding,bool material)=>NativeEngineTests.OnSta(()=>
    {
        using var theme=ThemeService.Initialize(Dispatcher.CurrentDispatcher);
        theme.Update(new AppearanceSettings{PinBarsToDesktop=false,GlassMaterial=GlassMaterialMode.PlainGlass,DockGlassTransparency=material?0:.9});
        var backing=Host(new Border{Background=Brushes.MidnightBlue},1000,400);backing.Left=100;backing.Top=120;backing.Topmost=true;backing.Show();
        var dock=new DockWindow{Left=120,Top=150,Opacity=opacity};ControllerGetCursorPos(out var originalCursor);DirectInputProbe.PointI? parked=null;
        try
        {
            var glass=(LiquidGlassSurface)dock.FindName("GlassSurface");glass.IsBackdropActive=()=>false;if(!material)glass.Visibility=Visibility.Collapsed;
            var bitmap=BitmapSource.Create(16,16,96,96,PixelFormats.Pbgra32,null,Enumerable.Range(0,256).SelectMany(_=>new byte[]{40,100,220,255}).ToArray(),64);bitmap.Freeze();
            var strip=(DockTileStrip)typeof(DockWindow).GetField("_tileStrip",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(dock)!;
            var models=Enumerable.Range(0,5).Select(i=>new DockTileModel{EntryId="controller-"+i,DisplayName="controller",Icon=i%2==0?bitmap:null,Glyph="\uE8A5",
                DotBrush=Brushes.White,ActiveBrush=Brushes.Cyan,RunningVisibility=Visibility.Visible}).ToArray();strip.SetTiles(models);
            dock.Show();dock.UpdateLayout();var root=(Grid)dock.FindName("DockVisualRoot");
            strip.SetMetrics(48,8,1.3,8,180);dock.UpdateLayout();
            if(sliding){root.RenderTransform=new TranslateTransform(0,13.35);root.Opacity=.6;}
            global::Program.Pump(TimeSpan.FromMilliseconds(100));
            var fixtureBounds=DisplayService.GetWindowPixelBounds(new WindowInteropHelper(dock).Handle)!.Value;
            Assert.True(ControllerSetWindowPos(new WindowInteropHelper(backing).Handle,new WindowInteropHelper(dock).Handle,
                fixtureBounds.X-20,fixtureBounds.Y-20,fixtureBounds.Width+40,fixtureBounds.Height+40,0x0210));
            parked=new(fixtureBounds.X-19,fixtureBounds.Y-19);
            var move=DirectInputProbe.MoveToOwned(parked.Value,new WindowInteropHelper(backing).Handle,new WindowInteropHelper(dock).Handle);
            while(!move.IsCompleted)global::Program.Pump(TimeSpan.FromMilliseconds(5));move.GetAwaiter().GetResult();
            var loop=typeof(DockWindow).GetField("_entryHoverLoop",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(dock)!;
            var active=loop.GetType().GetField("_active",BindingFlags.Instance|BindingFlags.NonPublic)!;var settle=System.Diagnostics.Stopwatch.StartNew();
            while((bool)active.GetValue(loop)! && settle.Elapsed<TimeSpan.FromSeconds(3))global::Program.Pump(TimeSpan.FromMilliseconds(10));
            Assert.False((bool)active.GetValue(loop)!);
            var expected=DockScreen(dock);var handle=new WindowInteropHelper(dock).Handle;var contract=ControllerWindowContract.Read(dock);
            Assert.True(Controller(dock,"StartNativeForegroundExperiment"),dock.NativeForegroundExperimentStatus.Failure);
            Assert.Equal(handle,new WindowInteropHelper(dock).Handle);Assert.Equal(opacity,dock.Opacity);Assert.Equal(sliding?.6:1,root.Opacity);
            Assert.Equal(contract,ControllerWindowContract.Read(dock));Assert.NotNull(dock.NativeForegroundExperimentStatus.Source);
            Assert.Equal(0,((UIElement)dock.FindName("ForegroundPresentationCarrier")).Opacity);
            var actual=DockScreen(dock);var name=$"dock-controller-{opacity}-{sliding}-{material}";Save(name,expected,actual);ManagedVectorTests.Compare(name,expected,actual);
            var glyph=(LaunchpadGlyph)dock.FindName("LauncherDefaultGlyph");glyph.IsFilled=!glyph.IsFilled;
            models[1].IsActive=true;strip.PaintFrame(80);strip.ScrollToHorizontalOffset(56);global::Program.Pump(TimeSpan.FromMilliseconds(80));
            var deadline=System.Diagnostics.Stopwatch.StartNew();
            while(!Controller(dock,"TryPresentNativeForegroundExperiment") && deadline.Elapsed<TimeSpan.FromSeconds(3))global::Program.Pump(TimeSpan.FromMilliseconds(5));
            Assert.True(deadline.Elapsed<TimeSpan.FromSeconds(3));var changed=DockScreen(dock);
            var running=dock.NativeForegroundExperimentStatus;
            Controller(dock,"StopNativeForegroundExperiment");var changedLegacy=DockScreen(dock);
            Save(name+"-mutated",changedLegacy,changed);ManagedVectorTests.Compare(name+"-mutated",changedLegacy,changed);
            Assert.Equal(opacity,dock.Opacity);Assert.Equal(sliding?.6:1,root.Opacity);Assert.Null(dock.RetainedForegroundStatistics);
            Assert.False(dock.NativeForegroundExperimentStatus.Active);Assert.Null(dock.NativeForegroundExperimentStatus.Source);
            Assert.Equal(contract,ControllerWindowContract.Read(dock));Assert.Null(dock.NativeForegroundExperimentStatus.Failure);
            Assert.True(Controller(dock,"StartNativeForegroundExperiment"));Controller(dock,"StopNativeForegroundExperiment");
            ManagedVectorTests.Compare(name+"-recovered",changedLegacy,DockScreen(dock));
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(Api.LibraryPath)!,name+"-state.json"),System.Text.Json.JsonSerializer.Serialize(new{opacity,sliding,material,contract,running,
                stopped=dock.NativeForegroundExperimentStatus,fixtureBounds,sameHwnd=true,physicalMonitorCrop=true,manuallyDriven=true,displayedFpsMeasured=false,wholeProductMemoryMeasured=false}));
        }
        finally
        {
            dock.Close();backing.Close();
            if(parked is { } point && ControllerGetCursorPos(out var current) && current.Equals(point))ControllerSetCursorPos(originalCursor.X,originalCursor.Y);
        }
    });

    [Fact]
    public void ActualDockControllerRejectsUnsupportedMaterialAndStopsWhenContentIsConcealed()=>NativeEngineTests.OnSta(()=>
    {
        using var theme=ThemeService.Initialize(Dispatcher.CurrentDispatcher);
        theme.Update(new AppearanceSettings{PinBarsToDesktop=false,GlassMaterial=GlassMaterialMode.PlainGlass});
        var dock=new DockWindow{Left=120,Top=150};
        try
        {
            var glass=(LiquidGlassSurface)dock.FindName("GlassSurface");glass.IsBackdropActive=()=>false;
            dock.Show();dock.UpdateLayout();global::Program.Pump(TimeSpan.FromMilliseconds(80));
            var unsupported=new BlurEffect{Radius=3};glass.Effect=unsupported;
            Assert.False(Controller(dock,"StartNativeForegroundExperiment"));Assert.Same(unsupported,glass.Effect);
            Assert.False(dock.NativeForegroundExperimentStatus.Active);Assert.NotNull(dock.NativeForegroundExperimentStatus.Failure);
            glass.Effect=null;global::Program.Pump(TimeSpan.FromMilliseconds(80));Assert.True(Controller(dock,"StartNativeForegroundExperiment"));
            var content=(UIElement)dock.Content;content.Visibility=Visibility.Hidden;
            Assert.False(dock.NativeForegroundExperimentStatus.Active);Assert.Equal(Visibility.Hidden,content.Visibility);
            Assert.Equal(1,((UIElement)dock.FindName("ForegroundPresentationCarrier")).Opacity);
            Assert.Null(dock.RetainedForegroundStatistics);content.Visibility=Visibility.Visible;
            global::Program.Pump(TimeSpan.FromMilliseconds(80));Assert.True(Controller(dock,"StartNativeForegroundExperiment"));
            var root=(Grid)dock.FindName("DockVisualRoot");root.Effect=unsupported;
            var deadline=System.Diagnostics.Stopwatch.StartNew();
            while(dock.NativeForegroundExperimentStatus.Active && deadline.Elapsed<TimeSpan.FromSeconds(3))
            {Controller(dock,"TryPresentNativeForegroundExperiment");global::Program.Pump(TimeSpan.FromMilliseconds(5));}
            Assert.False(dock.NativeForegroundExperimentStatus.Active);Assert.Same(unsupported,root.Effect);
            Assert.NotNull(dock.NativeForegroundExperimentStatus.Failure);Assert.Null(dock.NativeForegroundExperimentStatus.Source);
            root.Effect=null;global::Program.Pump(TimeSpan.FromMilliseconds(80));Assert.True(Controller(dock,"StartNativeForegroundExperiment"));
            dock.Opacity=0;Assert.False(dock.NativeForegroundExperimentStatus.Active);Assert.Equal(0,dock.Opacity);dock.Opacity=1;
            global::Program.Pump(TimeSpan.FromMilliseconds(80));Assert.True(Controller(dock,"StartNativeForegroundExperiment"));
            dock.Hide();Assert.False(dock.NativeForegroundExperimentStatus.Active);Assert.Null(dock.NativeForegroundExperimentStatus.Source);
        }
        finally{dock.Close();}
    });

    [Fact]
    public void ActualDockControllerResizeCloseAndSourceCallbacksRelease()=>NativeEngineTests.OnSta(()=>
    {
        using var theme=ThemeService.Initialize(Dispatcher.CurrentDispatcher);
        theme.Update(new AppearanceSettings{PinBarsToDesktop=false,GlassMaterial=GlassMaterialMode.PlainGlass});
        var weak=ClosedControllerWindow();global::Program.Pump(TimeSpan.FromMilliseconds(100));
        for(var i=0;i<3;i++){GC.Collect();GC.WaitForPendingFinalizers();}
        Assert.All(weak,reference=>Assert.False(reference.IsAlive));
    });
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] ClosedControllerWindow()
    {
        var dock=new DockWindow{Left=120,Top=150};
        try
        {
            ((LiquidGlassSurface)dock.FindName("GlassSurface")).IsBackdropActive=()=>false;
            dock.Show();dock.UpdateLayout();global::Program.Pump(TimeSpan.FromMilliseconds(100));Assert.True(dock.StartNativeForegroundExperiment());
            dock.SizeToContent=SizeToContent.Manual;dock.Width=224;dock.Height=115;dock.UpdateLayout();global::Program.Pump(TimeSpan.FromMilliseconds(80));
            var deadline=System.Diagnostics.Stopwatch.StartNew();while(!dock.TryPresentNativeForegroundExperiment() && deadline.Elapsed<TimeSpan.FromSeconds(3))global::Program.Pump(TimeSpan.FromMilliseconds(5));
            Assert.True(deadline.Elapsed<TimeSpan.FromSeconds(3));
            var weak=new[]{new WeakReference(dock),new WeakReference(typeof(DockWindow).GetField("_experimentalFrameSource",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(dock)!),
                new WeakReference(typeof(DockWindow).GetField("_experimentalForeground",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(dock)!)};
            dock.Close();Assert.False(dock.NativeForegroundExperimentStatus.Active);Assert.Null(dock.NativeForegroundExperimentStatus.Source);
            Assert.Throws<ObjectDisposedException>(()=>dock.StartNativeForegroundExperiment());return weak;
        }
        finally{dock.Close();}
    }
    [DllImport("user32.dll",EntryPoint="GetWindow")]private static extern nint ControllerGetWindow(nint hwnd,uint command);
    [DllImport("user32.dll",EntryPoint="GetParent")]private static extern nint ControllerGetParent(nint hwnd);
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")]private static extern nint ControllerGetWindowLongPtr(nint hwnd,int index);
    private sealed class ControllerPixelTheoryAttribute:TheoryAttribute
    {public ControllerPixelTheoryAttribute(){if(Environment.GetEnvironmentVariable("GLASSDESK_NATIVE_INPUT_ACCEPTANCE")!="1")Skip="Requires explicitly enabled pointer placement on owned backing for matched static physical frames.";}}
}
